using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Loads several native furnaces before waiting; nested procurement remains under the same actor owner.</summary>
internal sealed class FurnaceFleetController(IGameClient game, IControllerJournal journal)
{
    private static readonly AsyncLocal<bool> Running = new();
    internal static bool IsRunning => Running.Value;

    public async Task<bool> TryRunAsync(NativeRecipe recipe, string item, int targetStock, ProductionCatalog catalog, CancellationToken token)
    {
        if (Running.Value || recipe.Ingredients.Count != 1 || recipe.Products.Count != 1
            || !recipe.Ingredients[0].DeterministicItem || !recipe.Products[0].DeterministicItem
            || catalog.Items[recipe.Ingredients[0].Name].FuelValue > 0) return false;
        var production = new ProductionController(game, journal);
        var initial = await production.ObserveAsync(token);
        if (initial.Scope != catalog.Scope || initial.ControlMode != "ai") throw new InvalidDataException("Furnace fleet scope changed.");
        long missing = targetStock - initial.Inventory.GetValueOrDefault(item);
        if (missing <= 0) return true;
        var option = catalog.Machines.Where(m => m.Value.Categories.ContainsKey(recipe.Category) && m.Value.FuelCategories.Count > 0
                && (initial.Inventory.GetValueOrDefault(m.Key) > 0 || catalog.Recipes.Any(r => r.Enabled
                    && r.Products.Any(p => p.Name == m.Key && p.DeterministicItem))))
            .OrderByDescending(m => initial.Entities.Any(e => e.Name == m.Value.EntityName && e.AsMachine().CanProcess(recipe)
                && !ProductionReservations.Current.Contains(e.Id)))
            .ThenByDescending(m => m.Value.CraftingSpeed).ThenBy(m => m.Key, StringComparer.Ordinal).FirstOrDefault();
        if (option.Key is null) return false;
        int batches = checked((int)Math.Ceiling(missing / recipe.Products[0].Amount!.Value));
        int desired = FurnaceFleetPlanner.RequiredMachines(recipe, batches, option.Value.CraftingSpeed);
        if (desired < 2) return false;
        Running.Value = true;
        try { await RunCoreAsync(option.Key, option.Value, desired, initial); }
        finally { Running.Value = false; }
        return true;

        async Task RunCoreAsync(string furnaceItem, NativeFurnace machine, int count, ProductionState start)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(45));
            var cancellation = deadline.Token;
            await using var controller = new SpatialController(game, journal);
            var spatial = new SpatialClient(game);
            var firstMap = await MapAsync();
            MapPosition anchor = firstMap.Actor.Position;
            var fleet = start.Entities.Where(e => e.Name == machine.EntityName && e.AsMachine().CanProcess(recipe)
                    && !ProductionReservations.Current.Contains(e.Id))
                .OrderBy(e => e.Position.DistanceTo(anchor)).Take(FurnaceFleetPlanner.MaximumMachines).Select(e => e.Id).ToList();
            await journal.AppendAsync("furnace-fleet-start", new { recipe = recipe.Name, item, targetStock, furnaceItem,
                desired = count, existing = fleet, start.Tick, start.Scope }, cancellation);
            while (fleet.Count < count)
            {
                using (ProductionReservations.Enter(fleet.ToHashSet(StringComparer.Ordinal)))
                    await new ProductionGoalExecutor(game, journal).RunAsync(furnaceItem, 1, cancellation);
                await controller.TravelAsync(anchor, 8, catalog, cancellation);
                var map = await MapAsync();
                var state = await ObserveAsync();
                var access = fleet.Select(id => state.Entities.Single(e => e.Id == id).Position).ToList();
                var site = await ControllerPlanning.RunAsync(planningToken =>
                {
                    var field = new SpatialCollisionField(map);
                    var placement = new PlacementPlanner();
                    MapPosition exit = FindEscape(field, planningToken);
                    var targets = access.Append(exit).ToArray();
                    foreach (var candidate in placement.FindCandidates(field, furnaceItem, anchor))
                    {
                        planningToken.ThrowIfCancellationRequested();
                        if (placement.FindApproach(field, furnaceItem, candidate, targets) is not null) return (Candidate: candidate, Escape: exit);
                    }
                    throw new InvalidOperationException("No native furnace placement preserves service and escape access.");
                }, controller, TimeSpan.FromMinutes(2), cancellation);
                var candidate = site.Candidate;
                MapPosition escape = site.Escape;
                access.Add(escape);
                string id = await new PoweredMachineController(game, journal).BuildAtAsync(furnaceItem, candidate, catalog, controller, cancellation, access);
                fleet.Add(id);
                await journal.AppendAsync("furnace-fleet-built", new { entityId = id, candidate, escape }, cancellation);
            }

