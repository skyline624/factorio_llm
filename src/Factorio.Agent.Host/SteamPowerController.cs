using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record SteamPowerResult(long StartTick, long EndTick, IReadOnlyDictionary<string, string> Entities,
    double GeneratedLastTick, double LoadEnergy, long NetworkId);

/// <summary>Constructs a first native steam supply and verifies fluid links and an actual powered consumer.</summary>
public sealed class SteamPowerController(IGameClient game, IControllerJournal journal)
{
    public async Task<SteamPowerResult> RunAsync(CancellationToken token = default, SteamPowerPlan? resume = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        token = deadline.Token;
        var equipment = new PowerEquipment("offshore-pump", "boiler", "steam-engine", "small-electric-pole", "inserter");
        var production = new ProductionController(game, journal);
        var executor = new ProductionGoalExecutor(game, journal);
        var spatial = new SpatialClient(game);
        var exploration = new ExplorationPlanner();
        ProductionState initial = await production.ObserveAsync(token);
        ProductionCatalog catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        RequireScope(catalog.Scope);
        resume?.ValidateRecovery(initial.Scope, equipment);
        if (resume is null && initial.Entities.Any(e => equipment.Items.Take(3).Any(i => catalog.Items[i].PlaceEntity == e.Name)))
            throw new InvalidOperationException("A power installation already exists. Reconcile its journal before installing another one.");
        await journal.AppendAsync("steam-power-start", new { initial, equipment }, token);
        foreach (string item in equipment.Items)
        {
            PlannedMachine? planned = resume?.Machines.Single(m => m.Item == item);
            if (planned is null || !initial.Entities.Any(e => e.Name == catalog.Items[item].PlaceEntity
                && e.Position.DistanceTo(planned.Placement.Position) < 0.01)) await executor.RunAsync(item, 1, token);
        }
        SpatialSnapshot map = await spatial.CaptureAsync(equipment.Items, 48, token);
        RequireScope(map.Scope);
        EntityGeometry boiler = map.Prototypes[map.Items[equipment.Boiler].EntityName];
        ProductionState prepared = await production.ObserveAsync(token);
        RequireScope(prepared.Scope);
        string fuel = catalog.Items.Where(p => p.Value.FuelValue > 0 && p.Value.FuelCategory is { } category
                && boiler.FuelCategories?.ContainsKey(category) == true
                && catalog.Mining.Values.Any(products => products.Any(product => product.Name == p.Key && product.DeterministicItem)))
            .OrderByDescending(p => prepared.Inventory.GetValueOrDefault(p.Key) > 0).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key).FirstOrDefault() ?? throw new InvalidOperationException("No supported boiler fuel source.");
        await executor.RunAsync(fuel, 5, token);
        await using var controller = new SpatialController(game, journal);
        SteamPowerPlan? plan = resume;
        for (int attempt = 0; plan is null && attempt < 64; attempt++)
        {
            map = await spatial.CaptureAsync(equipment.Items, 48, token);
            RequireScope(map.Scope);
            plan = new SteamPowerPlanner().Find(map, equipment);
            if (plan is not null) break;
            MapPosition frontier = exploration.Choose(map, "", catalog);
            await journal.AppendAsync("power-exploration", new { map.CollectedTick, frontier }, token);
            await controller.NavigateAsync(frontier, cancellationToken: token);
        }
        if (plan is null) throw new InvalidOperationException("No observed reachable shore supports the first steam installation within the search budget.");
        await journal.AppendAsync("steam-power-plan", plan, token);
        var installed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (PlannedMachine machine in plan.Machines)
        {
            await TravelAsync(machine.Placement.Position, 8);
            map = await spatial.CaptureAsync(equipment.Items, 48, token);
            RequireScope(map.Scope);
            SpatialEntity[] existing = map.Entities.Where(e => e.Name == map.Items[machine.Item].EntityName
                && e.Position.DistanceTo(machine.Placement.Position) < 0.01 && initial.Entities.Any(own => own.Id == e.Id)).ToArray();
            if (existing.Length > 0)
            {
                if (existing.Length != 1 || existing[0].Direction != machine.Placement.Direction)
                    throw new InvalidDataException("The observed machine differs from the recovery plan.");
                installed[machine.Role] = existing[0].Id;
                await journal.AppendAsync("power-machine-reused", new { machine, entityId = existing[0].Id }, token);
                continue;
            }
            MapPosition approach = new PlacementPlanner().FindApproach(new(map), machine.Item, machine.Placement)
                ?? throw new InvalidOperationException("No reachable construction approach outside the planned footprint.");
            await TravelAsync(approach, 0.2);
            map = await spatial.CaptureAsync(equipment.Items, 48, token);
            RequireScope(map.Scope);
            PlacementValidation validation = await spatial.ValidateAsync(map.Scope, machine.Item, [machine.Placement], token);
            if (!validation.Candidates[0].Allowed || !validation.Candidates[0].InReach)
                throw new InvalidOperationException($"Native {machine.Role} placement refused. Preserve partial construction and reconcile the plan.");
            OperationReceipt built = await controller.WorkAsync("build", new
            {
                item = machine.Item,
                machine.Placement.Position,
                machine.Placement.Direction
            }, 600, token: token);
            Completed(built);
            installed[machine.Role] = built.Effects.GetProperty("entityId").GetString()!;
            await journal.AppendAsync("power-machine-built", new { machine, entityId = installed[machine.Role] }, token);
        }
        map = await spatial.CaptureAsync(equipment.Items, 48, token);
        RequireScope(map.Scope);
        VerifyLink(map, installed["pump"], installed["boiler"], plan.WaterConnection);
        VerifyLink(map, installed["boiler"], installed["engine"], plan.SteamConnection);
        await controller.NavigateAsync(plan.Machines.Single(m => m.Role == "boiler").Placement.Position, 3, token);
        Completed(await controller.WorkAsync("insert", new { entityId = installed["boiler"], inventory = "fuel", item = fuel, count = 5 }, 600, token: token));
        for (int attempt = 0; attempt < 120; attempt++)
        {
            Completed(await controller.WorkAsync("wait", new { ticks = 60 }, 180, token: token));
            map = await spatial.CaptureAsync(equipment.Items, 48, token);
            RequireScope(map.Scope);
            VerifyLink(map, installed["pump"], installed["boiler"], plan.WaterConnection);
            VerifyLink(map, installed["boiler"], installed["engine"], plan.SteamConnection);
            ObservedPower? enginePower = map.Entities.Single(e => e.Id == installed["engine"]).Power;
            ObservedPower? loadPower = map.Entities.Single(e => e.Id == installed["load"]).Power;
            ObservedPower? polePower = map.Entities.Single(e => e.Id == installed["pole"]).Power;
            await journal.AppendAsync("power-measurement", new { map.CollectedTick, enginePower, loadPower, polePower }, token);
            if (enginePower?.GeneratedLastTick is > 0 && loadPower?.Energy is > 0 && polePower?.NetworkId is { } network
                && enginePower.NetworkId == network && loadPower.NetworkId == network)
            {
                var result = new SteamPowerResult(initial.Tick, map.CollectedTick, installed,
                    enginePower.GeneratedLastTick.Value, loadPower.Energy, network);
                await journal.AppendAsync("steam-power-result", result, token);
                return result;
            }
        }
        throw new TimeoutException("The installed machines did not establish measured electric generation and a powered load.");

