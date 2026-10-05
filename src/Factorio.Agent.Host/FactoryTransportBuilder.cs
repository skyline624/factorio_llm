using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Persistent, filtered single-item buses with native destination-stock limits. Routes are solved from observed geometry.</summary>
public sealed class FactoryTransportBuilder(IGameClient game, IControllerJournal journal, string directory)
{
    internal const int FuelPlanningRadius = SpatialSnapshot.MaximumRadius;

    private static readonly BeltTransportEquipment Equipment = new("transport-belt", "inserter", "small-electric-pole");
    private static readonly string[] Items = [Equipment.Belt, Equipment.Inserter, Equipment.Pole];

    public async Task<int> ConnectAsync(ProductionCatalog catalog, int maximumLinks = 2, CancellationToken token = default,
        IReadOnlySet<string>? targetCellIds = null)
    {
        if (maximumLinks is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(maximumLinks));
        if (Items.Any(i => !FactoryDirector.Enabled(catalog, i))) return 0;
        var registry = new FactoryRegistry(directory);
        await using var controller = new SpatialController(game, journal);
        int connected = 0, considered = 0;
        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        foreach (var bus in (state.Transports ?? []).Where(b => state.Cells.Single(c => c.Id == b.CellId).Status == "building"))
        {
            await FinishAsync(bus, catalog, controller, token);
            if (++connected == maximumLinks) return connected;
        }
        state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        var shares = FactoryLogistics.CellShares(catalog, state);
        var stockSnapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        if (stockSnapshot.Scope != catalog.Scope) throw new InvalidDataException("Transport demand photograph belongs to another actor scope.");
        var pausedCells = FactoryLogistics.PausedCells(state.Cells.Where(c => c.Status == "ready"), catalog,
            FactoryLogistics.StockCaps(catalog, state), FactoryLogistics.AvailableStock(stockSnapshot))
            .Select(p => p.Cell.Id).ToHashSet(StringComparer.Ordinal);
        if (maximumLinks - connected >= 2 && FactoryDirector.Enabled(catalog, "splitter"))
        {
            foreach (var bus in (state.Transports ?? []).Where(b => b.Graph is null && b.Consumers.Count == 2
                && b.Consumers.All(c => !pausedCells.Contains(c.TargetCellId) && (targetCellIds is null || targetCellIds.Contains(c.TargetCellId)))))
            {
                if (++considered > 8) return connected;
                if (!await new FactoryTransportConversion(game, journal, directory).PlanAsync(bus, catalog, controller, token)) continue;
                state = await registry.LoadAsync(catalog.Scope.WorldId, token);
                await FinishAsync(state.Transports!.Single(b => b.Id == bus.Id), catalog, controller, token);
                connected += 2;
                if (maximumLinks - connected < 2) return connected;
                state = await registry.LoadAsync(catalog.Scope.WorldId, token);
                stockSnapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
                if (stockSnapshot.Scope != catalog.Scope) throw new InvalidDataException("Converted transport demand scope changed.");
            }
            var covered = FactoryTransportCoverage.Connected(state, stockSnapshot, catalog, shares);
            var needs = state.Cells.Where(c => c.Status == "ready" && c.Recipe is not null && c.Entities.ContainsKey("input-chest")
                && !pausedCells.Contains(c.Id) && (targetCellIds is null || targetCellIds.Contains(c.Id)))
                .SelectMany(c => catalog.Recipes.FirstOrDefault(r => r.Name == c.Recipe)?.Ingredients
                    .Where(i => i.DeterministicItem && !covered.Contains((c.Entities["input-chest"], i.Name)))
                    .Select(i => (Target: c, Ingredient: i)) ?? []).ToArray();
            foreach (var group in needs.GroupBy(n => n.Ingredient.Name))
            {
                foreach (var source in state.Cells.Where(c => c.Status == "ready" && c.Entities.ContainsKey("output-chest")
                    && Product(catalog, c) == group.Key && !(state.Transports ?? []).Any(b => b.SourceCellId == c.Id)))
                {
                    var from = Position(stockSnapshot, source.Entities["output-chest"]);
                    if (from is null) continue;
                    var targets = group.Where(n => n.Target.Id != source.Id && Position(stockSnapshot, n.Target.Entities["input-chest"]) is not null)
                        .OrderBy(n => Position(stockSnapshot, n.Target.Entities["input-chest"])!.DistanceTo(from)).Take(2).ToArray();
                    if (targets.Length != 2 || PlanningCenter(stockSnapshot,
                        [source.Entities["output-chest"], .. targets.Select(n => n.Target.Entities["input-chest"])]) is null) continue;
                    if (++considered > 8) return connected;
                    int Limit(int index) => checked((int)Math.Clamp(targets[index].Ingredient.Amount!.Value
                        * FactoryLogistics.CellBufferCrafts(targets[index].Target, shares, 40), 1, 10000));
                    if (!await LinkPairAsync(source.Id, targets[0].Target.Id, targets[1].Target.Id, group.Key,
                        Limit(0), Limit(1), catalog, controller, token)) continue;
                    connected += 2;
                    if (connected == maximumLinks) return connected;
                    state = await registry.LoadAsync(catalog.Scope.WorldId, token);
                    break;
                }
                if (maximumLinks - connected < 2) break;
            }
        }
        foreach (var target in state.Cells.Where(c => c.Status == "ready" && c.Recipe is not null && c.Entities.ContainsKey("input-chest")
            && (targetCellIds is null || targetCellIds.Contains(c.Id))))
        {
            if (pausedCells.Contains(target.Id)) continue;
            var recipe = catalog.Recipes.FirstOrDefault(r => r.Name == target.Recipe);
            if (recipe is null) continue;
            var batch = await PlanNewBatchAsync(target, recipe, catalog, shares, controller, maximumLinks - connected, token);
            connected += batch.Connected;
            if (connected == maximumLinks) return connected;
            if (batch.Handled) continue;
            foreach (var ingredient in recipe.Ingredients.Where(i => i.DeterministicItem))
            {
                state = await registry.LoadAsync(catalog.Scope.WorldId, token);
                var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
                if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Factory transport actor scope changed.");
                if (FactoryTransportCoverage.Connected(state, snapshot, catalog, shares).Contains((target.Entities["input-chest"], ingredient.Name))) continue;
                var destination = Position(snapshot, target.Entities["input-chest"]);
                if (destination is null) continue;
                var sources = state.Cells.Where(c => c.Id != target.Id && c.Status == "ready" && c.Entities.ContainsKey("output-chest")
                    && Product(catalog, c) == ingredient.Name).Select(c => (Cell: c, Position: Position(snapshot, c.Entities["output-chest"])))
                    .Where(p => p.Position is not null).OrderBy(p => p.Position!.DistanceTo(destination)).ToArray();
                foreach (var candidate in sources)
                {
                    // Keep the existing graph, but do not mistake one supplier for enough supply or retry its old link.
                    if ((state.Transports ?? []).Any(b => b.Item == ingredient.Name && b.SourceCellId == candidate.Cell.Id
                        && b.Consumers.Any(c => c.TargetCellId == target.Id))) continue;
                    var existingBus = (state.Transports ?? []).SingleOrDefault(b => b.SourceCellId == candidate.Cell.Id && b.Item == ingredient.Name);
                    if (PlanningCenter(snapshot, FrameEntities(state, candidate.Cell, target, existingBus)) is null) continue;
                    if (++considered > 8) return connected;
                    int limit = checked((int)Math.Clamp(ingredient.Amount!.Value * FactoryLogistics.CellBufferCrafts(target, shares, 40), 1, 10000));
                    if (await LinkAsync(candidate.Cell.Id, target.Id, ingredient.Name, limit, catalog, controller, token))
                    {
                        if (++connected == maximumLinks) return connected;
                        state = await registry.LoadAsync(catalog.Scope.WorldId, token);
                        snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
                        if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Factory transport actor scope changed.");
                        if (FactoryTransportCoverage.Connected(state, snapshot, catalog, shares)
                            .Contains((target.Entities["input-chest"], ingredient.Name))) break;
                    }
                }
            }
        }
        return connected;
    }

