using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

internal sealed record PowerFuelSourceNeed(int Targets, int Sources, double PerMinute)
{
    public int Missing => Math.Max(0, Targets - Sources);
}

/// <summary>Connects observed coal producers to registered native boiler feeders within the existing belt planner's bounds.</summary>
internal sealed class PowerFuelTransport(IGameClient game, IControllerJournal journal, string directory)
{
    public async Task<int> ConnectAsync(ProductionCatalog catalog, CancellationToken token, int maximumLinks = 2)
    {
        if (maximumLinks is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(maximumLinks));
        if (new[] { "transport-belt", "inserter", "small-electric-pole" }.Any(i => !FactoryDirector.Enabled(catalog, i))) return 0;
        var registry = new FactoryRegistry(directory);
        var builder = new FactoryTransportBuilder(game, journal, directory);
        await using var controller = new SpatialController(game, journal);
        int connected = 0, attempted = 0;
        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        foreach (var bus in (state.Transports ?? []).Where(b => state.Cells.Single(c => c.Id == b.CellId).Status == "building"
            && b.Consumers.Any(c => state.Cells.Any(target => target.Id == c.TargetCellId && target.Kind == "power"))))
        {
            await builder.FinishAsync(bus, catalog, controller, token);
            if (++connected == maximumLinks) return connected;
        }
        if (connected > 0) return connected;
        var batch = await PlanBatchAsync(catalog, controller, maximumLinks, token);
        if (batch.Handled) return batch.Connected;
        foreach (string id in state.Cells.Where(c => c.Kind == "power" && c.Status == "ready").Select(c => c.Id))
        {
            state = await registry.LoadAsync(catalog.Scope.WorldId, token);
            var target = state.Cells.Single(c => c.Id == id);
            var power = await new PowerExpansionController(game, journal, directory).ObserveAsync(token);
            double? demand = PowerFuelPolicy.Demand(catalog, target, FactoryLogistics.Fuel, power);
            if (demand is null) continue;
            var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Fuel transport actor scope changed.");
            if (FactoryLogistics.Missing(snapshot, target).Length > 0) continue;
            PowerExpansionController.ValidateRegisteredFeeder(snapshot, target);
            var shares = FactoryLogistics.CellShares(catalog, state);
            if (FactoryTransportCoverage.Connected(state, snapshot, catalog, shares, power).Contains((target.Entities["input-chest"], FactoryLogistics.Fuel))) continue;
            var position = FactoryTransportBuilder.Position(snapshot, target.Entities["input-chest"]);
            if (position is null) continue;
            var sources = state.Cells.Where(c => c.IsResource && c.Recipe == FactoryLogistics.Fuel && c.Status == "ready"
                    && c.Entities.ContainsKey("output-chest"))
                .Select(c => (Cell: c, Position: FactoryTransportBuilder.Position(snapshot, c.Entities["output-chest"])))
                .Where(c => c.Position is not null).OrderBy(c => c.Position!.DistanceTo(position)).ToArray();
            foreach (var source in sources)
            {
                if ((state.Transports ?? []).Any(b => b.SourceCellId == source.Cell.Id && b.Consumers.Any(c => c.TargetCellId == id))) continue;
                double available = Available(state, snapshot, catalog, shares, power, source.Cell, demand.Value);
                if (available <= 0) continue;
                if (++attempted > 8) return connected;
                if (!await builder.LinkAsync(source.Cell.Id, id, FactoryLogistics.Fuel,
                    FactoryLogistics.PowerChestStacks * catalog.Items[FactoryLogistics.Fuel].StackSize, catalog, controller, token)) continue;
                await journal.AppendAsync("power-fuel-transport-connected", new { source = source.Cell.Id, target = id,
                    demandPerMinute = demand.Value, availablePerMinute = available, snapshot.CollectedTick }, token);
                if (++connected == maximumLinks) return connected;
                state = await registry.LoadAsync(catalog.Scope.WorldId, token);
                snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
                if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Fuel delivery scope changed.");
                if (FactoryTransportCoverage.Connected(state, snapshot, catalog, shares, power)
                    .Contains((target.Entities["input-chest"], FactoryLogistics.Fuel))) break;
            }
        }
        return connected;
    }

    internal async Task<PowerFuelSourceNeed> ObserveSourceNeedAsync(ProductionCatalog catalog, CancellationToken token)
    {
        var state = await new FactoryRegistry(directory).LoadAsync(catalog.Scope.WorldId, token);
        var power = await new PowerExpansionController(game, journal, directory).ObserveAsync(token);
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        var need = SourceNeed(state, snapshot, catalog, power);
        if (need.Missing > 0)
            await journal.AppendAsync("power-fuel-source-shortfall", new { need, snapshot.Scope, snapshot.CollectedTick }, token);
        return need;
    }

