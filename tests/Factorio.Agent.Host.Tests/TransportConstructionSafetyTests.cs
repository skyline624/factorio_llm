using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class TransportConstructionSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SurfaceRouteDetoursAroundTheNavigationEnvelopeWithoutChangingNativeReach(bool partial)
    {
        var map = Map();
        var target = new MapPosition(partial ? map.Bounds.Max.X + 100.5 : 32.5, .5);
        var direct = new BeltRoutePlanner().Find(map with { StationaryThreats = [] }, "belt", new(-32.5, .5), target, partial: partial);
        var safe = new BeltRoutePlanner().Find(map, "belt", new(-32.5, .5), target, partial: partial);
        Assert.Equal(partial ? BeltRouteStatus.Partial : BeltRouteStatus.Found, safe.Status);
        Assert.True(safe.Belts.Count > direct.Belts.Count);
        Assert.All(safe.Belts, b => Assert.True(b.Position.DistanceTo(new(0, 0)) >= 29));
        Assert.Equal(10, map.Actor.BuildDistance);
    }

    [Fact]
    public void UndergroundEndpointsCannotBypassTheConstructionEnvelope()
    {
        var map = Map();
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            { ["underground"] = map.Prototypes["belt"] with { Name = "underground", Type = "underground-belt", MaxUndergroundDistance = 8 } },
            Items = new Dictionary<string, PlaceableItem>(map.Items) { ["underground"] = new("underground", 50) } };
        var route = new UndergroundBeltRoutePlanner().Find(map, "belt", "underground", new(-32.5, .5), new(32.5, .5));
        Assert.Equal(BeltRouteStatus.Found, route.Status);
        Assert.All(route.Belts, b => Assert.True(b.Position.DistanceTo(new(0, 0)) >= 29));
        var impossible = new UndergroundBeltRoutePlanner().Find(map, "belt", "underground", new(-32.5, .5), new(.5, .5));
        Assert.NotEqual(BeltRouteStatus.Found, impossible.Status);
    }

    [Fact]
    public void PartialRerouteConservesEveryPaidIdentityAndKeepsNativeCircuitEndpoints()
    {
        var map = Map();
        var equipment = new BeltTransportEquipment("belt", "arm", "pole");
        var oldPlan = new BeltTransportPlanner().Find(map with { StationaryThreats = [] }, equipment, "source", "target");
        Assert.NotNull(oldPlan);
        var oldRecord = FactoryTransportBuilder.NewBus("source-cell", "target-cell", "coal", 200, oldPlan, map.CollectedTick, equipment);
        var ids = new Dictionary<string, string>();
        var entities = new List<SpatialEntity>(map.Entities);
        // A paid prefix plus both stopped, wired arms, as in a persisted interrupted build.
        foreach (var part in oldRecord.Cell.Plan!.Values.Where(p => p.Role.Contains("inserter") || p.Role is "belt-0" or "belt-1" or "belt-2" or "belt-3"))
        {
            var geometry = map.Prototypes[map.Items[part.Item].EntityName];
            string id = $"paid:{part.Role}";
            ids[part.Role] = id;
            entities.Add(new(id, geometry.Name, part.Position, geometry.CollisionBox.Rotate(part.Direction).Translate(part.Position), part.Direction, "own"));
        }
        // This paid obsolete piece exercises recovery, rather than merely preserving a common prefix.
        var obsolete = oldRecord.Cell.Plan.Values.First(p => p.Role.StartsWith("belt-") && p.Position.DistanceTo(new(0, 0)) is > 24 and < 27);
        ids[obsolete.Role] = "paid:obsolete";
        entities.Add(new("paid:obsolete", "belt", obsolete.Position, map.Prototypes["belt"].CollisionBox.Translate(obsolete.Position), obsolete.Direction, "own"));
        var old = oldRecord.Cell with { Entities = ids, Attempts = 3 };
        map = map with { Entities = entities };
        var output = old.Plan!["source-inserter"];
        var input = old.Plan["target-inserter-0"];
        var search = map with { Entities = map.Entities.Where(e => !ids.Values.Contains(e.Id)).ToArray() };
        var replacement = new BeltTransportPlanner().Find(search, equipment, "source", "target",
            sourceInserter: new(output.Position, output.Direction, 0), targetInserter: new(input.Position, input.Direction, 0));
        Assert.NotNull(replacement);
        var record = FactoryTransportReplanning.CreateRecord(oldRecord.Bus with { ConstructionRoutingVersion = 0 }, old, replacement, map, equipment);
        var retired = record.Bus.PendingRetirements!.Select(r => r.EntityId).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(old.Id, record.Cell.Id);
        Assert.Equal(oldRecord.Bus.Id, record.Bus.Id);
        Assert.Equal(3, record.Cell.Attempts);
        Assert.Equal(ids["source-inserter"], record.Cell.Entities["source-inserter"]);
        Assert.Equal(ids["target-inserter-0"], record.Cell.Entities["target-inserter-0"]);
        Assert.Contains("paid:obsolete", retired);
        Assert.Contains("paid:belt-0", record.Cell.Entities.Values);
        Assert.True(ids.Values.ToHashSet(StringComparer.Ordinal).SetEquals(record.Cell.Entities.Values.Concat(retired)));
        Assert.DoesNotContain(record.Cell.Entities.Values, retired.Contains);
        Assert.True(record.Cell.Entities.Count >= 2);
        Assert.All(record.Cell.Plan!.Values, p => Assert.True(TransportConstructionSafety.Allows(map, p.Position)));
        var roundtrip = JsonSerializer.Deserialize<FactoryTransportBus>(JsonSerializer.Serialize(record.Bus, Protocol.Json), Protocol.Json)!;
        Assert.Equal(1, roundtrip.ConstructionRoutingVersion);
        Assert.Equal(0, roundtrip.ActorReserve);
        Assert.Contains(roundtrip.PendingRetirements!, r => r.EntityId == "paid:obsolete" && r.Part.Position == obsolete.Position);
    }

    internal static SpatialSnapshot Map()
    {
        var map = BeltTransportPlannerTests.Map(true);
        var actor = map.Actor with { Position = new(-35.5, 8.5) };
        var entities = map.Entities.Select(e =>
        {
            var position = e.Id switch { "source" => new MapPosition(-35.5, .5), "target" => new(35.5, .5),
                "pole1" => new(-35.5, 3.5), _ => new(35.5, 3.5) };
            return e with { Position = position, Bounds = map.Prototypes[e.Name].CollisionBox.Translate(position) };
        }).Append(new(actor.Id, actor.Name, actor.Position, map.Prototypes[actor.Name].CollisionBox.Translate(actor.Position), 0, "own")).ToArray();
        return map with { Actor = actor, Entities = entities, Bounds = new(new(-50, -50), new(51, 51)),
            Rows = Enumerable.Range(-50, 101).Select(y => new TileRun(-50, y, 101, "grass")).ToArray(),
            StationaryThreats = [new("worm", new(0, 0), 5, map.CollectedTick)] };
    }
}