        void RequireScope(ActorScope scope)
        {
            if (scope != initial.Scope) throw new InvalidOperationException("Actor scope changed during power installation; reconcile partial effects.");
        }

        async Task TravelAsync(MapPosition destination, double distance)
        {
            for (int segment = 0; segment < 64; segment++)
            {
                SpatialSnapshot current = await spatial.CaptureAsync(cancellationToken: token);
                RequireScope(current.Scope);
                if (current.Actor.Position.DistanceTo(destination) <= 24)
                {
                    await controller.NavigateAsync(destination, distance, token);
                    return;
                }
                await controller.NavigateAsync(exploration.Choose(current, "", catalog, destination), cancellationToken: token);
            }
            throw new InvalidOperationException("Travel to the planned power installation exhausted its segment budget.");
        }
    }

    private static void Completed(OperationReceipt receipt)
    {
        if (receipt.Status != "completed") throw new InvalidOperationException($"Power action {receipt.Kind} ended with {receipt.Status}: {receipt.Error?.Code}. Reconcile partial effects.");
    }

    private static void VerifyLink(SpatialSnapshot map, string source, string target, FluidConnectionPlacement planned)
    {
        ObservedFluidConnection? connection = map.Entities.Single(e => e.Id == source).FluidConnections?
            .SingleOrDefault(c => c.BoxIndex == planned.SourceBox && c.PortIndex == planned.SourcePort);
        if (connection is null || connection.TargetEntityId != target || connection.TargetBoxIndex != planned.TargetBox
            || connection.Position.DistanceTo(planned.SourcePosition) > 0.01 || connection.TargetPosition.DistanceTo(planned.TargetPosition) > 0.01)
            throw new InvalidDataException("Native fluid connection does not match the calculated installation. Reconcile the partial construction.");
    }
}
