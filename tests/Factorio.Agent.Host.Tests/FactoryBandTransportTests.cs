using Factorio.Agent.Core;
using System.Text.Json;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FactoryBandTransportTests
{
    private static readonly CellEquipment Equipment = new("assembling-machine-1", "inserter", "iron-chest", "small-electric-pole");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JointRoutesKeepTheThirdIngredientReachableBesideAnOlderBand(bool reverseRequests)
    {
        var map = NativeBandScenario();
        string[] sources = ["iron:output-chest", "gears:output-chest", "circuits:output-chest"];
        var equipment = new BeltTransportEquipment("transport-belt", "inserter", "small-electric-pole");
        var greedy = map;
        for (int i = 0; i < sources.Length; i++)
        {
            var plan = new BeltTransportPlanner().Find(greedy, equipment, sources[i], "consumer:input-chest");
            if (i == 2) { Assert.Null(plan); break; }
            Assert.NotNull(plan);
            greedy = ProjectBus(greedy, $"greedy{i}", plan);
        }

        var batch = new BeltTransportBatchPlanner().Find(map, equipment,
            reverseRequests ? sources.Reverse().ToArray() : sources, "consumer:input-chest");

        Assert.Equal(3, batch.Links.Count);
        Assert.False(batch.BudgetExhausted);
        Assert.Equal(sources.Order(), batch.Links.Select(l => l.SourceId).Order());
        Assert.Equal(3, batch.Links.Select(l => l.Plan.TargetInserter.Position).Distinct().Count());
        Assert.InRange(batch.Links.Sum(l => l.Plan.Belts.Count), 1, 100);
        var projected = map;
        foreach (var link in batch.Links)
        {
            var field = new SpatialCollisionField(projected);
            foreach (var part in link.Plan.Belts)
                Assert.True(field.PlacementClear(map.Prototypes["transport-belt"], part.Position, part.Direction));
            projected = ProjectBus(projected, link.SourceId, link.Plan);
        }
    }

    [Fact]
    public void JointRouteBudgetReportsItsPartialProofAndCancellationNeverReturnsAPlan()
    {
        var map = NativeBandScenario();
        var equipment = new BeltTransportEquipment("transport-belt", "inserter", "small-electric-pole");
        string[] sources = ["iron:output-chest", "gears:output-chest", "circuits:output-chest"];
        var partial = new BeltTransportBatchPlanner().Find(map, equipment, sources, "consumer:input-chest", maximumSearches: 1);
        Assert.Single(partial.Links);
        Assert.Equal(1, partial.Searches);
        Assert.True(partial.BudgetExhausted);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new BeltTransportBatchPlanner().Find(map, equipment,
            sources, "consumer:input-chest", token: cancelled.Token));
    }

    [Fact]
    public void SeveralConsumersReceiveDifferentSourcesWhenTheirCandidatesOverlap()
    {
        var map = SeveralConsumers();
        BeltTransportRequest[] requests = [new("first-target", ["first-source", "second-source"]),
            new("second-target", ["first-source", "second-source"])];

        var batch = new BeltTransportBatchPlanner().Find(map,
            new("transport-belt", "inserter", "small-electric-pole"), requests);

        Assert.Equal(2, batch.Links.Count);
        Assert.Equal(2, batch.Links.Select(l => l.SourceId).Distinct().Count());
        Assert.Equal(requests.Select(r => r.TargetId).Order(), batch.Links.Select(l => l.TargetId).Order());
        var projected = map;
        foreach (var link in batch.Links)
        {
            var field = new SpatialCollisionField(projected);
            foreach (var part in link.Plan.Belts)
                Assert.True(field.PlacementClear(map.Prototypes["transport-belt"], part.Position, part.Direction));
            projected = ProjectBus(projected, link.SourceId, link.Plan);
        }
    }

    [Fact]
    public void ASharedCandidateCannotBePromisedToTwoConsumers()
    {
        BeltTransportRequest[] requests = [new("first-target", ["first-source"]), new("second-target", ["first-source"])];
        var batch = new BeltTransportBatchPlanner().Find(SeveralConsumers(),
            new("transport-belt", "inserter", "small-electric-pole"), requests);

        Assert.Single(batch.Links);
        Assert.Equal("first-source", batch.Links[0].SourceId);
        Assert.False(batch.BudgetExhausted);
    }

    [Fact]
    public void FirstCompleteSearchCoversEveryConsumerBeforeStoppingComparisons()
    {
        var map = SeveralConsumers();
        BeltTransportRequest[] requests = [new("first-target", ["first-source", "second-source"]),
            new("second-target", ["first-source", "second-source"])];
        var equipment = new BeltTransportEquipment("transport-belt", "inserter", "small-electric-pole");
        var comparisons = new BeltTransportBatchPlanner().Find(map, equipment, requests);
        var feasible = new BeltTransportBatchPlanner().Find(map, equipment, requests, stopAfterComplete: true);
        Assert.Equal(2, feasible.Links.Count);
        Assert.Equal(2, feasible.Links.Select(l => l.SourceId).Distinct().Count());
        Assert.True(feasible.Searches < comparisons.Searches);
        Assert.False(feasible.BudgetExhausted);
        var projected = map;
        foreach (var link in feasible.Links)
        {
            var field = new SpatialCollisionField(projected);
            Assert.All(link.Plan.Belts, part => Assert.True(field.PlacementClear(map.Prototypes["transport-belt"], part.Position, part.Direction)));
            projected = ProjectBus(projected, link.SourceId, link.Plan);
        }
    }

    [Fact]
    public void FirstCompleteSearchStillRefusesToAssignOneSourceTwice()
    {
        BeltTransportRequest[] requests = [new("first-target", ["first-source"]), new("second-target", ["first-source"])];
        var plan = new BeltTransportBatchPlanner().Find(SeveralConsumers(), new("transport-belt", "inserter", "small-electric-pole"), requests, stopAfterComplete: true);
        Assert.Single(plan.Links);
    }

    [Fact]
    public void FirstCompleteSearchStillHonorsCallerCancellation()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new BeltTransportBatchPlanner().Find(SeveralConsumers(),
            new("transport-belt", "inserter", "small-electric-pole"),
            new BeltTransportRequest[] { new("first-target", ["first-source"]) }, token: cancelled.Token, stopAfterComplete: true));
    }

    [Fact]
    public void FuelBatchUsesTheSelectedTunnelAndPersistsItsPaidEnds()
    {
        var map = SeveralConsumers();
        var barrier = map.Prototypes["iron-chest"];
        map = map with
        {
            Bounds = new(new(-12, -12), new(13, 13)),
            Rows = Enumerable.Range(-12, 25).Select(y => new TileRun(-12, y, 25, map.Rows[0].Name)).ToArray(),
            Items = new Dictionary<string, PlaceableItem>(map.Items) { ["underground-belt"] = new("native-tunnel", 50) },
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            {
                ["native-tunnel"] = map.Prototypes["transport-belt"] with
                    { Name = "native-tunnel", Type = "underground-belt", MaxUndergroundDistance = 5 }
            },
            Entities = [.. map.Entities, .. Enumerable.Range(-12, 25).Select(y =>
                new SpatialEntity($"foreign-barrier-{y}", barrier.Name, new(.5, y + .5),
                    barrier.CollisionBox.Translate(new(.5, y + .5)), 0, "foreign"))]
        };
        BeltTransportRequest[] requests = [new("first-target", ["first-source"])];
        var ordinary = new BeltTransportEquipment("transport-belt", "inserter", "small-electric-pole");
        Assert.Empty(PowerFuelTransport.SearchBatch(map, ordinary, requests, CancellationToken.None, CancellationToken.None)!.Links);
        var crossing = PowerFuelTransport.SearchBatch(map, ordinary with { UndergroundBelt = "underground-belt" },
            requests, CancellationToken.None, CancellationToken.None);
        var link = Assert.Single(crossing!.Links);
        Assert.Equal("underground-belt", link.Plan.UndergroundBeltItem);
        Assert.Contains(link.Plan.Belts, p => p.UndergroundType == "input");
        Assert.Contains(link.Plan.Belts, p => p.UndergroundType == "output");
        var record = FactoryTransportBuilder.NewBus("coal-source", "boiler", "coal", 250, link.Plan, map.CollectedTick);
        Assert.All(record.Cell.Plan!.Values.Where(p => p.UndergroundType is not null),
            p => Assert.Equal("underground-belt", p.Item));
    }

    [Fact]
    public void FuelBatchStopsAtTheMaximumAssignmentWhenOneSourceServesTwoCandidates()
    {
        var plan = PowerFuelTransport.SearchBatch(SeveralConsumers(), new("transport-belt", "inserter", "small-electric-pole"),
            [new("first-target", ["first-source"]), new("second-target", ["first-source"])],
            CancellationToken.None, CancellationToken.None);
        Assert.NotNull(plan);
        Assert.Equal(1, plan.AssignmentUpperBound);
        Assert.Single(plan.Links);
        Assert.Equal(1, plan.Searches);
        Assert.False(plan.BudgetExhausted);
    }

    [Fact]
    public void MaximumAssignmentUsesAlternativeSourcesInsteadOfGreedyCounting()
    {
        var plan = PowerFuelTransport.SearchBatch(SeveralConsumers(), new("transport-belt", "inserter", "small-electric-pole"),
            [new("first-target", ["first-source", "second-source"]), new("second-target", ["first-source"])],
            CancellationToken.None, CancellationToken.None);
        Assert.NotNull(plan);
        Assert.Equal(2, plan.AssignmentUpperBound);
        Assert.Equal(2, plan.Links.Count);
        Assert.Equal("first-source", plan.Links.Single(l => l.TargetId == "second-target").SourceId);
        Assert.Equal("second-source", plan.Links.Single(l => l.TargetId == "first-target").SourceId);
        Assert.False(plan.BudgetExhausted);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void FuelBatchPlansOnlyItsConstructionQuantumAndLeavesTheNextRouteAvailable(int maximumLinks)
    {
        var map = SeveralConsumers();
        var equipment = new BeltTransportEquipment("transport-belt", "inserter", "small-electric-pole");
        BeltTransportRequest[] requests = [new("first-target", ["first-source"]), new("second-target", ["second-source"])];
        var plan = PowerFuelTransport.SearchBatch(map, equipment, requests,
            CancellationToken.None, CancellationToken.None, maximumLinks);
        Assert.NotNull(plan);
        Assert.Equal(2, plan.AssignmentUpperBound);
        Assert.Equal(maximumLinks, plan.Links.Count);
        Assert.Equal(maximumLinks, plan.Searches);
        Assert.False(plan.BudgetExhausted);
        if (maximumLinks == 1)
        {
            var selected = Assert.Single(plan.Links);
            var remaining = Assert.Single(requests, r => r.TargetId != selected.TargetId);
            Assert.NotNull(new BeltTransportPlanner().Find(ProjectBus(map, "reserved", selected.Plan), equipment,
                Assert.Single(remaining.SourceIds), remaining.TargetId));
        }
    }

    [Fact]
    public void AnAlreadyBlockedConsumerDoesNotPreventAnIndependentFuelLink()
    {
        var map = SeveralConsumers();
        var chest = map.Prototypes["iron-chest"];
        var positions = new MapPosition[] { new(5.5,6.5),new(7.5,6.5),new(6.5,5.5),new(6.5,7.5) };
        map = map with { Entities = [.. map.Entities, .. positions.Select((p,i) =>
            new SpatialEntity($"retained-obstacle-{i}",chest.Name,p,chest.CollisionBox.Translate(p),0,"foreign"))] };
        var plan = PowerFuelTransport.SearchBatch(map, new("transport-belt", "inserter", "small-electric-pole"),
            [new("first-target", ["first-source"]),new("second-target", ["second-source"])],
            CancellationToken.None, CancellationToken.None);
        Assert.NotNull(plan);
        Assert.Equal(1, plan.AssignmentUpperBound);
        Assert.Equal("first-target",Assert.Single(plan.Links).TargetId);
        Assert.False(plan.BudgetExhausted);
    }

    [Fact]
    public void FuelSearchDeadlineLeavesExistingLogisticsAvailable()
    {
        using var expired = new CancellationTokenSource(); expired.Cancel();
        var plan = PowerFuelTransport.SearchBatch(SeveralConsumers(), new("transport-belt", "inserter", "small-electric-pole"),
            [new("first-target", ["first-source"])], expired.Token, CancellationToken.None);
        Assert.Null(plan);
    }

    [Fact]
    public void FuelSearchDoesNotTurnCallerCancellationIntoLogisticsFallback()
    {
        using var caller = new CancellationTokenSource();
        using var planning = CancellationTokenSource.CreateLinkedTokenSource(caller.Token);
        caller.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PowerFuelTransport.SearchBatch(SeveralConsumers(), new("transport-belt", "inserter", "small-electric-pole"),
            [new("first-target", ["first-source"])], planning.Token, caller.Token));
    }

    [Fact]
    public void MultiConsumerSearchHonorsCancellationAndRejectsAnEmptyCandidateSet()
    {
        var map = SeveralConsumers();
        var equipment = new BeltTransportEquipment("transport-belt", "inserter", "small-electric-pole");
        Assert.Throws<ArgumentException>(() => new BeltTransportBatchPlanner().Find(map, equipment,
            new BeltTransportRequest[] { new("first-target", []) }));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new BeltTransportBatchPlanner().Find(map, equipment,
            new BeltTransportRequest[] { new("first-target", ["first-source"]) }, token: cancelled.Token));
    }

    [Theory]
    [InlineData("assembling-machine-1")]
    [InlineData("stone-furnace")]
    public void BothNativeChestFacesStayPoweredAlignedAndReachableAcrossAdjacentSlots(string machineItem)
    {
        var map = Map();
        var equipment = Equipment with { Machine = machineItem };
        var layouts = Enumerable.Range(0, 4).Select(i => new FactoryBandPlanner().Layout(map, equipment,
            new(-24, -10), new(0, i / 2, i % 2 == 0), transportAccess: true)).ToArray();
        var entities = layouts.SelectMany((l, i) => l.Entities.Select(e => Project(map, $"cell{i}:{e.Role}", e))).ToArray();
        var field = new SpatialCollisionField(map with { Entities = entities });
        var pole = map.Prototypes[equipment.Pole];
        foreach (var layout in layouts)
        {
            var station = layout.Role("pole")!;
            foreach (var part in layout.Entities)
            {
                var geometry = map.Prototypes[map.Items[part.Item].EntityName];
                Assert.Equal(Math.Floor(part.Position.X - geometry.TileWidth / 2.0), part.Position.X - geometry.TileWidth / 2.0);
                Assert.Equal(Math.Floor(part.Position.Y - geometry.TileHeight / 2.0), part.Position.Y - geometry.TileHeight / 2.0);
                var box = geometry.CollisionBox.Rotate(part.Direction).Translate(part.Position);
                Assert.True(layout.Footprint.Contains(box));
                Assert.DoesNotContain(entities.Where(e => e.Id != entities.Single(e => e.Position == part.Position && e.Name == geometry.Name).Id),
                    e => e.Bounds.Overlaps(box));
                if (part.Role is "machine" or "input-inserter" or "output-inserter")
                    Assert.True(PowerGridPlanner.Supplies(station.Position, pole, box));
                Assert.False(layout.Walkway.Overlaps(box));
            }
            var stand = new MapPosition((layout.Walkway.Min.X + layout.Walkway.Max.X) / 2,
                (layout.Walkway.Min.Y + layout.Walkway.Max.Y) / 2);
            var actor = new SpatialCollisionField(field.Map with { Actor = field.Map.Actor with { Position = stand } });
            Assert.True(actor.Walkable(stand));
            Assert.All(layout.Entities.Where(e => e.Role.EndsWith("chest", StringComparison.Ordinal)), e =>
                Assert.NotNull(new PlacementPlanner().FindInteractionApproach(actor, entities.Single(s => s.Position == e.Position && s.Name == map.Items[e.Item].EntityName))));
        }
        Assert.True(layouts[0].Role("pole")!.Position.DistanceTo(layouts[2].Role("pole")!.Position) <= pole.MaxWireDistance);
    }

    [Fact]
    public void AnOldZoneWithoutTheNewFieldStillReconstructsItsOriginalChestAndPolePositions()
    {
        var zone = JsonSerializer.Deserialize<FactoryZone>("""{"id":1,"origin":{"x":0,"y":0},"slots":8,"pitch":3,"bandHeight":12}""", Protocol.Json)!;
        Assert.False(zone.TransportAccess);
        var layout = new FactoryBandPlanner().Layout(Map(), Equipment, zone.Origin, new(0, 1, true), transportAccess: zone.TransportAccess);
        Assert.Equal(new MapPosition(4.5, 1.5), layout.Machine.Position);
        Assert.Equal(new MapPosition(3.5, 4.5), layout.Role("input-chest")!.Position);
        Assert.Equal(new MapPosition(5.5, 4.5), layout.Role("output-chest")!.Position);
        Assert.Equal(new MapPosition(4.5, 3.5), layout.Role("pole")!.Position);
    }

    [Fact]
    public void AlternatingProducerRowsCanFeedThreeIngredientsWithoutCrossingOtherBuses()
    {
        var map = Map();
        var layouts = Enumerable.Range(0, 4).Select(i => new FactoryBandPlanner().Layout(map, Equipment,
            new(-24, -10), new(0, i / 2, i % 2 == 0), transportAccess: true)).ToArray();
        var machine = map.Prototypes[Equipment.Machine];
        var zone = new FactoryZone(1, new(-24, -10), 4, FactoryBandPlanner.Pitch(machine, true), FactoryBandPlanner.BandHeight(machine, true), true);
        var cells = layouts.Select((l, i) => new FactoryCell($"cell{i}", 1, l.Slot, "assembler", Equipment.Machine, "inserter",
            l.Entities.ToDictionary(e => e.Role, e => $"cell{i}:{e.Role}"), "ready", 100,
            Plan: l.Entities.ToDictionary(e => e.Role))).ToArray();
        map = map with { Entities = layouts.SelectMany((l, i) => l.Entities.Select(e => Project(map, $"cell{i}:{e.Role}", e))).ToArray() };
        map = FactoryTransportBuilder.ProtectBands(map, new(1, "world", [zone], cells));
        foreach (int source in Enumerable.Range(0, 3))
        {
            var plan = new BeltTransportPlanner().Find(map, new("transport-belt", "inserter", "small-electric-pole"),
                $"cell{source}:output-chest", "cell3:input-chest");
            Assert.NotNull(plan);
            var parts = new List<SpatialEntity>
            {
                Project(map, $"bus{source}:source", new("source", "inserter", plan.SourceInserter.Position, plan.SourceInserter.Direction)),
                Project(map, $"bus{source}:target", new("target", "inserter", plan.TargetInserter.Position, plan.TargetInserter.Direction))
            };
            parts.AddRange(plan.Belts.Select((p, i) => Project(map, $"bus{source}:belt{i}", new("belt", "transport-belt", p.Position, p.Direction))));
            parts.AddRange(plan.Poles.Select((p, i) => Project(map, $"bus{source}:pole{i}", new("pole", "small-electric-pole", p.Position, p.Direction))));
            map = map with { Entities = [.. map.Entities, .. parts] };
        }
    }

    private static SpatialSnapshot Map()
    {
        var map = FactoryMaps.Grass(50);
        // Synthetic fixture using base-game 2.0.77 dimensions; native transport qualification remains separate.
        var belt = new EntityGeometry("transport-belt", "transport-belt", new(new(-.3984375, -.3984375), new(.3984375, .3984375)),
            map.Prototypes["inserter"].Mask, 1, 1, BeltSpeed: .03125);
        return map with
        {
            Items = new Dictionary<string, PlaceableItem>(map.Items) { [belt.Name] = new(belt.Name, 100) },
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes) { [belt.Name] = belt }
        };
    }

    private static SpatialSnapshot SeveralConsumers()
    {
        // Synthetic powered chest pairs. Real feeder construction is qualified separately in Factorio.
        var map = Map();
        var endpoints = new[] { ("first-source", new MapPosition(-6.5, -6.5)), ("second-source", new MapPosition(-6.5, 6.5)),
            ("first-target", new MapPosition(6.5, -6.5)), ("second-target", new MapPosition(6.5, 6.5)) };
        return map with { Entities = endpoints.SelectMany(e => new[] {
            Project(map, e.Item1, new("chest", "iron-chest", e.Item2, 0)),
            Project(map, e.Item1 + ":pole", new("pole", "small-electric-pole", new(e.Item2.X, e.Item2.Y - 3), 0))
        }).ToArray() };
    }

    private static SpatialSnapshot NativeBandScenario()
    {
        // Synthetic dry/powered replay of prepared fixture 20261145's recorded cell and link positions.
        var map = Map() with { Bounds = new(new(-59, -61), new(38, 36)),
            Rows = Enumerable.Range(-61, 97).Select(y => new TileRun(-59, y, 97, "grass")).ToArray() };
        map = map with { Actor = map.Actor with { Position = new(-11, -13) }, Coverage = map.Coverage with { Radius = 48 } };
        var assembler = map.Prototypes[Equipment.Machine];
        var furnace = map.Prototypes["stone-furnace"];
        FactoryZone[] zones = [new(1, new(11, 0), 2, 3, 12),
            new(2, new(-16, -24), 8, FactoryBandPlanner.Pitch(assembler, true), FactoryBandPlanner.BandHeight(assembler, true), true),
            new(3, new(-36, -45), 8, FactoryBandPlanner.Pitch(furnace, true), FactoryBandPlanner.BandHeight(furnace, true), true)];
        var descriptions = new[] {
            (Id: "legacy", Zone: zones[0], Slot: new CellSlot(0, 0, true), Equipment),
            (Id: "gears", Zone: zones[1], Slot: new CellSlot(0, 0, true), Equipment),
            (Id: "circuits", Zone: zones[1], Slot: new CellSlot(0, 0, false), Equipment),
            (Id: "consumer", Zone: zones[1], Slot: new CellSlot(0, 1, true), Equipment),
            (Id: "iron", Zone: zones[2], Slot: new CellSlot(0, 0, true), Equipment: Equipment with { Machine = "stone-furnace" }) };
        var cells = descriptions.Select(d =>
        {
            var layout = new FactoryBandPlanner().Layout(map, d.Equipment, d.Zone.Origin, d.Slot, transportAccess: d.Zone.TransportAccess);
            return new FactoryCell(d.Id, d.Zone.Id, d.Slot, "production", d.Equipment.Machine, null,
                layout.Entities.ToDictionary(e => e.Role, e => $"{d.Id}:{e.Role}"), "ready", 100,
                Plan: layout.Entities.ToDictionary(e => e.Role));
        }).ToArray();
        MapPosition[] links = [new(-2.5, .5), new(4.5, .5), new(11.5, .5), new(-4.5, .5),
            new(-10.5, -3.5), new(-16.5, -7.5), new(-18.5, -14.5),
            new(-20.5, -24.5), new(-27.5, -26.5), new(-34.5, -26.5), new(-36.5, -33.5), new(-40.5, -39.5)];
        var projected = map with { Entities = [.. cells.SelectMany(c => c.Plan!.Values.Select(p => Project(map, c.Entities[p.Role], p))),
            .. links.Select((p, i) => Project(map, $"link{i}", new("pole", Equipment.Pole, p, 0)))] };
        return FactoryTransportBuilder.ProtectBands(projected, new(1, "world", zones, cells));
    }

    private static SpatialSnapshot ProjectBus(SpatialSnapshot map, string id, BeltTransportPlan plan) => map with
    {
        Entities = [.. map.Entities,
            Project(map, $"{id}:source", new("source", Equipment.Inserter, plan.SourceInserter.Position, plan.SourceInserter.Direction)),
            Project(map, $"{id}:target", new("target", Equipment.Inserter, plan.TargetInserter.Position, plan.TargetInserter.Direction)),
            .. plan.Belts.Select((p, i) => Project(map, $"{id}:belt{i}", new("belt", "transport-belt", p.Position, p.Direction))),
            .. plan.Poles.Select((p, i) => Project(map, $"{id}:pole{i}", new("pole", Equipment.Pole, p.Position, p.Direction)))]
    };

    private static SpatialEntity Project(SpatialSnapshot map, string id, PlannedEntity e)
    {
        var geometry = map.Prototypes[map.Items[e.Item].EntityName];
        return new(id, geometry.Name, e.Position, geometry.CollisionBox.Rotate(e.Direction).Translate(e.Position), e.Direction, "own",
            DropPosition: geometry.InserterDrop is null ? null : At(e, geometry.InserterDrop),
            PickupPosition: geometry.InserterPickup is null ? null : At(e, geometry.InserterPickup),
            Power: geometry.Type == "electric-pole" ? new(1, 1) : null);
    }

    private static MapPosition At(PlannedEntity e, MapPosition offset)
    {
        var rotated = ExtractionPlanner.Rotate(offset, e.Direction);
        return new(e.Position.X + rotated.X, e.Position.Y + rotated.Y);
    }
}
