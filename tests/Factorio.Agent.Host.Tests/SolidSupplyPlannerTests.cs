using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class SolidSupplyPlannerTests
{
    [Fact]
    public void ReusesAnExistingInputWhenItsSourceBufferIsEmptyButTheLineCarriesMaterial()
    {
        var map = BeltTransportBoundaryTests.Map();
        var stock = BeltTransportBoundaryTests.Snapshot(0, 10, 0, 1, 2, 0);
        var source = Assert.Single(new SolidSupplyPlanner().Candidates(map, stock, BeltTransportBoundaryTests.Catalog(map), "target", "gear"));
        Assert.Equal("source", source.SourceId);
        Assert.True(source.Existing);
        Assert.Equal(3, source.Available);
    }

    [Fact]
    public void AnUnclaimedStockCanSupplyANewConnection()
    {
        var map = BeltTransportPlannerTests.Map(true);
        var stock = BeltTransportBoundaryTests.Snapshot(0, 10, 8, 0, 0, 0);
        var source = Assert.Single(new SolidSupplyPlanner().Candidates(map, stock, BeltTransportBoundaryTests.Catalog(map), "target", "gear"));
        Assert.Equal("source", source.SourceId);
        Assert.False(source.Existing);
        Assert.Equal(8, source.Available);
    }

    [Fact]
    public void ASourceAlreadyFeedingAnotherMachineCannotBeClaimedAgain()
    {
        var map = BeltTransportBoundaryTests.Map();
        var stock = BeltTransportBoundaryTests.Snapshot(2, 10, 0, 0, 0, 0);
        Assert.DoesNotContain(new SolidSupplyPlanner().Candidates(map, stock, BeltTransportBoundaryTests.Catalog(map), "target", "gear"), s => s.SourceId == "root");
    }

    [Fact]
    public void RecognizesMaterialUpstreamOfAnEmptyBufferBeforeItReachesTheFinalLine()
    {
        var map = BeltTransportBoundaryTests.Map();
        var stock = BeltTransportBoundaryTests.Snapshot(0, 10, 0, 1, 0, 0);
        var source = Assert.Single(new SolidSupplyPlanner().Candidates(map, stock, BeltTransportBoundaryTests.Catalog(map), "target", "gear"));
        Assert.Equal("source", source.SourceId);
        Assert.Equal(1, source.Available);
    }

    [Fact]
    public void AmbiguousTransitDoesNotBecomeAManualFallback()
    {
        var map = BeltTransportBoundaryTests.Map();
        var stock = BeltTransportBoundaryTests.Snapshot(0, 10, 1, 0, 1, 0);
        stock = stock with { Records = stock.Records.Where(r => r.Id != "out:2").ToArray() };
        Assert.Throws<InvalidDataException>(() => new SolidSupplyPlanner().Candidates(map, stock, BeltTransportBoundaryTests.Catalog(map), "target", "gear"));
    }

    [Fact]
    public void MissingMachineEvidenceCannotBeSilentlyTreatedAsAnUnsupportedSource()
    {
        var map = BeltTransportBoundaryTests.Map();
        var stock = BeltTransportBoundaryTests.Snapshot(2, 10, 1, 0, 0, 0);
        stock = stock with { Records = stock.Records.Where(r => r.Id != "root:work").ToArray() };
        Assert.Throws<InvalidDataException>(() => new SolidSupplyPlanner().Candidates(map, stock, BeltTransportBoundaryTests.Catalog(map), "target", "gear"));
    }

    [Fact]
    public void AnUpstreamReservationProtectsItsDownstreamBufferFromNestedProduction()
    {
        var map = BeltTransportBoundaryTests.Map();
        var stock = BeltTransportBoundaryTests.Snapshot(2, 10, 8, 0, 0, 0);
        Assert.Empty(new SolidSupplyPlanner().Candidates(map, stock, BeltTransportBoundaryTests.Catalog(map), "target", "gear", new HashSet<string> { "root" }));
    }

    [Fact]
    public void TheTargetsEmptyOutputChainIsNotAnIngredientSource()
    {
        var map = BeltTransportBoundaryTests.Map();
        var stock = BeltTransportBoundaryTests.Snapshot(0, 10, 0, 0, 0, 0);
        stock = stock with { Records = [.. stock.Records.Where(r => r.Id != "root:work"),
            new("root:work", "work", "root", "root", Protocol.ToElement(new
                { recipe = "gear", inputInventoryId = "root:input", outputInventoryId = "root:inventory", productsFinished = 10, inProcess = false })),
            ] };
        Assert.Empty(new SolidSupplyPlanner().Candidates(map, stock, BeltTransportBoundaryTests.Catalog(map), "root", "iron"));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void WaitsForConnectedRawIngredientsWithoutClaimingFinishedOutput(bool power, bool expected)
    {
        var map = BeltTransportBoundaryTests.Map();
        map = map with
        {
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
                { ["machine"] = map.Prototypes["machine"] with { IsElectric = true } },
            Entities = [.. map.Entities.Select(e => e.Id == "root" ? e with { Power = new(power ? 10 : 0, 1) } : e),
                new("raw", "chest", new(-9.5, .5), new(new(-9.85, .15), new(-9.15, .85)), 0, "own"),
                new("raw-belt", "belt", new(-7.5, .5), new(new(-7.9, .1), new(-7.1, .9)), 4, "own", BeltConnections: new([], [])),
                new("raw-feed", "arm", new(-8.5, .5), new(new(-8.65, .35), new(-8.35, .65)), 12, "own",
                    DropTargetId: "raw-belt", PickupTargetId: "raw", Power: new(10, 1)),
                new("raw-receive", "arm", new(-6.5, .5), new(new(-6.65, .35), new(-6.35, .65)), 12, "own",
                    DropTargetId: "root", PickupTargetId: "raw-belt", Power: new(10, 1))]
        };
        var stock = BeltTransportBoundaryTests.Snapshot(0, 10, 0, 0, 0, 0);
        stock = stock with { Records = [.. stock.Records,
            new("raw", "entity", "raw", "raw", Protocol.ToElement(new { type = "container" })),
            new("raw:inventory", "inventory", "raw", "raw", Protocol.ToElement(new { items = new { iron = 4 } })),
            .. new[] { ("raw-belt:1", "raw-belt"), ("raw-belt:2", "raw-belt"), ("raw-feed:hand", "raw-feed"), ("raw-receive:hand", "raw-receive") }
                .Select(p => new FactoryRecord(p.Item1, "transit", p.Item2, p.Item2, Protocol.ToElement(new { items = new { iron = 0 } })))] };
        var candidates = new SolidSupplyPlanner().Candidates(map, stock, BeltTransportBoundaryTests.Catalog(map), "target", "gear");
        Assert.Equal(expected, candidates.Count > 0);
        if (!expected) return;
        var source = Assert.Single(candidates);
        Assert.Equal("source", source.SourceId);
        Assert.Equal(0, source.Available);
        Assert.False(source.Producing);
        Assert.True(source.InputsAvailable);
    }
}
