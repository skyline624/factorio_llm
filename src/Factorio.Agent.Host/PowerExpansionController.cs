using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record PowerExpansionStep(string Kind, string AnchorBoilerId, IReadOnlyDictionary<string, string> Built,
    IReadOnlyList<string> Engines, long Tick);
public sealed record PowerExpansionResult(PowerBudget Before, PowerBudget After, double AdditionalPerTick,
    IReadOnlyList<PowerExpansionStep> Steps, IReadOnlyList<FactoryCell> PowerCells);

/// <summary>
/// Grows steam power with the factory. Native capacity and demand decide when to act; each step completes a boiler's
/// engines or adds a boiler with its engines beside the observed installation, joins them to the network and proves
/// generation. Every boiler becomes a chest-fed power cell so factory logistics keeps its coal stocked.
/// </summary>
public sealed class PowerExpansionController(IGameClient game, IControllerJournal journal, string directory)
{
    public const int MaximumSteps = 4;
    /// <summary>Power cells live beside the steam installation, outside any factory band.</summary>
    public const int PowerZone = 0;
    private const string Fuel = "coal";

    public async Task<PowerState> ObserveAsync(CancellationToken token) =>
        PowerState.Parse(await game.ExecuteAsync(GameRequest.Create("power_state"), token));

    /// <summary>Energy per tick that new cells will draw, from native prototype usage of the machine and its two inserters.</summary>
    public static double CellDemand(SpatialSnapshot map, CellEquipment equipment, bool io) =>
        Energy(map, equipment.Machine) + (io ? 2 * Energy(map, equipment.Inserter) : 0);

    /// <summary>Before adding cells: feeds every boiler from a chest, then expands while demand would pass 80 % of capacity.</summary>
    public async Task<PowerExpansionResult?> EnsureCapacityForCellsAsync(string machineItem, int cells, bool io, CancellationToken token)
    {
        if (cells <= 0) return null;
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var equipment = FactoryCellBuilder.Equipment(catalog, machineItem);
        var map = await new SpatialClient(game).CaptureAsync([equipment.Machine, equipment.Inserter], 4, token);
        return await RunAsync(cells * CellDemand(map, equipment, io), false, token);
    }

    public Task<PowerExpansionResult?> EnsureCapacityAsync(double additionalPerTick, CancellationToken token) =>
        RunAsync(additionalPerTick, false, token);

    /// <summary>One unconditional expansion step, for an operator who asks for more power.</summary>
    public async Task<PowerExpansionResult> ExpandAsync(CancellationToken token) =>
        await RunAsync(0, true, token) ?? throw new InvalidOperationException("No observed steam network can be expanded.");

