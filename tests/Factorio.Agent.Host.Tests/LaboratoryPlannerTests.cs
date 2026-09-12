using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class LaboratoryPlannerTests
{
    [Fact]
    public void ExtensionRespectsBothWireRangesAndKeepsTheLabClearOfTheNewPole()
    {
        var map = SteamPowerPlannerTests.Map(true);
        var prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
        {
            ["pole"] = map.Prototypes["pole"] with { MaxWireDistance = 7.5 },
            ["lab"] = new("lab", "lab", new(new(-1.4, -1.4), new(1.4, 1.4)), map.Prototypes["boiler"].Mask, 3, 3)
        };
        var source = new SpatialEntity("source", "pole", new(.5, .5), new(new(.35, .35), new(.65, .65)), 0, "own", Power: new(0, 1));
        map = map with { Prototypes = prototypes, Entities = [source], Items = new Dictionary<string, PlaceableItem>(map.Items) { ["lab"] = new("lab", 10) } };
        var extension = new PoweredMachinePlanner().Extend(map, "lab", "pole", source);
        Assert.NotNull(extension);
        Assert.InRange(source.Position.DistanceTo(extension.Pole.Position), .1, 7.5);
        Assert.False(prototypes["pole"].CollisionBox.Translate(extension.Pole.Position)
            .Overlaps(prototypes["lab"].CollisionBox.Translate(extension.Machine.Position)));
    }

    [Fact]
    public void PartlyUsedPacksAndSavedResearchReduceOnlyTheMissingSupply()
    {
        var technology = new NativeTechnology("automation", true, false, true, [], [new("red", 1)], 10, 600);
        var needed = LaboratoryPlanner.RequiredPacks(technology, .25, new Dictionary<string, double> { ["red"] = 2.25 });
        Assert.Equal(6, needed["red"]);
    }

    [Fact]
    public void ResearchCompletionRequiresNoAdditionalScience()
    {
        var technology = new NativeTechnology("done", true, true, false, [], [new("red", 1)], 10, 600);
        Assert.Empty(LaboratoryPlanner.RequiredPacks(technology, 0, new Dictionary<string, double>()));
    }

    [Theory]
    [InlineData(-.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void InvalidResearchProgressCannotBecomeAStockGoal(double progress)
    {
        var technology = new NativeTechnology("automation", true, false, true, [], [new("red", 1)], 10, 600);
        Assert.Throws<InvalidDataException>(() => LaboratoryPlanner.RequiredPacks(technology, progress, new Dictionary<string, double>()));
    }

    [Fact]
    public void LabPlacementOverlapsPoleCoverageWithoutCollidingWithThePole()
    {
        var map = SteamPowerPlannerTests.Map(true);
        var prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
        {
            ["lab"] = new("lab", "lab", new(new(-1.4, -1.4), new(1.4, 1.4)),
                map.Prototypes["boiler"].Mask, 3, 3)
        };
        var pole = new SpatialEntity("p", "pole", new(5.5, 5.5), new(new(5.35, 5.35), new(5.65, 5.65)), 0, "own", Power: new(0, 7));
        map = map with { Prototypes = prototypes, Entities = [pole], Items = new Dictionary<string, PlaceableItem>(map.Items) { ["lab"] = new("lab", 10) } };
        var placement = new PoweredMachinePlanner().Place(map, "lab", pole);
        Assert.NotNull(placement);
        Assert.True(new WorldBox(new(3, 3), new(8, 8)).Overlaps(prototypes["lab"].CollisionBox.Translate(placement.Position)));
        Assert.True(new SpatialCollisionField(map).PlacementClear(prototypes["lab"], placement.Position, placement.Direction));
        Assert.Null(new PoweredMachinePlanner().Place(map, "lab", pole with { Power = null }));
    }
}