    internal static PowerFuelSourceNeed SourceNeed(FactoryState state, FactorySnapshot snapshot, ProductionCatalog catalog, PowerState power)
    {
        if (snapshot.Scope != catalog.Scope || power.Scope != catalog.Scope || state.WorldId != catalog.Scope.WorldId)
            throw new InvalidDataException("Fuel source need requires one native actor and world.");
        // Finish already reserved links before adding suppliers; unfinished buses still own the source capacity.
        if ((state.Transports ?? []).Any(b => state.Cells.Single(c => c.Id == b.CellId).Status == "building"
            && b.Consumers.Any(c => state.Cells.Any(target => target.Id == c.TargetCellId && target.Kind == "power"))))
            return new(0, 0, 0);
        var shares = FactoryLogistics.CellShares(catalog, state);
        var targets = BatchTargets(state, snapshot, catalog, shares, power);
        if (targets.Length < 2) return new(targets.Length, targets.Length, 0); // Single consumers retain the multi-source path.
        double demand = targets.Max(c => PowerFuelPolicy.Demand(catalog, c, FactoryLogistics.Fuel, power)!.Value);
        int sources = LocalSources(state, snapshot, targets).Count(c =>
            FactoryTransportCoverage.Capacity(state, catalog, shares, c, FactoryLogistics.Fuel, snapshot) + 1e-9 >= demand);
        return new(targets.Length, sources, demand);
    }

    private static FactoryCell[] BatchTargets(FactoryState state, FactorySnapshot snapshot, ProductionCatalog catalog,
        IReadOnlyDictionary<string, double>? shares, PowerState power)
    {
        var covered = FactoryTransportCoverage.Connected(state, snapshot, catalog, shares, power);
        var targets = state.Cells.Where(c => c.Kind == "power" && c.Status == "ready"
            && PowerFuelPolicy.Demand(catalog, c, FactoryLogistics.Fuel, power) is > 0
            && FactoryLogistics.Missing(snapshot, c).Length == 0
            && !covered.Contains((c.Entities["input-chest"], FactoryLogistics.Fuel))).ToArray();
        if (targets.Length == 0) return [];
        var anchor = FactoryTransportBuilder.Position(snapshot, targets.OrderBy(c => c.Tick).First().Entities["input-chest"])!;
        return targets.OrderByDescending(c => FactoryTransportBuilder.Position(snapshot, c.Entities["input-chest"])!.DistanceTo(anchor)).Take(8).ToArray();
    }

    internal static FactoryCell[] LocalSources(FactoryState state, FactorySnapshot snapshot, IReadOnlyList<FactoryCell> targets)
    {
        if (snapshot.Scope.WorldId != state.WorldId) throw new InvalidDataException("Fuel sources belong to another world.");
        if (targets.Count == 0) return [];
        var anchor = FactoryTransportBuilder.Position(snapshot, targets.OrderBy(c => c.Tick).First().Entities["input-chest"])!;
        var targetIds = targets.Select(c => c.Entities["input-chest"]).ToArray();
        return state.Cells.Where(c => c.IsResource && c.Recipe == FactoryLogistics.Fuel && c.Status == "ready"
            && FactoryLogistics.Missing(snapshot, c).Length == 0 && PowerFuelPolicy.ProducerActive(c, snapshot)
            && !(state.Transports ?? []).Any(b => b.SourceCellId == c.Id)
            && FactoryTransportBuilder.PlanningCenter(snapshot, [.. targetIds, c.Entities["output-chest"]], 1,
                FactoryTransportBuilder.FuelPlanningRadius) is not null)
            .OrderBy(c => FactoryTransportBuilder.Position(snapshot, c.Entities["output-chest"])!.DistanceTo(anchor)).Take(8).ToArray();
    }