    private async Task<(int Connected, bool Handled)> PlanNewBatchAsync(FactoryCell target, NativeRecipe recipe,
        ProductionCatalog catalog, IReadOnlyDictionary<string, double>? shares, SpatialController controller, int maximumLinks,
        CancellationToken token)
    {
        var registry = new FactoryRegistry(directory);
        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Transport batch photograph belongs to another actor scope.");
        var coverage = FactoryTransportCoverage.Connected(state, snapshot, catalog, shares);
        string targetChest = target.Entities["input-chest"];
        var needed = recipe.Ingredients.Where(i => i.DeterministicItem && !coverage.Contains((targetChest, i.Name))).ToArray();
        if (needed.Length is < 2 or > 8 || Position(snapshot, targetChest) is not { } destination) return (0, false);
        var requests = new List<(FactoryCell Source, string Item, int Maximum)>();
        foreach (var ingredient in needed)
        {
            var source = state.Cells.Where(c => c.Id != target.Id && c.Status == "ready" && c.Entities.ContainsKey("output-chest")
                && Product(catalog, c) == ingredient.Name && !(state.Transports ?? []).Any(b => b.SourceCellId == c.Id))
                .Select(c => (Cell: c, Position: Position(snapshot, c.Entities["output-chest"])))
                .Where(p => p.Position is not null && PlanningCenter(snapshot, [p.Cell.Entities["output-chest"], targetChest]) is not null)
                .OrderBy(p => p.Position!.DistanceTo(destination)).Select(p => p.Cell).FirstOrDefault();
            // Existing buses retain their extension path; a distant/unobserved supply retains actor logistics.
            if (source is null) return (0, false);
            int limit = checked((int)Math.Clamp(ingredient.Amount!.Value * FactoryLogistics.CellBufferCrafts(target, shares, 40), 1, 10000));
            requests.Add((source, ingredient.Name, limit));
        }
        var frameEntities = requests.Select(r => r.Source.Entities["output-chest"]).Append(targetChest).ToArray();
        var frameCenter = PlanningCenter(snapshot, frameEntities);
        if (frameCenter is null) return (0, false);
        await controller.TravelAsync(frameCenter, 4, catalog, token);
        var steam = await new PowerExpansionController(game, journal, directory).SteamItemsAsync(catalog, token);
        var map = await new SpatialClient(game).CaptureAsync(GeometryItems(state, steam), 48, token);
        if (map.Scope != catalog.Scope) throw new InvalidDataException("Transport batch planning scope changed.");
        if (frameEntities.Any(id => !map.Entities.Any(e => e.Id == id))
            || map.Prototypes[map.Items[Equipment.Inserter].EntityName].FilterSlots is not > 0) return (0, false);
        map = ProtectBands(map, state, steam);
        var plan = await ControllerPlanning.RunAsync(t => new BeltTransportBatchPlanner().Find(map, Equipment,
            requests.Select(r => r.Source.Entities["output-chest"]).ToArray(), targetChest, token: t),
            controller, TimeSpan.FromSeconds(45), token);
        await journal.AppendAsync("factory-transport-batch-search", new { map.Scope, map.CollectedTick, targetCellId = target.Id,
            requested = requests.Count, plan.Searches, plan.BudgetExhausted, connected = plan.Links.Count,
            belts = plan.Links.Sum(l => l.Plan.Belts.Count) }, token);
        // Do not materialize an incomplete local proof that already blocks another ingredient.
        if (plan.Links.Count != requests.Count) return (0, true);
        var records = plan.Links.Select(link =>
        {
            var request = requests.Single(r => r.Source.Entities["output-chest"] == link.SourceId);
            return NewBus(request.Source.Id, target.Id, request.Item, request.Maximum, link.Plan, map.CollectedTick);
        }).ToArray();
        foreach (var record in records) state = state.With(record.Cell).With(record.Bus);
        // Persist every compared route together before the first build. A bounded run may leave later buses pending;
        // the existing building-bus loop resumes their recorded coordinates instead of greedily replanning them.
        await registry.SaveAsync(state, token);
        await journal.AppendAsync("factory-transport-batch-plan", new { map.Scope, map.CollectedTick,
            targetCellId = target.Id, buses = records.Select(r => r.Bus), plans = records.Select(r => r.Cell.Plan) }, token);
        int connected = 0;
        foreach (var record in records.Take(maximumLinks))
        {
            await FinishAsync(record.Bus, catalog, controller, token);
            connected++;
        }
        return (connected, true);

    }