            string ingredient = recipe.Ingredients[0].Name;
            for (int iteration = 0; iteration < 1800; iteration++)
            {
                var state = await ObserveAsync();
                foreach (string id in fleet)
                {
                    var member = Member(state, id);
                    if (member.Count("output", item) == 0) continue;
                    await controller.ApproachEntityAsync(id, member.Position, catalog, cancellation);
                    state = await ObserveAsync();
                    long take = Math.Min(Member(state, id).Count("output", item), Math.Max(0, targetStock - state.Inventory.GetValueOrDefault(item)));
                    if (take > 0) await WorkAsync("take", new { entityId = id, inventory = "output", item, count = take });
                }
                state = await ObserveAsync();
                if (state.Inventory.GetValueOrDefault(item) >= targetStock)
                {
                    await journal.AppendAsync("furnace-fleet-result", new { item, targetStock, startTick = start.Tick, endTick = state.Tick,
                        initialStock = start.Inventory.GetValueOrDefault(item), finalStock = state.Inventory.GetValueOrDefault(item), furnaces = fleet }, cancellation);
                    return;
                }
                var snapshot = await SnapshotAsync([ingredient]);
                var plan = Plan(snapshot, state);
                int inputTarget = plan.Loads.Sum(l => l.InputToInsert);
                if (inputTarget > state.Inventory.GetValueOrDefault(ingredient))
                {
                    using (ProductionReservations.Enter(fleet.ToHashSet(StringComparer.Ordinal)))
                        await new ProductionGoalExecutor(game, journal).RunAsync(ingredient, inputTarget, cancellation);
                }
                foreach (string id in fleet)
                {
                    state = await ObserveAsync();
                    await controller.ApproachEntityAsync(id, Member(state, id).Position, catalog, cancellation);
                    snapshot = await SnapshotAsync([ingredient]);
                    state = await ObserveAsync();
                    plan = Plan(snapshot, state);
                    var load = plan.Loads.Single(l => l.EntityId == id);
                    int quantity = checked((int)Math.Min(load.InputToInsert, state.Inventory.GetValueOrDefault(ingredient)));
                    if (quantity > 0)
                        await WorkAsync("insert", new { entityId = id, inventory = "input", item = ingredient, count = quantity });
                }

                snapshot = await SnapshotAsync([ingredient]);
                state = await ObserveAsync();
                plan = Plan(snapshot, state);
                var cold = plan.Loads.Where(l => l.TotalCycles > 0 && NativeBurnerStock.From(snapshot, l.EntityId).Empty).ToArray();
                if (cold.Length > 0)
                {
                    var map = await MapAsync();
                    var available = catalog.Items.Where(p => p.Value.FuelValue > 0).ToDictionary(p => p.Key,
                        p => checked(state.Inventory.GetValueOrDefault(p.Key) + state.Entities.Where(e => !fleet.Contains(e.Id)
                            && !ProductionReservations.Current.Contains(e.Id)).Sum(e => e.Count("output", p.Key))), StringComparer.Ordinal);
                    var fuelPlan = FurnaceFuelPlanner.ChooseFleet(recipe, machine, map.Prototypes[machine.EntityName],
                        cold.Select(l => l.TotalCycles).ToArray(), catalog, state.Inventory, available, false);
                    await journal.AppendAsync("furnace-fleet-supply", new { snapshot.CollectedTick, plan, cold = cold.Select(l => l.EntityId), fuelPlan }, cancellation);
                    using (ProductionReservations.Enter(fleet.ToHashSet(StringComparer.Ordinal)))
                        await new ProductionGoalExecutor(game, journal).RunAsync(fuelPlan.Fuel, fuelPlan.Reserve, cancellation);
                    foreach (var load in cold)
                    {
                        state = await ObserveAsync();
                        if (state.Inventory.GetValueOrDefault(fuelPlan.Fuel) == 0) break;
                        await controller.ApproachEntityAsync(load.EntityId, Member(state, load.EntityId).Position, catalog, cancellation);
                        var fuelStock = await SnapshotAsync([fuelPlan.Fuel]);
                        state = await ObserveAsync();
                        if (!NativeBurnerStock.From(fuelStock, load.EntityId).Empty) continue;
                        var fuelCatalog = catalog with { Items = new Dictionary<string, NativeItem> { [fuelPlan.Fuel] = catalog.Items[fuelPlan.Fuel] } };
                        int reserve = FurnaceFuelPlanner.Choose(recipe, machine, map.Prototypes[machine.EntityName], load.TotalCycles,
                            fuelCatalog, state.Inventory, state.Inventory, false).Reserve;
                        var entity = fuelStock.Records.Single(r => r.Kind == "entity" && r.EntityId == load.EntityId);
                        string inventoryId = entity.Data.GetProperty("fuelInventoryId").GetString()!;
                        var inventory = fuelStock.Records.Single(r => r.Kind == "inventory" && r.EntityId == load.EntityId && r.Id == inventoryId);
                        long capacity = inventory.Data.GetProperty("capacityHints").GetProperty(fuelPlan.Fuel).GetProperty("insertable").GetInt64();
                        int quantity = checked((int)Math.Min(reserve, Math.Min(capacity, state.Inventory.GetValueOrDefault(fuelPlan.Fuel))));
                        if (quantity > 0) await WorkAsync("insert", new { entityId = load.EntityId, inventory = "fuel", item = fuelPlan.Fuel, count = quantity });
                    }
                }
                var running = await SnapshotAsync([ingredient]);
                await journal.AppendAsync("furnace-fleet-observation", new { running.CollectedTick,
                    machines = fleet.Select(id => FurnaceFleetPlanner.Read(running, id, recipe)) }, cancellation);
                await WorkAsync("wait", new { ticks = 120 });
            }
            throw new TimeoutException("The furnace fleet exhausted its observation budget.");

