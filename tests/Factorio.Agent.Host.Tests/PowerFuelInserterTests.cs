using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class PowerFuelInserterTests
{
    private static readonly BeltTransportEquipment Equipment = new("transport-belt", "inserter", "small-electric-pole");
    private const string LongArm = "long-handed-inserter";

    [Theory]
    [InlineData(true, true, "inserter", "item", LongArm)]
    [InlineData(false, true, "inserter", "item", null)]
    [InlineData(true, false, "inserter", "item", null)]
    [InlineData(true, true, "container", "item", null)]
    [InlineData(true, true, "inserter", "fluid", null)]
    public void AlternativeRequiresUnlockedDeterministicNativeConstruction(bool enabled, bool present,
        string type, string productType, string? expected)
    {
        var catalog = Catalogs.Early();
        var items = new Dictionary<string, NativeItem>(catalog.Items);
        if (present) items[LongArm] = new(0, 50, PlaceEntity: LongArm, PlaceEntityType: type);
        catalog = catalog with { Items = items, Recipes = [.. catalog.Recipes,
            new("long-arm", enabled, "crafting", 1, [], [new(LongArm, productType, 1)], false)] };
        Assert.Equal(expected, PowerFuelTransport.SelectAlternativeInserter(catalog));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void InaccessibleOrdinaryPortsUseTheSameMapForLongArmsAndPersistTheirActualItems(int count)
    {
        var map = Map(blockOrdinary: true);
        var requests = Requests(count);
        var ordinary = PowerFuelTransport.SearchBatch(map, Equipment, requests, default, default, count)!;
        Assert.Equal(0, ordinary.AssignmentUpperBound);
        Assert.Equal(0, ordinary.Searches);
        var result = PowerFuelTransport.SearchWithInserterFallback(map, Equipment, requests, LongArm, default, default, count);
        Assert.Equal(LongArm, result.Equipment.Inserter);
        Assert.NotNull(result.Plan);
        Assert.Equal(count, result.Plan.Links.Count);
        Assert.False(result.Plan.BudgetExhausted);
        Assert.InRange(result.Plan.Searches, 1, 64);
        foreach (var link in result.Plan.Links)
        {
            var record = FactoryTransportBuilder.NewBus(link.SourceId, link.TargetId, "coal", 200,
                link.Plan, map.CollectedTick, result.Equipment);
            Assert.Equal(LongArm, record.Cell.Plan!["source-inserter"].Item);
            Assert.Equal(LongArm, record.Cell.Plan["target-inserter-0"].Item);
            Assert.All(link.Plan.Belts, p => Assert.True(new SpatialCollisionField(map)
                .PlacementClear(map.Prototypes[Equipment.Belt], p.Position, p.Direction)));
        }
        Assert.Equal("inserter", Equipment.Inserter);
    }

    [Fact]
    public void AccessibleOrdinaryPortsKeepTheOrdinaryEquipment()
    {
        var result = PowerFuelTransport.SearchWithInserterFallback(Map(false), Equipment, Requests(2), LongArm, default, default);
        Assert.Equal(Equipment, result.Equipment);
        Assert.Equal(2, result.Plan!.Links.Count);
    }

    [Fact]
    public void FailedRouteWithAccessiblePortsDoesNotSpendAnotherBudgetOnLongArms()
    {
        var map = Map(false);
        map = map with { Entities = map.Entities.Select(e => e.Power is null ? e : e with { Power = null }).ToArray() };
        var result = PowerFuelTransport.SearchWithInserterFallback(map, Equipment, Requests(1), LongArm, default, default, 1);
        Assert.Equal(1, result.Plan!.AssignmentUpperBound);
        Assert.True(result.Plan.Searches > 0);
        Assert.Empty(result.Plan.Links);
        Assert.Equal(Equipment, result.Equipment);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("no-filter")]
    [InlineData("no-power")]
    [InlineData("no-pickup")]
    [InlineData("no-drop")]
    public void UnobservedOrUnsupportedLongArmNeverReachesConstruction(string invalid)
    {
        var map = Map(true);
        var geometry = map.Prototypes[LongArm];
        var prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
        {
            [LongArm] = invalid switch
            {
                "no-filter" => geometry with { FilterSlots = null },
                "no-power" => geometry with { IsElectric = false },
                "no-pickup" => geometry with { InserterPickup = null },
                "no-drop" => geometry with { InserterDrop = null },
                _ => geometry
            }
        };
        var items = new Dictionary<string, PlaceableItem>(map.Items);
        if (invalid == "missing") items.Remove(LongArm);
        var result = PowerFuelTransport.SearchWithInserterFallback(map with { Items = items, Prototypes = prototypes },
            Equipment, Requests(1), LongArm, default, default, 1);
        Assert.Equal(Equipment, result.Equipment);
        Assert.Empty(result.Plan!.Links);
    }

    [Fact]
    public void SharedPlanningDeadlineReturnsNoPlanAndCallerCancellationStillPropagates()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var expired = PowerFuelTransport.SearchWithInserterFallback(Map(true), Equipment, Requests(1), LongArm,
            cancelled.Token, default, 1);
        Assert.Null(expired.Plan);
        Assert.Equal(Equipment, expired.Equipment);
        Assert.ThrowsAny<OperationCanceledException>(() => PowerFuelTransport.SearchWithInserterFallback(Map(true), Equipment,
            Requests(1), LongArm, cancelled.Token, cancelled.Token, 1));
    }

    [Fact]
    public void GeometryRequestIncludesTheAlternativeWithoutReplacingRequiredGroundGeometry()
    {
        var state = new FactoryState(1, "world", [], []);
        var ordinary = FactoryTransportBuilder.GeometryItems(state, undergroundBelt: "underground-belt");
        var expanded = FactoryTransportBuilder.GeometryItems(state, undergroundBelt: "underground-belt", alternativeInserter: LongArm);
        Assert.All(ordinary, item => Assert.Contains(item, expanded));
        Assert.Contains(LongArm, expanded);
        Assert.Equal(expanded.Length, expanded.Distinct().Count());
    }

    [Fact]
    public void DenseObservedConsumerIsDeferredWithoutDiscardingOtherConsumers()
    {
        var map = BlockTarget(Map(false), 1, 2);
        var unavailable = PowerFuelTransport.InaccessibleFuelTargets(map, Equipment, ["target-0", "target-1"], LongArm);
        Assert.Equal(new[] { "target-0" }, unavailable);
        Assert.Equal(new[] { "target-0" }, BeltTransportBatchPlanner.InaccessibleTargets(map, Equipment,
            ["target-0", "target-1"]));
        Assert.Equal(new[] { "target-0" }, BeltTransportBatchPlanner.InaccessibleTargets(map,
            Equipment with { Inserter = LongArm }, ["target-0", "target-1"]));
    }

    [Fact]
    public void NativeLongArmAccessPreventsDeferringAnOrdinaryBlockedConsumer()
    {
        var map = BlockTarget(Map(false), 1);
        Assert.Contains("target-0", BeltTransportBatchPlanner.InaccessibleTargets(map, Equipment, ["target-0"]));
        Assert.Empty(PowerFuelTransport.InaccessibleFuelTargets(map, Equipment, ["target-0"], LongArm));
        Assert.Contains("target-0", PowerFuelTransport.InaccessibleFuelTargets(map, Equipment, ["target-0"], null));
    }

    [Fact]
    public void UnavailableSourcesDoNotDeclareFreeConsumersInaccessible()
    {
        var map = Map(true);
        Assert.Equal(0, PowerFuelTransport.SearchBatch(map, Equipment, Requests(1), default, default)!.AssignmentUpperBound);
        Assert.Empty(PowerFuelTransport.InaccessibleFuelTargets(map, Equipment, ["target-0", "target-1"], LongArm));
    }

    [Fact]
    public void ANewNativePhotographRetriesAConsumerWhoseObstructionsWereRemoved()
    {
        var before = BlockTarget(Map(false), 1, 2);
        Assert.Contains("target-0", PowerFuelTransport.InaccessibleFuelTargets(before, Equipment, ["target-0"], LongArm));
        var after = before with { CollectedTick = before.CollectedTick + 1,
            Entities = before.Entities.Where(e => !e.Id.StartsWith("target-obstruction-", StringComparison.Ordinal)).ToArray() };
        Assert.Empty(PowerFuelTransport.InaccessibleFuelTargets(after, Equipment, ["target-0"], LongArm));
    }

    private static SpatialSnapshot BlockTarget(SpatialSnapshot map, params int[] distances)
    {
        var target = map.Entities.Single(e => e.Id == "target-0");
        var geometry = map.Prototypes["iron-chest"];
        var added = distances.SelectMany(d => new MapPosition[] { new(0, -d), new(0, d), new(-d, 0), new(d, 0) })
            .Select((offset, i) => new SpatialEntity($"target-obstruction-{i}", geometry.Name,
                new(target.Position.X + offset.X, target.Position.Y + offset.Y),
                geometry.CollisionBox.Translate(new(target.Position.X + offset.X, target.Position.Y + offset.Y)), 0, "own"));
        return map with { Entities = [.. map.Entities, .. added] };
    }

    private static BeltTransportRequest[] Requests(int count) => Enumerable.Range(0, count)
        .Select(i => new BeltTransportRequest($"target-{i}", [$"source-{i}"])).ToArray();

    private static SpatialSnapshot Map(bool blockOrdinary)
    {
        // Synthetic powered endpoints and reservations. Native placement, stop/configure and delivery are qualified separately.
        var map = FactoryMaps.Grass(32);
        var inserter = map.Prototypes["inserter"] with { FilterSlots = 5 };
        var prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
        {
            ["inserter"] = inserter,
            [LongArm] = inserter with { Name = LongArm, InserterPickup = new(0, -2), InserterDrop = new(0, 2.2) },
            [Equipment.Belt] = new(Equipment.Belt, "transport-belt", new(new(-.3984375, -.3984375), new(.3984375, .3984375)),
                inserter.Mask, 1, 1, BeltSpeed: .03125)
        };
        map = map with { Prototypes = prototypes, Items = new Dictionary<string, PlaceableItem>(map.Items)
            { [LongArm] = new(LongArm, 50), [Equipment.Belt] = new(Equipment.Belt, 100) } };
        var entities = new List<SpatialEntity>();
        for (int i = 0; i < 2; i++)
        {
            var source = new MapPosition(-8.5, i == 0 ? -8.5 : 8.5);
            var target = new MapPosition(8.5, source.Y);
            Add($"source-{i}", "iron-chest", source);
            Add($"target-{i}", "iron-chest", target);
            Add($"source-pole-{i}", Equipment.Pole, new(source.X, source.Y - 3));
            Add($"target-pole-{i}", Equipment.Pole, new(target.X, target.Y - 3));
            if (blockOrdinary)
                foreach (var offset in new MapPosition[] { new(0, -1), new(0, 1), new(-1, 0), new(1, 0) })
                    Add($"reservation-{i}-{offset.X}-{offset.Y}", "iron-chest", new(source.X + offset.X, source.Y + offset.Y));
        }
        return map with { Entities = entities };

        void Add(string id, string name, MapPosition position)
        {
            var geometry = map.Prototypes[name];
            entities.Add(new(id, name, position, geometry.CollisionBox.Translate(position), 0, "own",
                Power: geometry.Type == "electric-pole" ? new(1, 1) : null));
        }
    }
}