    internal static (FactoryCell Cell, FactoryTransportBus Bus) NewBus(string sourceCellId, string targetCellId, string item,
        int maximum, BeltTransportPlan plan, long tick)
    {
        var entities = new Dictionary<string, PlannedEntity>(StringComparer.Ordinal);
        Add("source-inserter", Equipment.Inserter, plan.SourceInserter);
        Add("target-inserter-0", Equipment.Inserter, plan.TargetInserter);
        for (int i = 0; i < plan.Belts.Count; i++) Add($"belt-{i}", Equipment.Belt, plan.Belts[i]);
        for (int i = 0; i < plan.Poles.Count; i++) Add($"pole-{i}", Equipment.Pole, plan.Poles[i]);
        string cellId = $"transport-{Guid.NewGuid():N}";
        var cell = new FactoryCell(cellId, 0, new(0, 0, true), "transport", Equipment.Belt, null,
            new Dictionary<string, string>(), "building", tick, Plan: entities);
        var bus = new FactoryTransportBus($"bus-{Guid.NewGuid():N}", sourceCellId, item, cellId,
            [new(targetCellId, "target-inserter-0", maximum)]);
        return (cell, bus);
        void Add(string role, string equipment, PlacementCandidate p) => entities[role] = new(role, equipment, p.Position, p.Direction);
    }

