using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Early solid production from observed resources and native recipes, with no injected stock.</summary>
public sealed class ProductionController(IGameClient game, IControllerJournal journal)
{
    public async Task<ProductionResult> ProduceAsync(string item, int targetStock, CancellationToken token = default,
        IReadOnlySet<string>? reservedEntityIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item);
        if (targetStock is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(targetStock));
        using var reservations = ProductionReservations.Enter(reservedEntityIds);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(45));
        await using var controller = new SpatialController(game, journal);
        var planner = new ProductionPlanner();
        var spatial = new SpatialClient(game);
        var exploration = new ExplorationPlanner();
        ProductionState initial = await ObserveAsync(deadline.Token);
        var receipts = new List<OperationReceipt>();
        for (int stepNumber = 0; stepNumber < 256; stepNumber++)
        {
            ProductionState state = await ObserveAsync(deadline.Token);
            if (state.Scope != initial.Scope) throw new InvalidOperationException("Production scope changed; reconcile death or pilot transition before resuming.");
            if (state.ControlMode != "ai") throw new InvalidOperationException("The pilot has manual control.");
            if (state.Inventory.GetValueOrDefault(item) >= targetStock)
            {
                var result = new ProductionResult(item, targetStock, initial.Tick, state.Tick,
                    initial.Inventory.GetValueOrDefault(item), state.Inventory.GetValueOrDefault(item), stepNumber, receipts.AsReadOnly());
                await journal.AppendAsync("production-result", result, deadline.Token);
                return result;
            }
            ProductionCatalog catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), deadline.Token));
            string[] drillItems = catalog.Items.Where(p => p.Value.PlaceEntityType == "mining-drill")
                .Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();
            if (drillItems.Length > 16) throw new InvalidOperationException("Mining drill geometry exceeds the snapshot budget.");
            SpatialSnapshot map = await spatial.CaptureAsync(drillItems, radius: 48, cancellationToken: deadline.Token);
            if (catalog.Scope != state.Scope || map.Scope != state.Scope) throw new InvalidDataException("Production observations span different actor scopes.");
            ProductionEntity? ready = state.AvailableOutput(item, reservedEntityIds);
            if (ready is not null)
            {
                await TravelAsync(ready.Position, 3, catalog);
                ProductionState arrived = await ObserveAsync(deadline.Token);
                if (arrived.Scope != state.Scope || arrived.ControlMode != "ai") throw new InvalidOperationException("Actor changed before stock collection.");
                long count = Math.Min(arrived.Entities.FirstOrDefault(e => e.Id == ready.Id)?.Count("output", item) ?? 0,
                    Math.Max(0, targetStock - arrived.Inventory.GetValueOrDefault(item)));
                if (count == 0) continue;
                await ActAsync("take", new
                {
                    entityId = ready.Id,
                    inventory = "output",
                    item,
                    count
                }, 600);
                continue;
            }
            ProductionEntity? engaged = state.Entities.FirstOrDefault(e => !ProductionReservations.Current.Contains(e.Id) && e.InventoryTotal("input") > 0
                && catalog.Recipes.Any(r => r.Name == e.Recipe && r.Products.Count == 1 && r.Products[0].Name == item
                    && r.Products[0].DeterministicItem && r.Ingredients.Count == 1 && r.Ingredients[0].DeterministicItem
                    && e.Count("input", r.Ingredients[0].Name) >= r.Ingredients[0].Amount!.Value)
                && catalog.Machines.Values.Any(m => m.EntityName == e.Name));
            if (engaged is not null)
            {
                NativeRecipe recipe = catalog.Recipes.Single(r => r.Name == engaged.Recipe);
                int supplied = FurnaceBatchSizing.SuppliedBatches(recipe, engaged.Count("input", recipe.Ingredients[0].Name),
                    state.Inventory.GetValueOrDefault(recipe.Ingredients[0].Name));
                int batches = FurnaceBatchSizing.Limit(recipe, catalog.Items, Math.Min(supplied,
                    checked((int)Math.Ceiling((targetStock - state.Inventory.GetValueOrDefault(item)) / recipe.Products[0].Amount!.Value))));
                await SmeltAsync(new("smelt", item, batches, recipe), state, catalog, map, engaged.Id);
                continue;
            }
            ProductionStep step = planner.Next(item, targetStock, state.Inventory, catalog, map,
                state.Entities.Where(e => !ProductionReservations.Current.Contains(e.Id)).Select(e => e.AsMachine()).ToArray(),
                allowExtractionPreparation: !SmeltingPreparationController.IsPreparing && !StoredResourceExtractionController.IsPreparing);
            await journal.AppendAsync("production-step", new { item, targetStock, stepNumber, state.Tick, step }, deadline.Token);
            switch (step.Kind)
            {
                case "extract":
                    await new StoredResourceExtractionController(game, journal).RunAsync(step.Item, step.Quantity, deadline.Token);
                    break;
                case "prepare-smelting":
                    await new SmeltingPreparationController(game, journal).PrepareAsync(step.Item, deadline.Token);
                    await new AutomatedSmeltingController(game, journal).RunAsync(step.Item, step.Quantity, deadline.Token);
                    break;
                case "assemble":
                    await new AssemblyController(game, journal).RunAsync(step.Item, step.Quantity, deadline.Token);
                    break;
                case "automate":
                    await new AutomatedSmeltingController(game, journal).RunAsync(step.Item, step.Quantity, deadline.Token);
                    break;
                case "build":
                    OperationReceipt built = await controller.BuildAsync(step.Item, map.Actor.Position, deadline.Token);
                    receipts.Add(built);
                    if (built.Status != "completed")
                        throw new InvalidOperationException($"Machine construction ended with {built.Status}: {built.Error?.Code}. Reconcile before replanning.");
                    break;
                case "mine":
                    await TravelAsync(step.Source!.Position, MiningDistance(step.Source, map), catalog);
                    await ActAsync("mine", new { name = step.Source.Name, position = step.Source.Position, count = step.Quantity }, 36000);
                    break;
                case "craft":
                    await ActAsync("craft", new { recipe = step.Recipe!.Name, count = step.Quantity },
                        HandcraftTiming.DeadlineTicks(step.Recipe, step.Quantity));
                    break;
                case "smelt":
                    await SmeltAsync(step, state, catalog, map);
                    break;
                case "unavailable":
                    ExplorationWaypoint next = await controller.FindExplorationWaypointAsync(exploration, catalog, step.Item, token: deadline.Token);
                    await journal.AppendAsync("exploration-frontier", new { step.Item, frontier = next.Position, next.CollectedTick }, deadline.Token);
                    await controller.NavigateAsync(next.Position, cancellationToken: deadline.Token);
                    break;
                default: throw new InvalidOperationException($"{step.Kind}: {step.Item}: {step.Reason}");
            }
        }
        throw new InvalidOperationException("Production exhausted its 256-step budget.");

        async Task<OperationReceipt> ActAsync(string kind, object args, long ticks)
        {
            OperationReceipt receipt = await controller.WorkAsync(kind, args, ticks, token: deadline.Token);
            receipts.Add(receipt);
            if (receipt.Status is not ("completed" or "partial") && receipt.Error?.Code != "cancelled")
                throw new InvalidOperationException($"Production action {kind} ended with {receipt.Status}: {receipt.Error?.Code}.");
            return receipt;
        }

        async Task TravelAsync(MapPosition position, double distance, ProductionCatalog catalog)
        {
            for (int segment = 0; segment < 64; segment++)
            {
                SpatialSnapshot currentMap = await spatial.CaptureAsync(cancellationToken: deadline.Token);
                if (currentMap.Actor.Position.DistanceTo(position) <= 24)
                {
                    await controller.NavigateAsync(position, distance, deadline.Token);
                    return;
                }
                ExplorationWaypoint next = await controller.FindExplorationWaypointAsync(exploration, catalog, "", position, deadline.Token);
                await journal.AppendAsync("travel-segment", new { position, waypoint = next.Position, next.CollectedTick }, deadline.Token);
                await controller.NavigateAsync(next.Position, cancellationToken: deadline.Token);
            }
            throw new InvalidOperationException("Travel to a known entity exhausted its local segment budget.");
        }

        async Task SmeltAsync(ProductionStep step, ProductionState state, ProductionCatalog catalog, SpatialSnapshot map, string? requiredId = null)
        {
            NativeRecipe recipe = step.Recipe!;
            if (recipe.Ingredients.Count != 1 || recipe.Products.Count != 1)
                throw new InvalidOperationException("Automatic furnace selection currently requires one solid ingredient and product.");
            var supported = catalog.Machines.Where(m => m.Value.Categories.ContainsKey(recipe.Category))
                .OrderBy(m => m.Key, StringComparer.Ordinal).ToArray();
            var owned = SelectFurnace(recipe, catalog, state, map.Actor.Position, requiredId);
            if (owned is null)
                throw new InvalidOperationException("The planned compatible furnace is unavailable; reconcile before replanning.");
            var machine = supported.First(m => m.Value.EntityName == owned.Name);
            string entityId = owned.Id;
            MapPosition position = owned.Position;
            await TravelAsync(position, 3, catalog);
            // Feed a bounded batch. Read actual output/fuel every iteration; never infer output from a timer.
            string input = recipe.Ingredients[0].Name;
            FactorySnapshot factory = await new FactorySnapshotClient(game).CaptureAsync([input], cancellationToken: deadline.Token);
            if (factory.Scope != state.Scope) throw new InvalidDataException("Furnace accounting scope changed.");
            if (catalog.Items[input].FuelValue > 0) throw new InvalidOperationException("Fuel ingredients need explicit compartment accounting.");
            FurnaceRequirements requirements = FurnaceRequirements.From(factory, entityId, recipe, step.Quantity);
            int missingInput = requirements.InputToInsert;
            await journal.AppendAsync("furnace-requirements", new { factory.SnapshotId, factory.CollectedTick, entityId, requirements }, deadline.Token);
            if (missingInput > 0)
            {
                string inputId = factory.Records.Single(r => r.Kind == "work" && r.EntityId == entityId).Data.GetProperty("inputInventoryId").GetString()
                    ?? throw new InvalidDataException("Missing native furnace input identity.");
                var inputInventory = factory.Records.Single(r => r.Kind == "inventory" && r.Id == inputId && r.EntityId == entityId);
                long insertable = inputInventory.Data.GetProperty("capacityHints").GetProperty(input).GetProperty("insertable").GetInt64();
                if (insertable < missingInput) throw new InvalidOperationException("Native furnace input capacity changed; reconcile the prepared batch before insertion.");
                ProductionState carried = await ObserveAsync(deadline.Token);
                if (carried.Scope != state.Scope || carried.ControlMode != "ai" || carried.Inventory.GetValueOrDefault(input) < missingInput)
                    throw new InvalidOperationException("The actor cannot supply the selected furnace batch; reconcile before insertion.");
                OperationReceipt inserted = await ActAsync("insert", new { entityId, inventory = "input", item = input, count = missingInput }, 600);
                if (inserted.Status != "completed") throw new InvalidOperationException("Furnace input was only partially transferred; re-observation is required.");
            }
            long initialOutput = state.Inventory.GetValueOrDefault(step.Item);
            long expectedOutput = checked((long)(recipe.Products[0].Amount!.Value * step.Quantity));
            int observationLimit = FurnaceBatchSizing.ObservationLimit(recipe, machine.Value.CraftingSpeed, step.Quantity);
            for (int attempt = 0; attempt < observationLimit; attempt++)
            {
                ProductionState current = await ObserveAsync(deadline.Token);
                ProductionEntity furnace = current.Entities.Single(e => e.Id == entityId);
                long output = furnace.Count("output", step.Item);
                if (output > 0)
                {
                    await TravelAsync(position, 3, catalog);
                    await ActAsync("take", new { entityId, inventory = "output", item = step.Item, count = output }, 600);
                }
                if (current.Inventory.GetValueOrDefault(step.Item) + output >= initialOutput + expectedOutput) return;
                if (furnace.InventoryTotal("fuel") == 0)
                {
                    FactorySnapshot burnerPhoto = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: deadline.Token);
                    if (burnerPhoto.Scope != state.Scope) throw new InvalidDataException("Furnace burner scope changed.");
                    if (!NativeBurnerStock.From(burnerPhoto, entityId).Empty)
                    {
                        await ActAsync("wait", new { ticks = 60 }, 180);
                        continue;
                    }
                    await controller.ApproachEntityAsync(entityId, position, catalog, deadline.Token);
                    map = await spatial.CaptureAsync(radius: 48, cancellationToken: deadline.Token);
                    if (map.Scope != state.Scope) throw new InvalidDataException("Furnace fuel geometry scope changed.");
                    bool bootstrap = SmeltingPreparationController.IsPreparing || StoredResourceExtractionController.IsPreparing;
                    var availableFuel = catalog.Items.Where(p => p.Value.FuelValue > 0).ToDictionary(p => p.Key,
                        p => checked(current.Inventory.GetValueOrDefault(p.Key) + current.Entities
                            .Where(e => e.Id != entityId && !ProductionReservations.Current.Contains(e.Id)).Sum(e => e.Count("output", p.Key))),
                        StringComparer.Ordinal);
                    int remainingBatches = Math.Max(1, checked((int)Math.Ceiling((initialOutput + expectedOutput
                        - current.Inventory.GetValueOrDefault(step.Item) - output) / recipe.Products[0].Amount!.Value)));
                    var fuelPlan = FurnaceFuelPlanner.Choose(recipe, machine.Value, map.Prototypes[furnace.Name], remainingBatches,
                        catalog, current.Inventory, availableFuel, bootstrap);
                    // During equipment bootstrap only, a cold furnace may need one manually obtained starter item.
                    int target = bootstrap ? checked((int)Math.Min(fuelPlan.Reserve, Math.Max(1, availableFuel.GetValueOrDefault(fuelPlan.Fuel))))
                        : fuelPlan.Reserve;
                    await journal.AppendAsync("furnace-fuel-plan", new { entityId, fuelPlan, target, bootstrap, map.CollectedTick }, deadline.Token);
                    using (ProductionReservations.Enter(new HashSet<string> { entityId }))
                        await new ProductionGoalExecutor(game, journal).RunAsync(fuelPlan.Fuel, target, deadline.Token);
                    await controller.ApproachEntityAsync(entityId, position, catalog, deadline.Token);
                    FactorySnapshot fuelStock = await new FactorySnapshotClient(game).CaptureAsync([fuelPlan.Fuel], cancellationToken: deadline.Token);
                    ProductionState supplied = await ObserveAsync(deadline.Token);
                    if (fuelStock.Scope != state.Scope || supplied.Scope != state.Scope)
                        throw new InvalidDataException("Actor changed while procuring furnace fuel.");
                    string fuelId = fuelStock.Records.Single(r => r.Kind == "entity" && r.EntityId == entityId).Data.GetProperty("fuelInventoryId").GetString()
                        ?? throw new InvalidDataException("Missing native furnace fuel inventory identity.");
                    var fuelInventory = fuelStock.Records.Single(r => r.Kind == "inventory" && r.Id == fuelId && r.EntityId == entityId);
                    long capacity = fuelInventory.Data.GetProperty("capacityHints").GetProperty(fuelPlan.Fuel).GetProperty("insertable").GetInt64();
                    long loaded = supplied.Entities.Single(e => e.Id == entityId).Count("fuel", fuelPlan.Fuel);
                    int count = checked((int)Math.Min(Math.Max(0, fuelPlan.Reserve - loaded),
                        Math.Min(capacity, supplied.Inventory.GetValueOrDefault(fuelPlan.Fuel))));
                    if (count > 0)
                    {
                        var inserted = await ActAsync("insert", new { entityId, inventory = "fuel", item = fuelPlan.Fuel, count }, 600);
                        if (inserted.Status != "completed") throw new InvalidOperationException("Furnace fuel transfer requires reconciliation.");
                    }
                }
                await ActAsync("wait", new { ticks = 60 }, 180);
            }
            throw new InvalidOperationException("Furnace output was not established within its observation budget.");
        }
    }

    internal static ProductionEntity? SelectFurnace(NativeRecipe recipe, ProductionCatalog catalog, ProductionState state,
        MapPosition actorPosition, string? requiredId) =>
        state.Entities.Where(e => !ProductionReservations.Current.Contains(e.Id)
            && (requiredId is null || e.Id == requiredId)
            && catalog.Machines.Values.Any(m => m.EntityName == e.Name && m.Categories.ContainsKey(recipe.Category)))
            .OrderBy(e => e.Position.DistanceTo(actorPosition)).FirstOrDefault(e => e.AsMachine().CanProcess(recipe));

    internal static double MiningDistance(SpatialEntity source, SpatialSnapshot map)
    {
        bool resource = map.Prototypes[source.Name].Type == "resource";
        double reach = resource
            ? map.Actor.ResourceReachDistance ?? throw new InvalidDataException("Native resource reach is required before manual mining.")
            : map.Actor.ReachDistance;
        if (!double.IsFinite(reach) || reach < .5) throw new InvalidDataException("Invalid native mining reach.");
        // Leave room for the native movement tolerance while allowing interaction beside an occupied deposit.
        // General reach is not a guarantee that a tree can be mined from that distance.
        return Math.Min(resource ? 10 : 3, reach - .3);
    }

    internal async Task<ProductionState> ObserveAsync(CancellationToken token)
    {
        GameResponse response = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 64, limit = 200 }), token);
        if (!response.Ok) throw new GameRpcException(response.Error!);
        JsonElement data = response.Data, agent = data.GetProperty("agent");
        if (!agent.GetProperty("alive").GetBoolean()) throw new InvalidOperationException("The actor died; production requires recovery.");
        if (data.GetProperty("collectedTick").GetInt64() != response.Tick
            || !data.GetProperty("coverage").GetProperty("knownInventoriesComplete").GetBoolean())
            throw new InvalidDataException("Production requires complete, current known entity inventories.");
        return new(data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!, response.Tick,
            agent.GetProperty("controlMode").GetString()!, agent.GetProperty("inventory").Deserialize<Dictionary<string, long>>(Protocol.Json)!,
            ReadEntities(data.GetProperty("entities")));
    }

    private static IReadOnlyList<ProductionEntity> ReadEntities(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object && !value.EnumerateObject().Any()) return [];
        return value.EnumerateArray().Select(e => new ProductionEntity(e.GetProperty("id").GetString()!,
            e.GetProperty("name").GetString()!, e.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!,
            e.TryGetProperty("recipe", out var recipe) ? recipe.GetString() : null, e.GetProperty("inventories").Clone(),
            e.TryGetProperty("previousRecipe", out var previous) ? previous.GetString() : null)).ToArray();
    }
}

