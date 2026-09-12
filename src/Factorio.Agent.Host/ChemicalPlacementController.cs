using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record ChemicalPlacementResult(string MachineId, IReadOnlyDictionary<string, double> DiscardedInputBuffers);

/// <summary>Repositions an unused solid-output chemical machine before committing incompatible fluid routes.</summary>
public sealed class ChemicalPlacementController(IGameClient game, IControllerJournal journal)
{
    public async Task<ChemicalPlacementResult> EnsureAsync(string machineId, NativeRecipe recipe, ProductionCatalog catalog,
        SpatialController controller, CancellationToken token)
    {
        string[] fluids = recipe.Ingredients.Where(i => i.DeterministicFluid).Select(i => i.Name).Distinct(StringComparer.Ordinal).ToArray();
        if (fluids.Length < 2 || recipe.Products.Any(p => !p.DeterministicItem)) return new(machineId, new Dictionary<string, double>());
        var spatial = new SpatialClient(game);
        var factory = new FactorySnapshotClient(game);
        string pipeItem = catalog.Items.Where(p => p.Value.PlaceEntityType == "pipe").OrderBy(p => p.Key, StringComparer.Ordinal).First().Key;
        var map = await spatial.CaptureAsync([pipeItem], 48, token);
        var stock = await factory.CaptureAsync(cancellationToken: token);
        RequireScope(map.Scope); RequireScope(stock.Scope);
        if (new MultiFluidSupplyPlanner().Find(map, stock, pipeItem, machineId, fluids, token) is not null) return new(machineId, new Dictionary<string, double>());
        ChemicalRelocationGuard.Inspect(stock, machineId, recipe);
        string item = catalog.Items.Where(p => p.Value.PlaceEntity == map.Entities.Single(e => e.Id == machineId).Name)
            .OrderBy(p => p.Key, StringComparer.Ordinal).First().Key;
        map = await spatial.CaptureAsync([pipeItem, item], 48, token);
        RequireScope(map.Scope);
        using var planningDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        planningDeadline.CancelAfter(TimeSpan.FromMinutes(5));
        var planning = Task.Run(() => new ConfiguredMachinePlacementPlanner().Find(map, stock, machineId, item, pipeItem, fluids,
            planningDeadline.Token), planningDeadline.Token);
        ConfiguredMachinePlacement placement;
        try
        {
            while (!planning.IsCompleted)
            {
                // Native waits retain the ordinary defense arbitration while the pure C# search runs.
                Completed(await controller.WorkAsync("wait", new { ticks = 30 }, 600, token: token));
            }
            placement = await planning ?? throw new InvalidOperationException("No jointly routable powered placement found in the bounded observed search.");
        }
        finally
        {
            await planningDeadline.CancelAsync();
            try { await planning; } catch (OperationCanceledException) { }
        }
        await controller.ApproachEntityAsync(machineId, map.Entities.Single(e => e.Id == machineId).Position, catalog, token);
        stock = await factory.CaptureAsync(cancellationToken: token);
        RequireScope(stock.Scope);
        var discardedInputBuffers = ChemicalRelocationGuard.Inspect(stock, machineId, recipe);
        var freshMap = await spatial.CaptureAsync([pipeItem, item], 48, token);
        RequireScope(freshMap.Scope);
        var vacant = freshMap with { Entities = freshMap.Entities.Where(e => e.Id != machineId && e.Id != freshMap.Actor.Id).ToArray() };
        if (!new SpatialCollisionField(vacant).PlacementClear(freshMap.Prototypes[freshMap.Items[item].EntityName],
                placement.Placement.Position, placement.Placement.Direction)
            || new MultiFluidSupplyPlanner().Find(ConfiguredMachinePlacementPlanner.Project(freshMap, machineId, placement.Placement),
                stock, pipeItem, ConfiguredMachinePlacementPlanner.PlannedId, fluids, token) is null)
            throw new InvalidOperationException("Observed geometry or supplies changed before relocation; preserve the existing machine.");
        await journal.AppendAsync("chemical-relocation-plan", new { machineId, item, recipe = recipe.Name, placement,
            stock.SnapshotId, stock.CollectedTick, discardedInputBuffers }, token);
        var mined = await controller.WorkAsync("mine", new { entityId = machineId, count = 1 }, 3600, token: token);
        Completed(mined);
        if (mined.Effects.GetProperty("product").GetString() != item || mined.Effects.GetProperty("produced").GetInt32() != 1)
            throw new InvalidDataException("Native mining did not prove recovery of exactly one chemical machine.");
        var afterMining = await factory.CaptureAsync(cancellationToken: token);
        RequireScope(afterMining.Scope);
        if (afterMining.Records.Any(r => r.Kind == "entity" && r.EntityId == machineId))
            throw new InvalidDataException("The mined chemical machine still appears in native factory evidence.");
        string replacementId = await new PoweredMachineController(game, journal).BuildAtAsync(item, placement.Placement,
            catalog, controller, token, placement.Supplies.SelectMany(s => s.Supply.Route.Pipes).ToArray());
        await controller.ApproachEntityAsync(replacementId, placement.Placement.Position, catalog, token);
        Completed(await controller.WorkAsync("set_recipe", new { entityId = replacementId, recipe = recipe.Name }, 600, token: token));
        var proof = await spatial.CaptureAsync([pipeItem, item], 48, token);
        RequireScope(proof.Scope);
        var replacement = proof.Entities.Single(e => e.Id == replacementId);
        var expected = ConfiguredMachinePlacementPlanner.Project(map, machineId, placement.Placement)
            .Entities.Single(e => e.Id == ConfiguredMachinePlacementPlanner.PlannedId);
        if (replacement.Power?.NetworkId is null || replacement.Power.NetworkId != proof.Entities.Single(e => e.Id == placement.PoleId).Power?.NetworkId
            || replacement.FluidConnections is null || expected.FluidConnections is null
            || replacement.FluidConnections.Count != expected.FluidConnections.Count
            || expected.FluidConnections.Any(p => !replacement.FluidConnections.Any(n => n.BoxIndex == p.BoxIndex
                && n.PortIndex == p.PortIndex && n.Position == p.Position && n.TargetPosition == p.TargetPosition
                && n.Filter == p.Filter && n.FlowDirection == p.FlowDirection && n.Type == p.Type)))
            throw new InvalidDataException("Native replacement ports or power differ from the projected configuration. Reconcile partial effects.");
        await journal.AppendAsync("chemical-relocation-result", new { machineId, replacementId, mined, discardedInputBuffers,
            proof.CollectedTick, replacement }, token);
        return new(replacementId, discardedInputBuffers);

        void RequireScope(ActorScope scope)
        {
            if (scope != catalog.Scope) throw new InvalidDataException("Actor scope changed during chemical placement.");
        }
        static void Completed(OperationReceipt receipt)
        {
            if (receipt.Status != "completed") throw new InvalidOperationException($"Chemical relocation action ended with {receipt.Status}; reconcile effects before retrying.");
        }
    }
}