    public async Task<bool> LinkAsync(string sourceCellId, string targetCellId, string item, int maximum, ProductionCatalog catalog,
        SpatialController controller, CancellationToken token)
    {
        if (maximum is < 1 or > 10000 || !catalog.Items.ContainsKey(item)) throw new ArgumentException("Invalid transport stock limit or item.");
        var registry = new FactoryRegistry(directory);
        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        var source = state.Cells.Single(c => c.Id == sourceCellId);
        var target = state.Cells.Single(c => c.Id == targetCellId);
        var power = target.Kind == "power" ? await new PowerExpansionController(game, journal, directory).ObserveAsync(token) : null;
        bool consumes = catalog.Recipes.Any(r => r.Name == target.Recipe && r.Ingredients.Any(i => i.DeterministicItem && i.Name == item))
            || PowerFuelPolicy.Demand(catalog, target, item, power) is > 0;
        if (source.Status != "ready" || target.Status != "ready" || Product(catalog, source) != item
            || !source.Entities.ContainsKey("output-chest") || !target.Entities.ContainsKey("input-chest")
            || !consumes)
            throw new InvalidOperationException("A bus must connect a registered producer to its native recipe or fuel consumer.");
        var bus = (state.Transports ?? []).SingleOrDefault(b => b.SourceCellId == sourceCellId && b.Item == item);
        if (bus is not null && bus.Consumers.Any(c => c.TargetCellId == targetCellId))
        {
            if (state.Cells.Single(c => c.Id == bus.CellId).Status == "building") await FinishAsync(bus, catalog, controller, token);
            return true;
        }
        // A branched graph has no single ordered tail. Retain it until graph-aware extension is available.
        if (bus?.Graph is not null) return false;
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Factory transport scope changed.");
        if (target.Kind == "power") PowerExpansionController.ValidateRegisteredFeeder(snapshot, target);
        if (bus is not null && !FactoryTransportHealth.Healthy(state, snapshot, bus)) return false;
        var from = Position(snapshot, source.Entities["output-chest"]);
        var to = Position(snapshot, target.Entities["input-chest"]);
        if (from is null || to is null) return false;
        var frameEntities = FrameEntities(state, source, target, bus);
        int approachTiles = target.Kind == "power" ? 1 : 4;
        var frameCenter = PlanningCenter(snapshot, frameEntities, approachTiles, target.Kind == "power" ? FuelPlanningRadius : 48);
        if (frameCenter is null) return false;
        var steam = await new PowerExpansionController(game, journal, directory).SteamItemsAsync(catalog, token);
        SpatialSnapshot? map;
        if (target.Kind == "power") map = await CaptureFuelFrameAsync(state, snapshot, frameEntities, steam, catalog, controller, token);
        else
        {
            await controller.TravelAsync(frameCenter, approachTiles, catalog, token);
            map = await new SpatialClient(game).CaptureAsync(GeometryItems(state, steam), 48, token);
        }
        if (map is null) return false;
        if (map.Scope != catalog.Scope) throw new InvalidDataException("Transport planning scope changed.");
        if (frameEntities.Any(id => !map.Entities.Any(e => e.Id == id)) || map.Prototypes[map.Items[Equipment.Inserter].EntityName].FilterSlots is not > 0)
            return false;
        await journal.AppendAsync("factory-transport-frame", new { map.Scope, map.CollectedTick, sourceCellId, targetCellId,
            requestedCenter = frameCenter, observedCenter = map.Actor.Position, map.Bounds, requiredEntities = frameEntities.Length }, token);
        map = ProtectBands(map, state, steam, target.Kind == "power" && source.IsResource ? new HashSet<int> { source.Slot.Band } : null);
        if (bus is null)
        {
            var plan = new BeltTransportPlanner().Find(map, Equipment, source.Entities["output-chest"], target.Entities["input-chest"], token);
            if (plan is null) return false;
            var record = NewBus(sourceCellId, targetCellId, item, maximum, plan, map.CollectedTick);
            await registry.SaveAsync(state.With(record.Cell).With(record.Bus), token);
            await journal.AppendAsync("factory-transport-plan", new { bus = record.Bus, record.Cell.Plan, map.CollectedTick }, token);
            await FinishAsync(record.Bus, catalog, controller, token);
            return true;
        }
        var current = state.Cells.Single(c => c.Id == bus.CellId);
        var plans = new Dictionary<string, PlannedEntity>(current.Plan!, StringComparer.Ordinal);
        string consumerRole = $"target-inserter-{bus.Consumers.Count}";
        var roles = FactoryTransportHealth.Belts(current);
        if (roles.Any(r => !map.Entities.Any(e => e.Id == current.Entities[r]))) return false;
        var extension = new FactoryBeltPlanner().Extend(map, Equipment, roles.Select(r => current.Entities[r]).ToArray(), target.Entities["input-chest"], token);
        if (extension is null) return false;
        Add(consumerRole, Equipment.Inserter, extension.TargetInserter);
        Add(roles[^1], Equipment.Belt, extension.Belts[0]);
        for (int i = 1; i < extension.Belts.Count; i++) Add($"belt-{roles.Length + i - 1}", Equipment.Belt, extension.Belts[i]);
        int poles = plans.Keys.Count(k => k.StartsWith("pole-", StringComparison.Ordinal));
        for (int i = 0; i < extension.Poles.Count; i++) Add($"pole-{poles + i}", Equipment.Pole, extension.Poles[i]);
        var cell = current with { Plan = plans, Status = "building" };
        bus = bus with { Consumers = [.. bus.Consumers, new(targetCellId, consumerRole, maximum)] };
        // Save the complete new graph before its first mutation, including a terminal belt's future direction on extension.
        await registry.SaveAsync(state.With(cell).With(bus), token);
        await journal.AppendAsync("factory-transport-plan", new { bus, cell.Plan, map.CollectedTick }, token);
        await FinishAsync(bus, catalog, controller, token);
        return true;

        void Add(string role, string equipment, PlacementCandidate p) => plans[role] = new(role, equipment, p.Position, p.Direction);
    }

    public async Task<bool> LinkPairAsync(string sourceCellId, string firstCellId, string secondCellId, string item,
        int firstMaximum, int secondMaximum, ProductionCatalog catalog, SpatialController controller, CancellationToken token)
    {
        if (firstMaximum is < 1 or > 10000 || secondMaximum is < 1 or > 10000 || !catalog.Items.ContainsKey(item)
            || new[] { sourceCellId, firstCellId, secondCellId }.Distinct(StringComparer.Ordinal).Count() != 3)
            throw new ArgumentException("A balanced bus requires distinct endpoints, a known item and bounded stock limits.");
        if (Items.Append("splitter").Any(i => !FactoryDirector.Enabled(catalog, i))) return false;
        var registry = new FactoryRegistry(directory);
        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        var source = state.Cells.Single(c => c.Id == sourceCellId);
        var first = state.Cells.Single(c => c.Id == firstCellId);
        var second = state.Cells.Single(c => c.Id == secondCellId);
        if (source.Status != "ready" || Product(catalog, source) != item || !source.Entities.ContainsKey("output-chest")
            || new[] { first, second }.Any(c => c.Status != "ready" || !c.Entities.ContainsKey("input-chest")
                || !catalog.Recipes.Any(r => r.Name == c.Recipe && r.Ingredients.Any(i => i.DeterministicItem && i.Name == item))))
            throw new InvalidOperationException("A balanced bus must connect a registered producer and two native recipe consumers.");
        if ((state.Transports ?? []).Any(b => b.SourceCellId == sourceCellId)) return false;
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Balanced transport actor scope changed.");
        string[] endpoints = [source.Entities["output-chest"], first.Entities["input-chest"], second.Entities["input-chest"]];
        if (PlanningCenter(snapshot, endpoints) is not { } center) return false;
        await controller.TravelAsync(center, 4, catalog, token);
        var steam = await new PowerExpansionController(game, journal, directory).SteamItemsAsync(catalog, token);
        var map = await new SpatialClient(game).CaptureAsync(GeometryItems(state, steam).Append("splitter").Distinct(StringComparer.Ordinal).ToArray(), 48, token);
        if (map.Scope != catalog.Scope) throw new InvalidDataException("Balanced transport planning scope changed.");
        if (endpoints.Any(id => !map.Entities.Any(e => e.Id == id))
            || map.Prototypes[map.Items[Equipment.Inserter].EntityName].FilterSlots is not > 0) return false;
        map = ProtectBands(map, state, steam);
        var plan = await ControllerPlanning.RunAsync(t => new BalancedBeltPlanner().Find(map, Equipment, "splitter",
            endpoints[0], endpoints[1], endpoints[2], t), controller, TimeSpan.FromSeconds(45), token);
        await journal.AppendAsync("factory-balanced-transport-search", new { map.Scope, map.CollectedTick, sourceCellId,
            firstCellId, secondCellId, found = plan is not null }, token);
        if (plan is null) return false;
        var record = NewBalancedBus(sourceCellId, firstCellId, secondCellId, item, firstMaximum, secondMaximum, plan, map.CollectedTick);
        await registry.SaveAsync(state.With(record.Cell).With(record.Bus), token);
        await journal.AppendAsync("factory-transport-plan", new { bus = record.Bus, record.Cell.Plan, map.CollectedTick }, token);
        await FinishAsync(record.Bus, catalog, controller, token);
        return true;
    }