    private async Task<PowerExpansionResult?> RunAsync(double additionalPerTick, bool force, CancellationToken token)
    {
        if (!double.IsFinite(additionalPerTick) || additionalPerTick < 0) throw new ArgumentOutOfRangeException(nameof(additionalPerTick));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(60));
        token = deadline.Token;
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var state = await ObserveAsync(token);
        Require(state.Scope, catalog);
        var network = state.Main();
        if (network is null || Boilers(state, network).Count == 0)
        {
            // Without an observed steam installation there is nothing to grow; the first supply is built elsewhere.
            await journal.AppendAsync("power-expansion-unavailable", new { state.CollectedTick, networks = state.Networks.Count, additionalPerTick }, token);
            return null;
        }
        var before = state.Budget(network);
        await journal.AppendAsync("power-budget", new { before, additionalPerTick, state.CollectedTick }, token);
        await using var controller = new SpatialController(game, journal);
        var cells = await EnsureFeedersAsync(state, network, catalog, controller, token);
        var steps = new List<PowerExpansionStep>();
        var budget = before;
        while (steps.Count < MaximumSteps && (force && steps.Count == 0 || budget.Exceeded(additionalPerTick)))
        {
            var step = await StepAsync(state, network, catalog, controller, token);
            if (step is null)
            {
                await journal.AppendAsync("power-expansion-no-site", new { budget, additionalPerTick }, token);
                if (force && steps.Count == 0) throw new InvalidOperationException("No clear site extends the observed steam installation.");
                break;
            }
            steps.Add(step);
            state = await ObserveAsync(token);
            Require(state.Scope, catalog);
            network = state.Main() ?? throw new InvalidDataException("The expanded network is no longer observed.");
            cells = await EnsureFeedersAsync(state, network, catalog, controller, token);
            budget = state.Budget(network);
            await journal.AppendAsync("power-budget", new { budget, additionalPerTick, state.CollectedTick, step.Kind }, token);
        }
        var result = new PowerExpansionResult(before, budget, additionalPerTick, steps, cells);
        await journal.AppendAsync("power-expansion-result", result, token);
        return result;
    }

    private async Task<PowerExpansionStep?> StepAsync(PowerState state, ElectricNetworkState network, ProductionCatalog catalog,
        SpatialController controller, CancellationToken token)
    {
        var anchor = Boilers(state, network).OrderBy(b => b.Id, StringComparer.Ordinal).First();
        var items = Items(catalog, state, network);
        await controller.TravelAsync(anchor.Position, 6, catalog, token);
        var map = await CaptureAsync(items, catalog, token);
        string force = map.Entities.Single(e => e.Id == map.Actor.Id).Force;
        var planner = new PowerExpansionPlanner();
        var plan = planner.Next(await PlanningAsync(map, items, catalog, token), items.Boiler, items.Engine, force);
        if (plan is null) return null;
        await journal.AppendAsync("power-expansion-plan", new { plan, map.CollectedTick }, token);
        foreach (var group in plan.Machines.GroupBy(m => m.Item)) await EnsureItemsAsync(group.Key, group.Count(), token);
        var boxes = plan.Machines.Select(m => Footprint(map, m.Item, m.Placement)).ToArray();
        await ClearAsync(boxes, items, catalog, controller, token);

        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (machine, index) in plan.Machines.Select((m, i) => (m, i)))
        {
            ids[machine.Role] = await new PoweredMachineController(game, journal).BuildAtAsync(machine.Item, machine.Placement, catalog, controller, token,
                plan.Machines.Skip(index + 1).Select(m => m.Placement.Position).ToArray());
            await journal.AppendAsync("power-machine-built", new { machine, entityId = ids[machine.Role] }, token);
        }
        string Id(string reference) => ids.GetValueOrDefault(reference, reference);
        map = await CaptureAsync(items, catalog, token);
        foreach (var link in plan.Links) SteamPowerController.VerifyLink(map, Id(link.Source), Id(link.Target), link.Connection);
        await journal.AppendAsync("power-fluid-links", new { plan.Links, ids, map.CollectedTick }, token);

        if (ids.TryGetValue("boiler", out var boilerId)) await StartBoilerAsync(boilerId, map.Entities.Single(e => e.Id == boilerId).Position, catalog, controller, token);
        // A source that already powers the main network nearby defines the network the new engines must join.
        string reference = map.Entities.Where(e => network.Sources.Any(s => s.Id == e.Id) && !ids.ContainsValue(e.Id))
            .OrderBy(e => e.Position.DistanceTo(anchor.Position)).Select(e => e.Id).FirstOrDefault()
            ?? throw new InvalidDataException("No source of the main network is observed near the expansion.");
        var engines = plan.Machines.Where(m => m.Role.StartsWith("engine", StringComparison.Ordinal)).Select(m => ids[m.Role]).ToArray();
        foreach (var engine in engines) await LinkAsync(engine, reference, items, catalog, controller, token);
        long tick = await ProveGenerationAsync(engines, reference, items, catalog, controller, token);
        return new(plan.Kind, plan.BoilerId, ids, engines, tick);
    }

    /// <summary>Joins an observed consumer or generator to the reference entity's network with the fewest native pole links.</summary>
    internal async Task LinkAsync(string targetId, string referenceId, SteamItems items, ProductionCatalog catalog,
        SpatialController controller, CancellationToken token)
    {
        for (int link = 0; link < 24; link++)
        {
            var map = await CaptureAsync(items, catalog, token);
            var target = map.Entities.SingleOrDefault(e => e.Id == targetId)
                ?? throw new InvalidDataException("The entity to power is not observed near the actor.");
            long? network = map.Entities.SingleOrDefault(e => e.Id == referenceId)?.Power?.NetworkId
                ?? throw new InvalidDataException("The reference generator is not observed on a network.");
            if (target.Power?.NetworkId == network) return;
            string force = map.Entities.Single(e => e.Id == map.Actor.Id).Force;
            var owned = map.Entities.Where(e => e.Force == force && e.Power?.NetworkId == network).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            var planning = await PlanningAsync(map, items, catalog, token);
            var next = new PowerGridPlanner().Next(new PowerExpansionPlanner().ReserveGrowth(planning, items.Boiler, items.Engine, force),
                items.Pole, target.Bounds, owned, token);
            if (next.Status is PowerGridSearchStatus.NoObservedPath) next = new PowerGridPlanner().Next(planning, items.Pole, target.Bounds, owned, token);
            await journal.AppendAsync("power-link", new { targetId, referenceId, network, next, map.CollectedTick }, token);
            if (next.Status != PowerGridSearchStatus.Extension || next.Pole is null)
                throw new InvalidOperationException($"The entity cannot join the observed network: {next.Status}.");
            await EnsureItemsAsync(items.Pole, 1, token);
            await ClearAsync([Footprint(map, items.Pole, next.Pole)], items, catalog, controller, token);
            await new PoweredMachineController(game, journal).BuildAtAsync(items.Pole, next.Pole, catalog, controller, token, [target.Position]);
        }
        throw new InvalidOperationException("Joining the network exceeded its pole budget.");
    }

    internal async Task<SteamItems> ItemsAsync(CancellationToken token)
    {
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var state = await ObserveAsync(token);
        var network = state.Main() ?? throw new InvalidDataException("No powered network is observed.");
        return Items(catalog, state, network);
    }

    private async Task<long> ProveGenerationAsync(IReadOnlyList<string> engines, string reference, SteamItems items, ProductionCatalog catalog,
        SpatialController controller, CancellationToken token)
    {
        for (int attempt = 0; attempt < 120; attempt++)
        {
            var map = await CaptureAsync(items, catalog, token);
            long? network = map.Entities.SingleOrDefault(e => e.Id == reference)?.Power?.NetworkId;
            var observed = engines.Select(id => map.Entities.SingleOrDefault(e => e.Id == id)?.Power).ToArray();
            await journal.AppendAsync("power-measurement", new { map.CollectedTick, engines, reference, network, observed }, token);
            if (network is not null && observed.All(p => p?.GeneratedLastTick is > 0 && p.NetworkId == network)) return map.CollectedTick;
            Completed(await controller.WorkAsync("wait", new { ticks = 60 }, 180, token: token), "wait");
        }
        throw new TimeoutException("The expanded engines did not all generate on the main network.");
    }

    /// <summary>
    /// Direct startup fuel: an electric feeder cannot move coal into a boiler whose network has no power yet.
    /// A boiler still burning another fuel keeps it, since its single fuel slot would refuse coal.
    /// </summary>
    private async Task StartBoilerAsync(string boilerId, MapPosition position, ProductionCatalog catalog, SpatialController controller,
        CancellationToken token)
    {
        const int startup = 5;
        var boiler = (await ObserveAsync(token)).Boilers.SingleOrDefault(b => b.Id == boilerId)
            ?? throw new InvalidDataException("The boiler to start is not a known own boiler.");
        long missing = startup - boiler.Fuel.GetValueOrDefault(Fuel);
        if (missing <= 0 || boiler.Fuel.Any(p => p.Key != Fuel && p.Value > 0)) return;
        await EnsureItemsAsync(Fuel, (int)missing, token);
        await controller.ApproachEntityAsync(boilerId, position, catalog, token);
        Completed(await controller.WorkAsync("insert", new { entityId = boilerId, inventory = "fuel", item = Fuel, count = missing }, 600, token: token), "insert");
    }

    /// <summary>A new feeder starts with its boiler lit and one stack in its chest; logistics keeps it stocked afterwards.</summary>
    private async Task PrimeAsync(string boilerId, MapPosition boilerPosition, string chestId, MapPosition chestPosition, ProductionCatalog catalog,
        SpatialController controller, CancellationToken token)
    {
        await StartBoilerAsync(boilerId, boilerPosition, catalog, controller, token);
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        Require(snapshot.Scope, catalog);
        long need = FactoryLogistics.PowerFuelNeed(FactoryLogistics.Items(snapshot, chestId), Fuel, catalog.Items[Fuel].StackSize);
        if (need == 0) return;
        await EnsureItemsAsync(Fuel, (int)need, token);
        await controller.ApproachEntityAsync(chestId, chestPosition, catalog, token);
        Completed(await controller.WorkAsync("insert", new { entityId = chestId, inventory = "chest", item = Fuel, count = need }, 600, token: token), "insert");
    }

    /// <summary>
    /// Gives every boiler of the network a chest and inserter feeder, reusing an observed feeder, and records it as a power cell.
    /// Registered boilers are not revisited, so this costs a trip only for new or migrated boilers.
    /// </summary>
    private async Task<IReadOnlyList<FactoryCell>> EnsureFeedersAsync(PowerState state, ElectricNetworkState network, ProductionCatalog catalog,
        SpatialController controller, CancellationToken token)
    {
        var registry = new FactoryRegistry(directory);
        var items = Items(catalog, state, network);
        foreach (var boiler in Boilers(state, network).OrderBy(b => b.Id, StringComparer.Ordinal))
        {
            var known = await registry.LoadAsync(catalog.Scope.WorldId, token);
            if (known.Cells.Any(c => c.Kind == "power" && c.Status == "ready" && c.Entities.GetValueOrDefault("boiler") == boiler.Id)) continue;
            await controller.TravelAsync(boiler.Position, 6, catalog, token);
            var map = await CaptureAsync(items, catalog, token);
            string force = map.Entities.Single(e => e.Id == map.Actor.Id).Force;
            long networkId = map.Entities.Where(e => boiler.GeneratorIds.Contains(e.Id)).Select(e => e.Power?.NetworkId).FirstOrDefault(n => n is not null)
                ?? throw new InvalidDataException("The boiler's generators are not observed on a network.");
            var planning = await PlanningAsync(map, items, catalog, token);
            var feeder = new FuelFeederPlanner().Find(new PowerExpansionPlanner().ReserveGrowth(planning, items.Boiler, items.Engine, force),
                items.Chest, items.Inserter, boiler.Id, networkId, items.Pole)
                ?? new FuelFeederPlanner().Find(planning, items.Chest, items.Inserter, boiler.Id, networkId, items.Pole);
            if (feeder is null)
            {
                await journal.AppendAsync("power-feeder-unavailable", new { boiler.Id, map.CollectedTick }, token);
                continue;
            }
            await journal.AppendAsync("power-feeder-plan", new { boiler.Id, networkId, feeder, map.CollectedTick }, token);
            string chestId, inserterId;
            if (feeder.ExistingContainerId is not null && feeder.ExistingInserterId is not null)
                (chestId, inserterId) = (feeder.ExistingContainerId, feeder.ExistingInserterId);
            else
            {
                var builder = new PoweredMachineController(game, journal);
                var boxes = new List<WorldBox> { Footprint(map, items.Chest, feeder.Container), Footprint(map, items.Inserter, feeder.Inserter) };
                if (feeder.Pole is not null) boxes.Add(Footprint(map, items.Pole, feeder.Pole));
                await ClearAsync(boxes, items, catalog, controller, token);
                if (feeder.Pole is not null)
                {
                    await EnsureItemsAsync(items.Pole, 1, token);
                    await builder.BuildAtAsync(items.Pole, feeder.Pole, catalog, controller, token, [feeder.Container.Position, feeder.Inserter.Position]);
                }
                await EnsureItemsAsync(items.Chest, 1, token);
                chestId = await builder.BuildAtAsync(items.Chest, feeder.Container, catalog, controller, token, [feeder.Inserter.Position]);
                await EnsureItemsAsync(items.Inserter, 1, token);
                inserterId = await builder.BuildAtAsync(items.Inserter, feeder.Inserter, catalog, controller, token, [feeder.Container.Position]);
            }
            map = await CaptureAsync(items, catalog, token);
            var arm = map.Entities.Single(e => e.Id == inserterId);
            if (arm.PickupTargetId != chestId || arm.DropTargetId != boiler.Id || arm.Power?.NetworkId is null)
                throw new InvalidDataException("The native feeder does not take from its chest into the boiler on a network.");
            var chest = map.Entities.Single(e => e.Id == chestId);
            if (new PlacementPlanner().FindInteractionApproach(new(map), chest) is null)
                throw new InvalidOperationException("The feeder chest is not reachable for logistics.");
            await PrimeAsync(boiler.Id, boiler.Position, chestId, chest.Position, catalog, controller, token);
            var cell = new FactoryCell($"power-{boiler.Id}", PowerZone, new(0, 0, true), "power", items.Boiler, null,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["boiler"] = boiler.Id, ["input-chest"] = chestId, ["input-inserter"] = inserterId },
                "ready", map.CollectedTick);
            await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).With(cell), token);
            await journal.AppendAsync("power-cell-ready", cell, token);
        }
        return (await registry.LoadAsync(catalog.Scope.WorldId, token)).Cells.Where(c => c.Kind == "power").ToArray();
    }

    /// <summary>Observed terrain for planning: the actor moves and removable obstacles are mined, but factory bands stay reserved.</summary>
    private async Task<SpatialSnapshot> PlanningAsync(SpatialSnapshot map, SteamItems items, ProductionCatalog catalog, CancellationToken token)
    {
        var planning = map with
        {
            Entities = map.Entities.Where(e => e.Id != map.Actor.Id && !FactoryZonePlanner.Removable.Contains(map.Prototypes[e.Name].Type)).ToArray()
        };
        foreach (var zone in (await new FactoryRegistry(directory).LoadAsync(catalog.Scope.WorldId, token)).Zones)
            planning = FactoryCellBuilder.ReserveZone(planning, zone, items.Pole);
        return planning;
    }

    private async Task ClearAsync(IReadOnlyList<WorldBox> boxes, SteamItems items, ProductionCatalog catalog, SpatialController controller,
        CancellationToken token)
    {
        for (int attempt = 0; attempt < 32; attempt++)
        {
            var map = await CaptureAsync(items, catalog, token);
            var obstacle = map.Entities.Where(e => FactoryZonePlanner.Removable.Contains(map.Prototypes[e.Name].Type) && boxes.Any(b => b.Overlaps(e.Bounds)))
                .OrderBy(e => e.Position.DistanceTo(map.Actor.Position)).FirstOrDefault();
            if (obstacle is null) return;
            await controller.TravelAsync(obstacle.Position, 2, catalog, token);
            var mined = await controller.WorkAsync("mine", new { name = obstacle.Name, position = obstacle.Position, count = 1 }, 1800, token: token);
            if (mined.Status is not ("completed" or "partial")) throw new InvalidOperationException($"Clearing {obstacle.Name} ended with {mined.Status}: {mined.Error?.Code}.");
            await journal.AppendAsync("power-clearance", new { obstacle.Id, obstacle.Name, obstacle.Position }, token);
        }
        throw new InvalidOperationException("Power clearance exceeded its obstacle budget.");
    }

    private async Task EnsureItemsAsync(string item, int count, CancellationToken token)
    {
        long carried = (await new ProductionController(game, journal).ObserveAsync(token)).Inventory.GetValueOrDefault(item);
        if (carried < count) await new ProductionGoalExecutor(game, journal).RunAsync(item, count, token);
    }

    private async Task<SpatialSnapshot> CaptureAsync(SteamItems items, ProductionCatalog catalog, CancellationToken token)
    {
        var map = await new SpatialClient(game).CaptureAsync(items.All, 48, token);
        Require(map.Scope, catalog);
        return map;
    }

    internal sealed record SteamItems(string Boiler, string Engine, string Pole, string Chest, string Inserter)
    {
        public string[] All => [Boiler, Engine, Pole, Chest, Inserter];
    }

    /// <summary>Expansion reuses the observed boiler and generator prototypes and the best craftable cell equipment.</summary>
    private static SteamItems Items(ProductionCatalog catalog, PowerState state, ElectricNetworkState network)
    {
        var boiler = Boilers(state, network)[0];
        var engine = network.Sources.First(s => boiler.GeneratorIds.Contains(s.Id));
        string Item(string entity) => catalog.Items.Where(p => p.Value.PlaceEntity == entity).Select(p => p.Key).Order(StringComparer.Ordinal).FirstOrDefault()
            ?? throw new InvalidDataException($"No item places {entity}.");
        var equipment = FactoryCellBuilder.Equipment(catalog, Item(boiler.Name));
        return new(equipment.Machine, Item(engine.Name), equipment.Pole, equipment.Chest, equipment.Inserter);
    }

    private static IReadOnlyList<ObservedBoiler> Boilers(PowerState state, ElectricNetworkState network)
    {
        var generators = network.Sources.Where(s => s.Type == "generator").Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        return state.Boilers.Where(b => b.GeneratorIds.Any(generators.Contains)).ToArray();
    }

    private static double Energy(SpatialSnapshot map, string item) => map.Prototypes[map.Items[item].EntityName].EnergyPerTick ?? 0;

    private static WorldBox Footprint(SpatialSnapshot map, string item, PlacementCandidate placement) =>
        map.Prototypes[map.Items[item].EntityName].CollisionBox.Rotate(placement.Direction).Translate(placement.Position);

    private static void Require(ActorScope scope, ProductionCatalog catalog)
    {
        if (scope != catalog.Scope) throw new InvalidDataException("Actor identity changed during power expansion; reconcile partial construction.");
    }

    private static void Completed(OperationReceipt receipt, string action)
    {
        if (receipt.Status != "completed") throw new InvalidOperationException($"Power {action} ended with {receipt.Status}: {receipt.Error?.Code}.");
    }
}