public sealed record ProductionResult(string Item, int TargetStock, long StartTick, long EndTick, long InitialStock,
    long FinalStock, int Steps, IReadOnlyList<OperationReceipt> Receipts);
internal sealed record ProductionState(ActorScope Scope, long Tick, string ControlMode, IReadOnlyDictionary<string, long> Inventory,
    IReadOnlyList<ProductionEntity> Entities)
{
    public ProductionEntity? AvailableOutput(string item, IReadOnlySet<string>? reservedEntityIds = null) =>
        Entities.FirstOrDefault(e => !ProductionReservations.Current.Contains(e.Id) && reservedEntityIds?.Contains(e.Id) != true && e.Count("output", item) > 0);
}
internal sealed record ProductionEntity(string Id, string Name, MapPosition Position, string? Recipe, JsonElement Inventories, string? PreviousRecipe = null)
{
    public KnownProductionMachine AsMachine() => new(Id, Name, Recipe, Items("input"), Items("output"));
    public IReadOnlyDictionary<string, long> Items(string slot) => Inventories.TryGetProperty(slot, out var inventory)
        ? inventory.GetProperty("items").Deserialize<Dictionary<string, long>>(Protocol.Json)!
        : new Dictionary<string, long>();
    public long Count(string slot, string item) => Inventories.TryGetProperty(slot, out var inventory)
        && inventory.GetProperty("items").TryGetProperty(item, out var amount) ? amount.GetInt64() : 0;
    public long InventoryTotal(string slot) => Inventories.TryGetProperty(slot, out var inventory)
        ? inventory.GetProperty("items").EnumerateObject().Sum(i => i.Value.GetInt64()) : 0;
}