    internal static (FactoryCell Cell, FactoryTransportBus Bus) NewBalancedBus(string sourceCellId, string firstCellId,
        string secondCellId, string item, int firstMaximum, int secondMaximum, BalancedBeltPlan plan, long tick)
    {
        var entities = new Dictionary<string, PlannedEntity>(StringComparer.Ordinal);
        Add("source-inserter", Equipment.Inserter, plan.First.SourceInserter);
        Add("target-inserter-0", Equipment.Inserter, plan.First.TargetInserter);
        Add("target-inserter-1", Equipment.Inserter, plan.Second.TargetInserter);
        Add("splitter-0", "splitter", plan.Splitter);
        for (int i = 0; i < plan.First.Belts.Count; i++)
            if (i != plan.ReplacedBelt) Add($"belt-{i}", Equipment.Belt, plan.First.Belts[i]);
        for (int i = 0; i < plan.Second.Belts.Count; i++) Add($"belt-{plan.First.Belts.Count + i}", Equipment.Belt, plan.Second.Belts[i]);
        var poles = plan.First.Poles.Concat(plan.Second.Poles).DistinctBy(p => p.Position).ToArray();
        for (int i = 0; i < poles.Length; i++) Add($"pole-{i}", Equipment.Pole, poles[i]);
        var inputs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var outputs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string FirstRole(int index) => index == plan.ReplacedBelt ? "splitter-0" : $"belt-{index}";
        string SecondRole(int index) => $"belt-{plan.First.Belts.Count + index}";
        foreach (var role in Enumerable.Range(0, plan.First.Belts.Count).Select(FirstRole)
            .Concat(Enumerable.Range(0, plan.Second.Belts.Count).Select(SecondRole)))
        { inputs[role] = []; outputs[role] = []; }
        void Edge(string from, string to) { outputs[from].Add(to); inputs[to].Add(from); }
        for (int i = 1; i < plan.First.Belts.Count; i++) Edge(FirstRole(i - 1), FirstRole(i));
        Edge("splitter-0", SecondRole(0));
        for (int i = 1; i < plan.Second.Belts.Count; i++) Edge(SecondRole(i - 1), SecondRole(i));
        var graph = inputs.ToDictionary(p => p.Key, p => new FactoryConveyorEdges(p.Value, outputs[p.Key]), StringComparer.Ordinal);
        string cellId = $"transport-{Guid.NewGuid():N}";
        var cell = new FactoryCell(cellId, 0, new(0, 0, true), "transport", Equipment.Belt, null,
            new Dictionary<string, string>(), "building", tick, Plan: entities);
        var bus = new FactoryTransportBus($"bus-{Guid.NewGuid():N}", sourceCellId, item, cellId,
            [new(firstCellId, "target-inserter-0", firstMaximum), new(secondCellId, "target-inserter-1", secondMaximum)], Graph: graph);
        return (cell, bus);
        void Add(string role, string equipment, PlacementCandidate p) => entities[role] = new(role, equipment, p.Position, p.Direction);
    }

    public async Task RepairControlsAsync(FactoryState state, FactorySnapshot snapshot, ProductionCatalog catalog,
        SpatialController controller, CancellationToken token)
    {
        foreach (var bus in state.Transports ?? [])
        {
            var cell = state.Cells.Single(c => c.Id == bus.CellId);
            if (cell.Status != "ready" || state.Cells.SingleOrDefault(c => c.Id == bus.SourceCellId)?.Status != "ready"
                || bus.Consumers.Any(p => state.Cells.SingleOrDefault(c => c.Id == p.TargetCellId)?.Status != "ready")
                || cell.Entities.Values.Any(id => Position(snapshot, id) is null)
                || FactoryTransportHealth.Healthy(state, snapshot, bus)) continue;
            await ConfigureAsync(bus, state, catalog, controller, token);
        }
    }

    public async Task<FactoryState> ApplyPausesAsync(FactoryState state, IReadOnlySet<string> pausedCells, CancellationToken token)
    {
        bool changed = false;
        foreach (var bus in state.Transports ?? [])
        {
            var consumers = bus.Consumers.Select(c => c with { Paused = pausedCells.Contains(c.TargetCellId) }).ToArray();
            if (consumers.SequenceEqual(bus.Consumers)) continue;
            state = state.With(bus with { Consumers = consumers });
            changed = true;
        }
        if (changed) await new FactoryRegistry(directory).SaveAsync(state, token);
        return state;
    }