    private async Task<(int Connected, bool Handled)> PlanBatchAsync(ProductionCatalog catalog, SpatialController controller,
        int maximumLinks, CancellationToken token)
    {
        var registry = new FactoryRegistry(directory);
        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        var powerController = new PowerExpansionController(game, journal, directory);
        var power = await powerController.ObserveAsync(token);
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Fuel batch actor scope changed.");
        var shares = FactoryLogistics.CellShares(catalog, state);
        var targets = BatchTargets(state, snapshot, catalog, shares, power);
        if (targets.Length < 2) return (0, false);
        foreach (var target in targets) PowerExpansionController.ValidateRegisteredFeeder(snapshot, target);
        var targetIds = targets.Select(c => c.Entities["input-chest"]).ToArray();
        var sources = LocalSources(state, snapshot, targets);
        if (sources.Length < targets.Length) return (0, true);
        var requests = targets.Select(target => new BeltTransportRequest(target.Entities["input-chest"], sources
            .Where(source => FactoryTransportCoverage.Capacity(state, catalog, shares, source, FactoryLogistics.Fuel, snapshot) + 1e-9
                >= PowerFuelPolicy.Demand(catalog, target, FactoryLogistics.Fuel, power)!.Value)
            .OrderBy(c => FactoryTransportBuilder.Position(snapshot, c.Entities["output-chest"])!
                .DistanceTo(FactoryTransportBuilder.Position(snapshot, target.Entities["input-chest"])!))
            .ThenBy(c => c.Id, StringComparer.Ordinal).Select(c => c.Entities["output-chest"]).ToArray())).ToArray();
        if (requests.Any(r => r.SourceIds.Count == 0)) return (0, true);
        var endpoints = targetIds.Concat(sources.Select(c => c.Entities["output-chest"])).ToArray();
        var steam = await powerController.SteamItemsAsync(catalog, token);
        var builder = new FactoryTransportBuilder(game, journal, directory);
        var map = await builder.CaptureFuelFrameAsync(state, snapshot, endpoints, steam, catalog, controller, token);
        if (map is null) return (0, true);
        if (map.Scope != catalog.Scope) throw new InvalidDataException("Fuel batch planning scope changed.");
        if (endpoints.Any(id => !map.Entities.Any(e => e.Id == id))
            || map.Prototypes[map.Items["inserter"].EntityName].FilterSlots is not > 0) return (0, true);
        var planning = FactoryTransportBuilder.ProtectBands(map, state, steam, sources.Select(c => c.Slot.Band).ToHashSet());
        var plan = await ControllerPlanning.RunAsync(t => SearchBatch(planning, requests, t, token),
            controller, TimeSpan.FromSeconds(45), token);
        if (plan is null)
        {
            await journal.AppendAsync("power-fuel-batch-budget-exceeded", new { targets = targets.Length, sources = sources.Length,
                budgetSeconds = 45, map.CollectedTick, action = "retain-existing-logistics" }, token);
            return (0, true);
        }
        await journal.AppendAsync("power-fuel-batch-search", new { targets = targets.Length, sources = sources.Length,
            links = plan.Links.Count, plan.Searches, plan.BudgetExhausted, selection = "first-complete-feasible", map.CollectedTick }, token);
        if (plan.Links.Count != targets.Length) return (0, true); // Keep actor delivery rather than closing an unfinished consumer's access.
        var records = plan.Links.Select(link => FactoryTransportBuilder.NewBus(
            sources.Single(c => c.Entities["output-chest"] == link.SourceId).Id,
            targets.Single(c => c.Entities["input-chest"] == link.TargetId).Id, FactoryLogistics.Fuel,
            FactoryLogistics.PowerChestStacks * catalog.Items[FactoryLogistics.Fuel].StackSize, link.Plan, map.CollectedTick)).ToArray();
        foreach (var record in records) state = state.With(record.Cell).With(record.Bus);
        await registry.SaveAsync(state, token);
        await journal.AppendAsync("power-fuel-batch-plan", new { map.Scope, map.CollectedTick, buses = records.Select(r => r.Bus),
            plans = records.Select(r => r.Cell.Plan) }, token);
        int connected = 0;
        foreach (var record in records.Take(maximumLinks))
        {
            await builder.FinishAsync(record.Bus, catalog, controller, token);
            connected++;
        }
        return (connected, true);
    }

    internal static BeltTransportBatchPlan? SearchBatch(SpatialSnapshot planning, IReadOnlyList<BeltTransportRequest> requests,
        CancellationToken planningToken, CancellationToken callerToken)
    {
        try
        {
            return new BeltTransportBatchPlanner().Find(planning, new("transport-belt", "inserter", "small-electric-pole"),
                requests, maximumSearches: 64, token: planningToken, stopAfterComplete: true,
                maximumBelts: 512, nodeBudget: 24000);
        }
        catch (OperationCanceledException error) when (planningToken.IsCancellationRequested
            && !callerToken.IsCancellationRequested && error.CancellationToken == planningToken)
        {
            return null; // Only the pure route search expired; no native operation or registry mutation was attempted.
        }
    }

    internal static double Available(FactoryState state, FactorySnapshot snapshot, ProductionCatalog catalog,
        IReadOnlyDictionary<string, double>? shares, PowerState power, FactoryCell source, double additionalDemand)
    {
        if (!double.IsFinite(additionalDemand) || additionalDemand <= 0) throw new ArgumentOutOfRangeException(nameof(additionalDemand));
        var buses = (state.Transports ?? []).Where(b => b.SourceCellId == source.Id).ToArray();
        if (buses.Any(b => b.Graph is not null || !FactoryTransportHealth.Healthy(state, snapshot, b))) return 0;
        double allocated = 0;
        foreach (string id in buses.SelectMany(b => b.Consumers.Where(c => !c.Paused)).Select(c => c.TargetCellId).Distinct(StringComparer.Ordinal))
        {
            double? demand = FactoryTransportCoverage.Demand(catalog, shares, state.Cells.Single(c => c.Id == id), FactoryLogistics.Fuel, power);
            if (demand is null) return 0;
            allocated += demand.Value;
        }
        double spare = Math.Max(0, FactoryTransportCoverage.Capacity(state, catalog, shares, source, FactoryLogistics.Fuel, snapshot) - allocated);
        // A shared source redistributes its flow across all consumer demands. A partial new share must not dilute an existing promise.
        return allocated > 0 && spare + 1e-9 < additionalDemand ? 0 : spare;
    }
}
