using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Builds a native terrain-fluid source with a planned route, then verifies actual extraction and connectivity.</summary>
public sealed class OffshoreSupplyController(IGameClient game, IControllerJournal journal)
{
    public async Task<string?> PrepareJointSourceAsync(string targetId, string fluid, IReadOnlyList<string> inputs,
        ProductionCatalog catalog, SpatialController controller, CancellationToken token)
    {
        var spatial = new SpatialClient(game);
        var factory = new FactorySnapshotClient(game);
        string pipeItem = catalog.Items.Where(p => p.Value.PlaceEntityType == "pipe").OrderBy(p => p.Key, StringComparer.Ordinal).First().Key;
        var pumps = catalog.Items.Where(p => p.Value.PlaceEntityType == "offshore-pump").OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).ToArray();
        if (pumps.Length == 0 || pumps.Length > 15) throw new InvalidOperationException("No bounded native offshore equipment catalog is available.");
        var map = await spatial.CaptureAsync([pipeItem, .. pumps], 48, token);
        var stock = await factory.CaptureAsync(cancellationToken: token);
        RequireScope(map.Scope); RequireScope(stock.Scope);
        var choice = await ControllerPlanning.RunAsync(cancellation => pumps
            .Select(item => (Item: item, Plan: new OffshoreSupplyPlanner().FindJoint(map, stock, item, pipeItem, targetId, fluid, inputs, cancellation)))
            .FirstOrDefault(p => p.Plan is not null), controller, TimeSpan.FromMinutes(5), token);
        await journal.AppendAsync("offshore-joint-plan", new { targetId, fluid, choice.Item, choice.Plan, map.Scope, map.CollectedTick }, token);
        if (choice.Plan is null) return null;
        await new ProductionGoalExecutor(game, journal).RunAsync(choice.Item, 1, token);
        string sourceId = await new PoweredMachineController(game, journal).BuildAtAsync(choice.Item, choice.Plan.Pump, catalog, controller, token,
            choice.Plan.Supplies.SelectMany(s => s.Supply.Route.Pipes).Append(map.Entities.Single(e => e.Id == targetId).Position).ToArray());
        var proof = await spatial.CaptureAsync([pipeItem], 48, token);
        var available = await factory.CaptureAsync(cancellationToken: token);
        RequireScope(proof.Scope); RequireScope(available.Scope);
        if (!OffshoreSupplyPlanner.CanExtract(proof, sourceId, fluid)
            || new MultiFluidSupplyPlanner().Find(proof, available, pipeItem, targetId, inputs, token) is null)
            throw new InvalidOperationException("The new offshore source does not preserve all observed input routes; reconcile the constructed pump.");
        await journal.AppendAsync("offshore-joint-source", new { sourceId, targetId, fluid, proof.CollectedTick }, token);
        return sourceId;

        void RequireScope(ActorScope scope)
        {
            if (scope != catalog.Scope) throw new InvalidDataException("Actor changed during joint offshore supply construction.");
        }
    }

    public async Task ConnectAsync(string targetId, string fluid, ProductionCatalog catalog, SpatialController controller, CancellationToken token)
    {
        var spatial = new SpatialClient(game);
        var factory = new FactorySnapshotClient(game);
        string pipeItem = catalog.Items.Where(p => p.Value.PlaceEntityType == "pipe").OrderBy(p => p.Key, StringComparer.Ordinal).First().Key;
        var pumps = catalog.Items.Where(p => p.Value.PlaceEntityType == "offshore-pump").OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).ToArray();
        if (pumps.Length == 0 || pumps.Length > 15) throw new InvalidOperationException("No bounded native offshore equipment catalog is available.");
        var map = await spatial.CaptureAsync([pipeItem, .. pumps], 48, token);
        RequireScope(map.Scope);
        var choice = pumps.Select(item => (Item: item, Plan: new OffshoreSupplyPlanner().Find(map, item, pipeItem, targetId, fluid, token)))
            .FirstOrDefault(p => p.Plan is not null);
        if (choice.Plan is null) throw new InvalidOperationException("No observed shoreline supports a compatible offshore supply route.");
        await journal.AppendAsync("offshore-supply-plan", new { targetId, fluid, choice.Item, choice.Plan, map.Scope, map.CollectedTick }, token);
        await new ProductionGoalExecutor(game, journal).RunAsync(choice.Item, 1, token);
        string sourceId = await new PoweredMachineController(game, journal).BuildAtAsync(choice.Item, choice.Plan.Pump, catalog, controller, token,
            [map.Entities.Single(e => e.Id == targetId).Position]);
        await new PipeConnectionController(game, journal).RunAsync(sourceId, targetId, fluid, token);
        bool supplied = false;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            var snapshot = await factory.CaptureAsync(cancellationToken: token);
            RequireScope(snapshot.Scope);
            double amount = Math.Max(snapshot.FluidStockAt(sourceId, fluid), snapshot.FluidStockAt(targetId, fluid));
            await journal.AppendAsync("offshore-supply-observation", new { sourceId, fluid, amount, snapshot.SnapshotId, snapshot.CollectedTick }, token);
            if (amount > 0) { supplied = true; break; }
            var waited = await controller.WorkAsync("wait", new { ticks = 60 }, 180, token: token);
            if (waited.Status != "completed") throw new InvalidOperationException("Offshore extraction wait did not complete.");
        }
        if (!supplied) throw new InvalidOperationException("The constructed offshore pump did not establish native fluid output.");

        void RequireScope(ActorScope scope)
        {
            if (scope != catalog.Scope) throw new InvalidDataException("Actor changed during offshore supply construction.");
        }
    }
}