    /// <summary>Keep one bounded actor lot in each healthy bus source when other, unconnected consumers lack its item.</summary>
    public async Task<FactoryState> ApplyActorReservationsAsync(FactoryState state, FactorySnapshot snapshot,
        IReadOnlyDictionary<string, long> needs, IReadOnlyDictionary<string, long> carried, ProductionCatalog catalog, CancellationToken token)
    {
        bool changed = false;
        foreach (var bus in state.Transports ?? [])
        {
            if (!FactoryTransportHealth.Healthy(state, snapshot, bus)) continue;
            int reserve = ActorReserve(needs.GetValueOrDefault(bus.Item), carried.GetValueOrDefault(bus.Item), catalog.Items[bus.Item].StackSize);
            if (bus.ActorReserve == reserve || bus.ActorReserve is null && reserve == 0) continue;
            state = state.With(bus with { ActorReserve = reserve });
            changed = true;
            await journal.AppendAsync("factory-transport-actor-reserve", new { bus.Id, bus.Item, reserve, snapshot.CollectedTick }, token);
        }
        if (changed) await new FactoryRegistry(directory).SaveAsync(state, token);
        return state;
    }

    internal static int ActorReserve(long needed, long carried, int stackSize) =>
        checked((int)Math.Min(Math.Max(0, needed - carried), Math.Clamp(stackSize / 4, 1, 10000)));

