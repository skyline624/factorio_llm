using Factorio.Agent.Core;
using Xunit;
namespace Factorio.Agent.Host.Tests;

public sealed class ResourceExtractionPlannerTests
{
    [Fact]
    public void DistantDepositCanBeSelectedBeforeItsGridIsExtended()
    {
        var map = Map();
        map = map with { Entities = map.Entities.Where(e => e.Id != "power").ToArray() };
        var site = new ResourceExtractionPlanner().FindSite(map, "oil", "pumpjack", new HashSet<string>());
        Assert.NotNull(site);
        Assert.Equal("oil-1", site.ResourceId);
        Assert.Equal(new MapPosition(5.5, 5.5), site.Machine.Position);
        Assert.Null(site.ExistingMachineId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InterruptedInstallationReusesOnlyAnOwnedCompatibleExtractor(bool owned)
    {
        var map = Map();
        var position = new MapPosition(5.5, 5.5);
        map = map with { Entities = [..map.Entities, new("installed", "pumpjack", position,
            map.Prototypes["pumpjack"].CollisionBox.Translate(position), 4, owned ? "agent" : "other")] };
        var site = new ResourceExtractionPlanner().FindSite(map, "oil", "pumpjack",
            owned ? new HashSet<string> { "installed" } : new HashSet<string>());
        if (owned)
        {
            Assert.NotNull(site);
            Assert.Equal("installed", site.ExistingMachineId);
            Assert.Equal(4, site.Machine.Direction);
        }
        else Assert.Null(site);
    }

    [Theory]
    [InlineData("wrong-category")]
    [InlineData("no-output")]
    [InlineData("not-electric")]
    [InlineData("exhausted")]
    public void UnpoweredSiteStillRequiresUsableFluidExtraction(string fault)
    {
        var map = Map();
        var prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes);
        if (fault == "wrong-category") prototypes["pumpjack"] = prototypes["pumpjack"] with { ResourceCategories = new Dictionary<string, bool> { ["solid"] = true } };
        if (fault == "no-output") prototypes["pumpjack"] = prototypes["pumpjack"] with { FluidBoxes = [] };
        if (fault == "not-electric") prototypes["pumpjack"] = prototypes["pumpjack"] with { IsElectric = false };
        map = map with { Prototypes = prototypes, Entities = map.Entities.Select(e =>
            fault == "exhausted" && e.Id == "oil-1" ? e with { Amount = 0 } : e).ToArray() };
        Assert.Null(new ResourceExtractionPlanner().FindSite(map, "oil", "pumpjack", new HashSet<string>()));
    }

    [Fact]
    public void PlacesFluidExtractorExactlyOnCompatibleObservedResource()
    {
        var map = Map();
        var plan = new ResourceExtractionPlanner().Find(map, "oil", "pumpjack", "pole", new HashSet<string> { "power" });
        Assert.NotNull(plan);
        Assert.Equal("oil-1", plan.ResourceId);
        Assert.Equal(new MapPosition(5.5, 5.5), plan.Machine.Position);
        Assert.Null(plan.AdditionalPole);
    }
    [Fact]
    public void ExtendsPowerWithoutPlacingPoleInsideThePumpjack()
    {
        var map = Map(farPole: true);
        var plan = new ResourceExtractionPlanner().Find(map, "oil", "pumpjack", "pole", new HashSet<string> { "power" });
        Assert.NotNull(plan?.AdditionalPole);
        var pole = plan.AdditionalPole;
        Assert.InRange(pole.Position.DistanceTo(map.Entities.Single(e => e.Id == "power").Position), 0, 7.5);
        Assert.False(map.Prototypes["pumpjack"].CollisionBox.Translate(plan.Machine.Position)
            .Overlaps(map.Prototypes["pole"].CollisionBox.Translate(pole.Position)));
    }
    [Theory]
    [InlineData("wrong-category")]
    [InlineData("no-output")]
    [InlineData("not-electric")]
    [InlineData("exhausted")]
    [InlineData("no-network")]
    public void RefusesUnsupportedOrUnusableExtraction(string fault)
    {
        var map = Map();
        var p = new Dictionary<string, EntityGeometry>(map.Prototypes);
        if (fault == "wrong-category") p["pumpjack"] = p["pumpjack"] with { ResourceCategories = new Dictionary<string, bool> { { "solid", true } } };
        if (fault == "no-output") p["pumpjack"] = p["pumpjack"] with { FluidBoxes = [] };
        if (fault == "not-electric") p["pumpjack"] = p["pumpjack"] with { IsElectric = false };
        map = map with
        {
            Prototypes = p,
            Entities = map.Entities.Select(e => fault == "exhausted" && e.Id == "oil-1" ? e with { Amount = 0 } :
            fault == "no-network" && e.Id == "power" ? e with { Power = null } : e).ToArray()
        };
        Assert.Null(new ResourceExtractionPlanner().Find(map, "oil", "pumpjack", "pole", new HashSet<string> { "power" }));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OwnActorCanLeaveFixedResourceSiteButAnotherCharacterRemainsAnObstacle(bool ownActor)
    {
        var map = Map();
        var position = new MapPosition(5.5, 5.5);
        var character = new SpatialEntity(ownActor ? map.Actor.Id : "other-character", "character", position,
            map.Prototypes["character"].CollisionBox.Translate(position), 0, "agent");
        map = map with { Actor = map.Actor with { Position = position }, Entities = [.. map.Entities, character] };
        var plan = new ResourceExtractionPlanner().Find(map, "oil", "pumpjack", "pole", new HashSet<string> { "power" });
        if (ownActor)
        {
            Assert.NotNull(plan);
            var approach = new PlacementPlanner().FindApproach(new(map), "pumpjack", plan.Machine);
            Assert.NotNull(approach);
            Assert.False(map.Prototypes["pumpjack"].CollisionBox.Translate(position).Contains(approach));
        }
        else Assert.Null(plan);
    }

    private static SpatialSnapshot Map(bool farPole = false)
    {
        var m = SpatialPlannerTests.Map([]);
        var solid = m.Prototypes["chest"].Mask;
        var p = new Dictionary<string, EntityGeometry>(m.Prototypes)
        {
            ["oil"] = new("oil", "resource", new(new(-.4, -.4), new(.4, .4)), new(["resource"], false, false, false), 1, 1, ResourceCategory: "fluid"),
            ["pumpjack"] = new("pumpjack", "mining-drill", new(new(-1.4, -1.4), new(1.4, 1.4)), solid, 3, 3,
                MiningRadius: 0.49, ResourceCategories: new Dictionary<string, bool> { { "fluid", true } }, FluidBoxes: [new(1, "output", [])], IsElectric: true),
            ["pole"] = new("pole", "electric-pole", new(new(-.2, -.2), new(.2, .2)), solid, 1, 1, SupplyArea: 2.5, MaxWireDistance: 7.5)
        };
        var rp = new MapPosition(5.5, 5.5); var pp = farPole ? new MapPosition(-.5, 3.5) : new MapPosition(2.5, 3.5);
        return m with
        {
            Prototypes = p,
            Items = new Dictionary<string, PlaceableItem> { { "pumpjack", new("pumpjack", 20) }, { "pole", new("pole", 50) } },
            Entities = [new("oil-1","oil",rp,p["oil"].CollisionBox.Translate(rp),0,"neutral",100000),
                new("power","pole",pp,p["pole"].CollisionBox.Translate(pp),0,"agent",Power:new(0,1))]
        };
    }
}
