using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record FuelFeederResult(string BoilerId, string ChestId, string InserterId, string Fuel,
    long StartTick, long EndTick, long Delivered, long RemainingSource, int PoweredSamples, int Samples);

/// <summary>Installs a native chest-to-boiler inserter, then observes finite unattended fuel delivery.</summary>
public sealed class FuelFeederController(IGameClient game, IControllerJournal journal)
{
    public async Task<FuelFeederResult> RunAsync(string boilerId, int reserve = 50, int observeTicks = 3600, CancellationToken token = default)
    {
        if (reserve is < 5 or > 500 || observeTicks is < 120 or > 36000) throw new ArgumentOutOfRangeException(nameof(reserve));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(45));
        token = deadline.Token;
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var production = new ProductionController(game, journal);
        ProductionState owned = await ObserveAsync();
        var target = owned.Entities.Single(e => e.Id == boilerId);
        bool Available(KeyValuePair<string, NativeItem> p) => owned.Inventory.GetValueOrDefault(p.Key) > 0
            || catalog.Recipes.Any(r => r.Enabled && catalog.CanHandCraft(r) && r.Products.Any(m => m.Name == p.Key && m.DeterministicItem));
        string[] containers = catalog.Items.Where(p => p.Value.PlaceEntityType == "container" && Available(p)).OrderByDescending(p => owned.Inventory.GetValueOrDefault(p.Key) > 0)
            .ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).ToArray();
        string[] arms = catalog.Items.Where(p => p.Value.PlaceEntityType == "inserter" && Available(p)).OrderByDescending(p => owned.Inventory.GetValueOrDefault(p.Key) > 0)
            .ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).ToArray();
        string? poleItem = catalog.Items.Where(p => p.Value.PlaceEntityType == "electric-pole" && Available(p))
            .OrderByDescending(p => owned.Inventory.GetValueOrDefault(p.Key) > 0).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).FirstOrDefault();
        string[] items = containers.Concat(arms).Concat(poleItem is null ? [] : new[] { poleItem }).ToArray();
        if (items.Length is 0 or > 16) throw new InvalidOperationException("Fuel-feeder equipment exceeds the local geometry budget.");
        await using var controller = new SpatialController(game, journal);
        await controller.ApproachEntityAsync(boilerId, target.Position, catalog, token);
        var spatial = new SpatialClient(game);
        SpatialSnapshot map = await MapAsync();
        var boiler = map.Entities.Single(e => e.Id == boilerId);
        var generator = map.Entities.FirstOrDefault(e => map.Prototypes[e.Name].Type == "generator" && e.Power?.NetworkId is not null
            && (boiler.FluidConnections?.Any(p => p.TargetEntityId == e.Id) == true || e.FluidConnections?.Any(p => p.TargetEntityId == boilerId) == true))
            ?? throw new InvalidOperationException("The boiler has no observed connected steam generator.");
        long networkId = generator.Power!.NetworkId!.Value;
        var choices = (from chest in containers
                       from arm in arms
                       where map.Prototypes[map.Items[arm].EntityName].IsElectric
                       let plan = new FuelFeederPlanner().Find(map, chest, arm, boilerId, networkId, poleItem)
                       where plan is not null
                       select new { Chest = chest, Arm = arm, Plan = plan }).ToArray();
        var choice = choices.OrderByDescending(c => c.Plan!.ExistingInserterId is not null).FirstOrDefault()
            ?? throw new InvalidOperationException("No clear powered chest-and-inserter layout can feed this boiler in the observed area.");
        FuelFeederPlan selected = choice.Plan!;
        string fuel = catalog.Items.Where(p => p.Value.FuelValue > 0 && p.Value.FuelCategory is { } category
            && map.Prototypes[boiler.Name].FuelCategories?.ContainsKey(category) == true
            && catalog.Mining.Values.Any(products => products.Any(m => m.Name == p.Key && m.DeterministicItem)))
            .OrderByDescending(p => owned.Inventory.GetValueOrDefault(p.Key) > 0).ThenBy(p => p.Key, StringComparer.Ordinal).First().Key;
        await journal.AppendAsync("fuel-feeder-plan", new { boilerId, networkId, fuel, reserve, choice, map.Scope, map.CollectedTick }, token);
        var producer = new ProductionGoalExecutor(game, journal);
        var builder = new PoweredMachineController(game, journal);
        string chestId, inserterId;
        if (selected.ExistingContainerId is not null && selected.ExistingInserterId is not null)
        {
            chestId = selected.ExistingContainerId;
            inserterId = selected.ExistingInserterId;
        }
        else
        {
            await producer.RunAsync(choice.Chest, 1, token);
            await producer.RunAsync(choice.Arm, 1, token);
            if (selected.Pole is not null)
            {
                await producer.RunAsync(poleItem!, 1, token);
                string poleId = await builder.BuildAtAsync(poleItem!, selected.Pole, catalog, controller, token,
                    [selected.Container.Position, selected.Inserter.Position, boiler.Position]);
                map = await MapAsync();
                if (map.Entities.Single(e => e.Id == poleId).Power?.NetworkId != networkId)
                    throw new InvalidDataException("The feeder pole did not join the planned native electric network.");
            }
            chestId = await builder.BuildAtAsync(choice.Chest, selected.Container, catalog, controller, token, [selected.Inserter.Position, boiler.Position]);
            inserterId = await builder.BuildAtAsync(choice.Arm, selected.Inserter, catalog, controller, token, [selected.Container.Position, boiler.Position]);
        }
        owned = await ObserveAsync();
        var chestState = owned.Entities.Single(e => e.Id == chestId);
        if (chestState.InventoryTotal("chest") != chestState.Count("chest", fuel))
            throw new InvalidDataException("The feeder chest contains unrelated items; refuse to mix its supply.");
        int missing = checked((int)Math.Max(0, reserve - chestState.Count("chest", fuel)));
        await producer.RunAsync(fuel, Math.Max(1, missing + 2), token);
        await controller.ApproachEntityAsync(chestId, selected.Container.Position, catalog, token);
        var factory = new FactorySnapshotClient(game);
        FactorySnapshot capacity = await factory.CaptureAsync([fuel], cancellationToken: token);
        RequireScope(capacity.Scope);
        var inventory = capacity.Records.Single(r => r.EntityId == chestId && r.Kind == "inventory");
        if (inventory.Data.GetProperty("capacityHints").GetProperty(fuel).GetProperty("insertable").GetInt64() < missing)
            throw new InvalidOperationException("The native chest capacity cannot hold the requested fuel reserve.");
        if (missing > 0) await ActAsync("insert", new { entityId = chestId, inventory = "chest", item = fuel, count = missing });
        owned = await ObserveAsync();
        int startup = (int)Math.Max(0, 2 - owned.Entities.Single(e => e.Id == boilerId).Count("fuel", fuel));
        if (startup > 0)
        {
            await controller.ApproachEntityAsync(boilerId, boiler.Position, catalog, token);
            await ActAsync("insert", new { entityId = boilerId, inventory = "fuel", item = fuel, count = startup });
        }
        await ActAsync("wait", new { ticks = 120 });
        FactorySnapshot initial = await factory.CaptureAsync(cancellationToken: token);
        RequireScope(initial.Scope);
        FeederStockReading before = FeederStockReading.From(initial, chestId, inserterId, fuel);
        int powered = 0, samples = 0;
        for (int attempt = 0; attempt <= observeTicks / 60 + 60; attempt++)
        {
            map = await MapAsync();
            SpatialEntity arm = map.Entities.Single(e => e.Id == inserterId);
            if (arm.PickupTargetId != chestId || arm.DropTargetId != boilerId || arm.Power?.NetworkId != networkId
                || map.Entities.Any(e => e.Id != inserterId && e.PickupTargetId == chestId))
                throw new InvalidDataException("The native feeder endpoints or exclusive source changed.");
            FactorySnapshot current = await factory.CaptureAsync(cancellationToken: token);
            RequireScope(current.Scope);
            FeederStockReading reading = FeederStockReading.From(current, chestId, inserterId, fuel);
            long delivered = reading.DeliveredSince(before);
            bool generating = map.Entities.Single(e => e.Id == generator.Id).Power?.GeneratedLastTick > 0;
            samples++;
            if (generating) powered++;
            await journal.AppendAsync("fuel-feeder-measurement", new
            {
                current.SnapshotId,
                current.CollectedTick,
                chestId,
                inserterId,
                boilerId,
                reading,
                delivered,
                generating,
                arm.Power
            }, token);
            if (current.CollectedTick - initial.CollectedTick >= observeTicks)
            {
                if (delivered <= 0 || powered == 0) throw new InvalidOperationException("The observation window did not prove native fuel delivery and power generation.");
                var result = new FuelFeederResult(boilerId, chestId, inserterId, fuel, initial.CollectedTick, current.CollectedTick,
                    delivered, reading.Source, powered, samples);
                await journal.AppendAsync("fuel-feeder-result", result, token);
                return result;
            }
            await ActAsync("wait", new { ticks = 60 });
        }
        throw new TimeoutException("Fuel feeder verification exhausted its observation budget.");

        void RequireScope(ActorScope scope)
        {
            if (scope != catalog.Scope) throw new InvalidDataException("Actor scope changed during fuel-feeder execution.");
        }
        async Task<ProductionState> ObserveAsync()
        {
            var value = await production.ObserveAsync(token);
            RequireScope(value.Scope);
            if (value.ControlMode != "ai") throw new InvalidOperationException("The pilot has manual control.");
            return value;
        }
        async Task<SpatialSnapshot> MapAsync()
        {
            var value = await spatial.CaptureAsync(items, 48, token);
            RequireScope(value.Scope);
            return value;
        }
        async Task ActAsync(string kind, object args)
        {
            var receipt = await controller.WorkAsync(kind, args, 600, token: token);
            if (receipt.Status != "completed") throw new InvalidOperationException($"Fuel-feeder action ended with {receipt.Status}; reconcile native effects.");
        }
    }
}