            FurnaceFleetPlan Plan(FactorySnapshot snapshot, ProductionState state) => FurnaceFleetPlanner.Plan(recipe, targetStock,
                state.Inventory.GetValueOrDefault(item), fleet.Select(id => FurnaceFleetPlanner.Read(snapshot, id, recipe)).ToArray());
            ProductionEntity Member(ProductionState state, string id)
            {
                var member = state.Entities.Single(e => e.Id == id);
                if (member.Name != machine.EntityName || !member.AsMachine().CanProcess(recipe))
                    throw new InvalidDataException("A furnace fleet member changed or contains an incompatible recipe.");
                return member;
            }
            async Task<ProductionState> ObserveAsync()
            {
                var value = await production.ObserveAsync(cancellation);
                if (value.Scope != catalog.Scope || value.ControlMode != "ai") throw new InvalidDataException("Furnace fleet actor changed.");
                return value;
            }
            async Task<SpatialSnapshot> MapAsync()
            {
                var map = await spatial.CaptureAsync([furnaceItem], radius: 48, cancellationToken: cancellation);
                if (map.Scope != catalog.Scope) throw new InvalidDataException("Furnace fleet map scope changed.");
                return map;
            }
            async Task<FactorySnapshot> SnapshotAsync(string[] items)
            {
                var value = await new FactorySnapshotClient(game).CaptureAsync(items, cancellationToken: cancellation);
                if (value.Scope != catalog.Scope) throw new InvalidDataException("Furnace fleet stock scope changed.");
                return value;
            }
            async Task WorkAsync(string kind, object arguments)
            {
                var receipt = await controller.WorkAsync(kind, arguments, 600, token: cancellation);
                if (receipt.Status != "completed") throw new InvalidOperationException($"Fleet {kind} ended with {receipt.Status}; reconcile partial effects.");
            }
        }
    }

    private static MapPosition FindEscape(SpatialCollisionField field, CancellationToken token)
    {
        var origin = field.Map.Actor.Position;
        for (int x = (int)Math.Ceiling(origin.X - 24); x <= origin.X + 24; x++)
            for (int y = (int)Math.Ceiling(origin.Y - 24); y <= origin.Y + 24; y++)
            {
                token.ThrowIfCancellationRequested();
                var point = new MapPosition(x, y);
                if (point.DistanceTo(origin) is >= 20 and <= 24 && field.Walkable(point)
                    && new RoutePlanner().Find(field, point).Status == RouteStatus.Found) return point;
            }
        throw new InvalidOperationException("No observed escape route for furnace expansion.");
    }
}
