using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FluidExtractorAdoptionTests
{
    [Fact]
    public void AFreePoweredExtractorRetainsItsIdentityAndUnclaimedNativeGridPlans()
    {
        var (state, stock, map, catalog) = Scenario();
        var cell = FluidExtractorAdoption.Plan(state, stock, map, catalog, "crude-oil", "jack")!;
        Assert.Equal("jack", cell.Entities["drill"]);
        Assert.Equal("crude-oil", cell.Recipe);
        Assert.Equal(FluidCellBuilder.ExtractorKind, cell.Kind);
        Assert.Equal("ready", cell.Status);
        Assert.Equal("unclaimed", cell.Entities["link-0"]);
        Assert.DoesNotContain("claimed", cell.Entities.Values);
        Assert.DoesNotContain("island", cell.Entities.Values);
        Assert.Equal(new MapPosition(8.5, .5), cell.Plan!["drill"].Position);
        Assert.Equal(4, cell.Plan["drill"].Direction);
        Assert.Equal("small-electric-pole", cell.Plan["link-0"].Item);
        Assert.Equal(new MapPosition(6.5, .5), cell.Plan["link-0"].Position);
        Assert.Equal(300, FluidCellBuilder.Rate(map, catalog, cell));
        Assert.Empty(FluidExtractorAdoption.Candidates(state.With(cell), stock, "crude-oil"));
    }

    [Theory]
    [InlineData("claimed")]
    [InlineData("connected")]
    [InlineData("unknown-ports")]
    [InlineData("empty-fluid")]
    [InlineData("unpowered")]
    [InlineData("island")]
    [InlineData("unseen")]
    [InlineData("no-deposit")]
    [InlineData("wrong-fluid")]
    [InlineData("manual")]
    [InlineData("moved")]
    [InlineData("foreign")]
    public void MissingProofOrAnExistingOwnerPreventsAdoption(string missing)
    {
        var (state, stock, map, catalog) = Scenario();
        var drill = map.Entities.Single(e => e.Id == "jack");
        if (missing == "claimed") state = state.With(new FactoryCell("old", 0, new(0, 0, true), "extractor", "pumpjack", "crude-oil",
            new Dictionary<string, string> { ["drill"] = "jack" }, "ready", 1));
        if (missing == "connected") drill = drill with { FluidConnections = [drill.FluidConnections![0] with { TargetEntityId = "pipe", TargetBoxIndex = 1 }] };
        if (missing == "unknown-ports") drill = drill with { FluidConnections = null };
        if (missing == "unpowered") drill = drill with { Power = new(0, 1) };
        if (missing == "moved") drill = drill with { Position = new(9.5, .5) };
        map = map with { Entities = map.Entities.Select(e => e.Id == drill.Id ? drill : e).ToArray() };
        if (missing == "manual") map = map with { Actor = map.Actor with { ControlMode = "manual" } };
        if (missing == "unseen") map = map with { Entities = map.Entities.Where(e => e.Id != "jack").ToArray() };
        if (missing == "no-deposit") map = map with { Entities = map.Entities.Where(e => e.Name != "crude-oil").ToArray() };
        if (missing == "empty-fluid") stock = stock with { Records = stock.Records.Where(r => r.Kind != "fluid").ToArray() };
        if (missing == "island") stock = stock with { Records = stock.Records.Where(r => r.EntityId != "source").ToArray() };
        if (missing == "foreign") stock = stock with { Records = stock.Records.Select(r => r.EntityId == "jack" && r.Kind == "entity"
            ? r with { Data = Protocol.ToElement(new { role = "foreign", type = "mining-drill", position = drill.Position, direction = 4, electricNetworkId = 1 }) } : r).ToArray() };
        Assert.Null(FluidExtractorAdoption.Plan(state, stock, map, catalog, missing == "wrong-fluid" ? "water" : "crude-oil", "jack"));
    }

    [Fact]
    public void AnActorScopeChangeCannotBeRegistered()
    {
        var (state, stock, map, catalog) = Scenario();
        map = map with { Scope = map.Scope with { Generation = map.Scope.Generation + 1 } };
        Assert.Throws<InvalidDataException>(() => FluidExtractorAdoption.Plan(state, stock, map, catalog, "crude-oil", "jack"));
    }

    private static (FactoryState State, FactorySnapshot Stock, SpatialSnapshot Map, ProductionCatalog Catalog) Scenario()
    {
        var catalog = OilCatalogs.Oil();
        var at = new MapPosition(8.5, .5);
        var drill = new SpatialEntity("jack", "pumpjack", at, new WorldBox(new(-1.2, -1.2), new(1.2, 1.2)).Translate(at), 4, "agent",
            FluidConnections: [new(1, 1, new(9.5, -.5), new(9.5, -1.5), FlowDirection: "output", Filter: "crude-oil")], Power: new(1000, 1));
        var map = OilMaps.Map([OilMaps.Crude("deposit", at, 150000), drill]) with { Scope = catalog.Scope };
        FactoryRecord Entity(string id, string name, string type, MapPosition position, int direction, long network) =>
            new($"entity:{id}", "entity", id, name, Protocol.ToElement(new { role = "factory", type, position, direction, electricNetworkId = network }));
        var stock = new FactorySnapshot("snapshot", catalog.Scope, 100, 200, Protocol.ToElement(new { atomic = true }),
            [Entity("jack", "pumpjack", "mining-drill", at, 4, 1),
             Entity("unclaimed", "small-electric-pole", "electric-pole", new(6.5, .5), 0, 1),
             Entity("claimed", "small-electric-pole", "electric-pole", new(4.5, .5), 0, 1),
             Entity("island", "small-electric-pole", "electric-pole", new(6.5, 3.5), 0, 2),
             Entity("source", "steam-engine", "generator", new(0, 0), 0, 1),
             new("fluid:jack", "fluid", "jack", "crude-oil", Protocol.ToElement(new { aggregateSafe = true, contents = new Dictionary<string, double> { ["crude-oil"] = 100 } }))]);
        var state = new FactoryState(1, catalog.Scope.WorldId, [],
            [new("power", 0, new(0, 0, true), "power", "steam-engine", null, new Dictionary<string, string> { ["pole"] = "claimed" }, "ready", 1)]);
        return (state, stock, map, catalog);
    }
}