    internal async Task FinishAsync(FactoryTransportBus bus, ProductionCatalog catalog, SpatialController controller, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(40));
        token = deadline.Token;
        bus = await new FactoryTransportConversion(game, journal, directory).RetireAsync(bus, catalog, controller, token);
        var registry = new FactoryRegistry(directory);
        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        var cell = state.Cells.Single(c => c.Id == bus.CellId);
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        cell = FactoryMaintenance.Reconcile(cell, snapshot, catalog, state.Cells.Where(c => c.Id != cell.Id)
            .SelectMany(c => c.Entities.Values).ToHashSet(StringComparer.Ordinal), removeMissing: true);
        await registry.SaveAsync(state.With(cell), token);
        var missing = cell.Plan!.Values.Where(p => !cell.Entities.ContainsKey(p.Role)).GroupBy(p => p.Item)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        await new FactoryCellBuilder(game, journal, directory).EnsureCarriedAsync(registry, catalog, missing, token);
        var ids = new Dictionary<string, string>(cell.Entities, StringComparer.Ordinal);
        foreach (var p in cell.Plan.Values.OrderBy(p => p.Role.StartsWith("pole-", StringComparison.Ordinal) ? 0
            : p.Role.StartsWith("target-inserter-", StringComparison.Ordinal) ? 1 : p.Role == "source-inserter" ? 3 : 2))
        {
            if (ids.ContainsKey(p.Role)) continue;
            string id = await new PoweredMachineController(game, journal).BuildAtAsync(p.Item, new(p.Position, p.Direction, 0), catalog, controller, token,
                stoppedInserterItem: p.Item == Equipment.Inserter ? bus.Item : null);
            ids[p.Role] = id;
            cell = cell with { Entities = new Dictionary<string, string>(ids, StringComparer.Ordinal) };
            await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).With(cell), token);
        }
        state = (await registry.LoadAsync(catalog.Scope.WorldId, token)).With(cell with { Status = "ready" });
        await ConfigureAsync(bus, state, catalog, controller, token);
        snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        if (!FactoryTransportHealth.Healthy(state, snapshot, bus))
            throw new InvalidDataException("The built bus lacks a matching native graph, filtered stock control or fed endpoints.");
        cell = cell with { Status = "ready", Tick = snapshot.CollectedTick };
        await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).With(cell), token);
        await journal.AppendAsync("factory-transport-ready", new { bus, cell, snapshot.CollectedTick }, token);
    }

    private async Task ConfigureAsync(FactoryTransportBus bus, FactoryState state, ProductionCatalog catalog, SpatialController controller, CancellationToken token)
    {
        var cell = state.Cells.Single(c => c.Id == bus.CellId);
        var control = new FactoryTransportControl(game, journal);
        foreach (var consumer in bus.Consumers)
        {
            var target = state.Cells.Single(c => c.Id == consumer.TargetCellId);
            if (!target.Entities.TryGetValue("input-chest", out var chest)) return;
            var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            if (Position(snapshot, chest) is null) return;
            var position = Position(snapshot, cell.Entities[consumer.InserterRole])!;
            await controller.ApproachEntityAsync(cell.Entities[consumer.InserterRole], position, catalog, token);
            await control.EnsureAsync(cell.Entities[consumer.InserterRole], bus.Item, catalog, controller, token, chest, consumer.Paused ? 0 : consumer.Maximum);
        }
        foreach (var role in bus.Graph?.Keys ?? FactoryTransportHealth.Belts(cell))
        {
            var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            var record = snapshot.Records.Single(r => r.Kind == "entity" && r.EntityId == cell.Entities[role]);
            if (record.Data.GetProperty("direction").GetInt32() == cell.Plan![role].Direction) continue;
            await OrientAsync(cell.Entities[role], cell.Plan[role].Direction, catalog, controller, token);
        }
        var sourceSnapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        await controller.ApproachEntityAsync(cell.Entities["source-inserter"], Position(sourceSnapshot, cell.Entities["source-inserter"])!, catalog, token);
        await control.EnsureAsync(cell.Entities["source-inserter"], bus.Item, catalog, controller, token,
            bus.ActorReserve is null ? null : state.Cells.Single(c => c.Id == bus.SourceCellId).Entities["output-chest"],
            bus.ActorReserve, bus.ActorReserve is null ? "<" : ">");
    }

    private async Task OrientAsync(string id, int direction, ProductionCatalog catalog, SpatialController controller, CancellationToken token)
    {
        int? expected = null;
        for (int attempt = 0; attempt <= 3; attempt++)
        {
            var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            var data = snapshot.Records.Single(r => r.Kind == "entity" && r.EntityId == id).Data;
            int native = data.GetProperty("direction").GetInt32();
            if (expected is not null && native != expected) throw new InvalidDataException("Belt direction differs from its rotation receipt.");
            if (native == direction) return;
            if (attempt == 3) break;
            await controller.ApproachEntityAsync(id, data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!, catalog, token);
            var receipt = await controller.WorkAsync("rotate", new { entityId = id }, 600, token: token);
            if (receipt.Status != "completed" || receipt.Effects.GetProperty("beforeDirection").GetInt32() != native)
                throw new InvalidDataException("Belt orientation requires a matching completed rotation receipt.");
            expected = receipt.Effects.GetProperty("afterDirection").GetInt32();
        }
        throw new InvalidOperationException("Belt did not reach its planned cardinal direction.");
    }

    // Built entities already export their geometry in the local photograph. Only unfinished non-transport plans need
    // additional item prototypes; retaining every historical plan eventually exceeds the native 16-item request budget.
    internal static string[] GeometryItems(FactoryState state, PowerExpansionController.SteamItems? steam = null) =>
        [.. Items.Concat(new FactoryGround(state, steam).Items)
            .Concat(UnfinishedParts(state).Select(p => p.Item)).Distinct(StringComparer.Ordinal)];

    private static IEnumerable<PlannedEntity> UnfinishedParts(FactoryState state) =>
        state.Cells.Where(c => c.Status == "building" && c.Kind != "transport").SelectMany(c => c.Plan?.Values ?? []);

    internal static SpatialSnapshot ProtectBands(SpatialSnapshot map, FactoryState state, PowerExpansionController.SteamItems? steam = null,
        IReadOnlySet<int>? sourceTransportRows = null)
    {
        var boxes = new List<WorldBox>();
        foreach (var zone in state.Zones)
        {
            int walkway = FactoryBandPlanner.WalkwayTiles(zone.TransportAccess);
            int rowHeight = (zone.BandHeight - walkway) / 2;
            for (int i = 0; i < zone.Slots; i++)
                foreach (bool north in new[] { true, false })
                {
                    if (state.Cells.Any(c => c.Zone == zone.Id && c.Slot.Index == i && c.Slot.North == north)) continue;
                    double top = zone.Origin.Y + (north ? 0 : rowHeight + walkway);
                    boxes.Add(new(new(zone.Origin.X + i * zone.Pitch, top), new(zone.Origin.X + (i + 1) * zone.Pitch, top + rowHeight)));
                }
        }
        boxes.AddRange(UnfinishedParts(state)
            .Where(p => map.Items.ContainsKey(p.Item)).Select(p => map.Prototypes[map.Items[p.Item].EntityName].CollisionBox.Rotate(p.Direction).Translate(p.Position)));
        boxes.AddRange((state.Rows ?? []).SelectMany(r => ResourceCellPlanner.Reservation(map, r, reserveWalkway: sourceTransportRows?.Contains(r.Id) != true)));
        if (steam is not null)
            boxes.AddRange(PowerExpansionController.ReserveGrowth(map, steam, state.Zones,
                map.Entities.Single(e => e.Id == map.Actor.Id).Force).Entities.Skip(map.Entities.Count).Select(e => e.Bounds));
        return FactoryGround.Reserve(map, boxes, Equipment.Belt);
    }

    private static string? Product(ProductionCatalog catalog, FactoryCell cell) => cell.Recipe is null ? null
        : catalog.Recipes.FirstOrDefault(r => r.Name == cell.Recipe)?.Products.FirstOrDefault(p => p.DeterministicItem)?.Name
            ?? (cell.IsResource ? cell.Recipe : null);
    internal async Task<SpatialSnapshot?> CaptureFuelFrameAsync(FactoryState state, FactorySnapshot snapshot,
        IReadOnlyList<string> entityIds, PowerExpansionController.SteamItems? steam, ProductionCatalog catalog,
        SpatialController controller, CancellationToken token)
    {
        if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Fuel frame factory scope changed.");
        if (PlanningCenter(snapshot, entityIds, 1, FuelPlanningRadius) is not { } center) return null;
        var spatial = new SpatialClient(game);
        var items = GeometryItems(state, steam);
        var map = await spatial.CaptureAsync(items, FuelPlanningRadius, token);
        if (map.Scope != catalog.Scope) throw new InvalidDataException("Fuel frame planning scope changed.");
        bool moved = false;
        if (!FrameCovered(map, entityIds))
        {
            var stand = PlanningStand(map, snapshot, entityIds, FuelPlanningRadius);
            if (stand is null)
            {
                // A remote actor's photograph need not include the feasible vantage rectangle. Approach a known own
                // endpoint first, then choose ground from the new native view; never walk blindly onto the center.
                foreach (string id in PlanningAnchors(snapshot, entityIds, center).Take(2))
                {
                    var position = Position(snapshot, id)!;
                    await journal.AppendAsync("power-fuel-frame-approach", new { id, position, map.CollectedTick }, token);
                    await controller.ApproachEntityAsync(id, position, catalog, token);
                    map = await spatial.CaptureAsync(items, FuelPlanningRadius, token);
                    if (map.Scope != catalog.Scope) throw new InvalidDataException("Fuel endpoint travel scope changed.");
                    moved = true;
                    if (FrameCovered(map, entityIds)) break;
                    stand = PlanningStand(map, snapshot, entityIds, FuelPlanningRadius);
                    if (stand is not null) break;
                }
                if (FrameCovered(map, entityIds))
                {
                    await journal.AppendAsync("power-fuel-frame", new { map.Scope, map.CollectedTick, map.Actor.Position,
                        map.Bounds, moved, requiredEntities = entityIds.Count }, token);
                    return map;
                }
            }
            if (stand is null) return null; // Observed endpoints still provide no known walkable vantage.
            await controller.TravelAsync(stand, 1, catalog, token);
            map = await spatial.CaptureAsync(items, FuelPlanningRadius, token);
            if (map.Scope != catalog.Scope) throw new InvalidDataException("Fuel frame travel scope changed.");
            if (!FrameCovered(map, entityIds)) return null;
            moved = true;
        }
        await journal.AppendAsync("power-fuel-frame", new { map.Scope, map.CollectedTick, map.Actor.Position,
            map.Bounds, moved, requiredEntities = entityIds.Count }, token);
        return map;
    }

    internal static IReadOnlyList<string> PlanningAnchors(FactorySnapshot snapshot, IReadOnlyList<string> entityIds, MapPosition center) =>
        entityIds.Distinct(StringComparer.Ordinal).Select(id => snapshot.Records.SingleOrDefault(r => r.Kind == "entity" && r.EntityId == id))
            .Where(r => r?.Data.TryGetProperty("role", out var role) == true && role.GetString() == "factory")
            .OrderBy(r => Position(snapshot, r!.EntityId)!.DistanceTo(center)).ThenBy(r => r!.EntityId, StringComparer.Ordinal)
            .Select(r => r!.EntityId).ToArray();

    internal static bool FrameCovered(SpatialSnapshot map, IReadOnlyList<string> entityIds) =>
        map.Coverage.Atomic && map.Coverage.Complete && entityIds.Count > 0 && entityIds.All(id =>
            map.Entities.SingleOrDefault(e => e.Id == id) is { } entity
            && map.Bounds.Contains(new WorldBox(new(entity.Bounds.Min.X - 4, entity.Bounds.Min.Y - 4),
                new(entity.Bounds.Max.X + 4, entity.Bounds.Max.Y + 4))));

    /// <summary>Choose known walkable ground inside the endpoint frame instead of insisting on its possibly occupied center.</summary>
    internal static MapPosition? PlanningStand(SpatialSnapshot map, FactorySnapshot snapshot, IReadOnlyList<string> entityIds, int observationRadius = 48)
    {
        if (map.Scope != snapshot.Scope) throw new InvalidDataException("Fuel vantage observations span actor scopes.");
        var center = PlanningCenter(snapshot, entityIds, 1, observationRadius);
        if (center is null) return null;
        var points = entityIds.Select(id => Position(snapshot, id)!).ToArray();
        int reach = observationRadius - 4 - 1;
        var feasible = new WorldBox(new(points.Max(p => p.X) - reach, points.Max(p => p.Y) - reach),
            new(points.Min(p => p.X) + reach, points.Min(p => p.Y) + reach));
        var field = new SpatialCollisionField(map);
        if (field.Walkable(center)) return center;
        var candidates = new List<MapPosition>();
        for (int y = (int)Math.Ceiling(Math.Max(feasible.Min.Y, map.Bounds.Min.Y) - .5);
            y + .5 <= Math.Min(feasible.Max.Y, map.Bounds.Max.Y); y++)
            for (int x = (int)Math.Ceiling(Math.Max(feasible.Min.X, map.Bounds.Min.X) - .5);
                x + .5 <= Math.Min(feasible.Max.X, map.Bounds.Max.X); x++)
                candidates.Add(new(x + .5, y + .5));
        return candidates.OrderBy(p => p.DistanceTo(center)).ThenBy(p => p.DistanceTo(map.Actor.Position)).FirstOrDefault(p => field.Walkable(p));
    }

    internal static MapPosition? Position(FactorySnapshot snapshot, string id) => snapshot.Records.FirstOrDefault(r => r.Kind == "entity" && r.EntityId == id)
        ?.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json);

    internal static string[] FrameEntities(FactoryState state, FactoryCell source, FactoryCell target, FactoryTransportBus? bus) =>
        [.. new[] { source.Entities["output-chest"], target.Entities["input-chest"] }
            .Concat(bus is null ? [] : state.Cells.Single(c => c.Id == bus.CellId).Entities.Values)
            .Concat(bus is null ? [] : bus.Consumers.Select(c => state.Cells.Single(t => t.Id == c.TargetCellId).Entities["input-chest"]))
            .Distinct(StringComparer.Ordinal)];

    /// <summary>One bounded native photograph must cover every endpoint and retained bus part, with room for approaches and routing.</summary>
    internal static MapPosition? PlanningCenter(FactorySnapshot snapshot, IReadOnlyList<string> entityIds, int approachTiles = 4, int observationRadius = 48)
    {
        if (approachTiles is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(approachTiles));
        if (observationRadius is < 8 or > SpatialSnapshot.MaximumRadius) throw new ArgumentOutOfRangeException(nameof(observationRadius));
        if (entityIds.Count == 0) return null;
        var positions = entityIds.Distinct(StringComparer.Ordinal).Select(id => Position(snapshot, id)).ToArray();
        if (positions.Any(p => p is null || !double.IsFinite(p.X) || !double.IsFinite(p.Y))) return null;
        double left = positions.Min(p => p!.X), right = positions.Max(p => p!.X);
        double top = positions.Min(p => p!.Y), bottom = positions.Max(p => p!.Y);
        // Keep four routing tiles on either side even when travel stops short of the requested center.
        // This is a bounded local proposal, not a claim that longer connections are impossible.
        int maximumSpan = 2 * (observationRadius - 4 - approachTiles);
        if (right - left > maximumSpan || bottom - top > maximumSpan) return null;
        return new(left + (right - left) / 2, top + (bottom - top) / 2);
    }
}
