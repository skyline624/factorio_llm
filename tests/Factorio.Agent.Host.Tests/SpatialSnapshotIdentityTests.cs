using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class SpatialSnapshotIdentityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeCircuitNeighboursParseIncludingEmptyLuaTables(bool connected)
    {
        var map = SpatialPlannerTests.Map([]);
        var row = System.Text.Json.Nodes.JsonNode.Parse(Protocol.ToElement(new SpatialEntity(
            "drill", "wall", new(2, 0), new(new(1.5, -.5), new(2.5, .5)), 0, "own")).GetRawText())!;
        row["greenNeighbours"] = System.Text.Json.Nodes.JsonNode.Parse(connected ? "[\"pole\"]" : "{}");
        row["greenNeighbourCount"] = connected ? 1 : 0;
        row["redNeighbours"] = System.Text.Json.Nodes.JsonNode.Parse("{}");
        row["redNeighbourCount"] = 0;
        var data = System.Text.Json.Nodes.JsonNode.Parse(Protocol.ToElement(map).GetRawText())!;
        data["entities"] = new System.Text.Json.Nodes.JsonArray(row);
        var response = new GameResponse(1, "synthetic", true, map.CollectedTick,
            System.Text.Json.JsonSerializer.SerializeToElement(data, Protocol.Json));
        var observed = Assert.Single(SpatialSnapshot.Parse(response).Entities);
        Assert.Equal(connected ? 1 : 0, observed.GreenNeighbourCount);
        Assert.Equal(connected ? new[] { "pole" } : Array.Empty<string>(), observed.GreenNeighbours);
        Assert.Empty(observed.RedNeighbours!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverlappingBodiesRequireDistinctNativeIdentities(bool duplicate)
    {
        var map = SpatialPlannerTests.Map([]);
        var body = new EntityGeometry("small-biter-corpse", "corpse", new(new(-.2, -.2), new(.2, .2)),
            new([], false, false, false), 1, 1);
        map = map with
        {
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes) { [body.Name] = body },
            Entities = [new("body:10", body.Name, new(2, 0), body.CollisionBox.Translate(new(2, 0)), 0, "neutral"),
                new(duplicate ? "body:10" : "body:11", body.Name, new(2, 0), body.CollisionBox.Translate(new(2, 0)), 0, "neutral")]
        };
        var response = new GameResponse(1, "synthetic", true, map.CollectedTick, Protocol.ToElement(map));
        if (duplicate) Assert.Throws<InvalidDataException>(() => SpatialSnapshot.Parse(response));
        else Assert.Equal(2, SpatialSnapshot.Parse(response).Entities.Count);
    }
}
