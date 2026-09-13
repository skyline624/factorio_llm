using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class LaboratoryPlannerTests
{
    [Fact]
    public void LargeSiloPlacementUsesItsWholeNativeFootprint()
    {
        var map = SteamPowerPlannerTests.Map(true);
        var silo = new EntityGeometry("silo", "rocket-silo", new(new(-4.4, -4.4), new(4.4, 4.4)), map.Prototypes["boiler"].Mask, 9, 9);
        var pole = new SpatialEntity("p", "pole", new(5.5, 5.5), new(new(5.35, 5.35), new(5.65, 5.65)), 0, "own", Power: new(0, 7));
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes) { ["silo"] = silo },
            Entities = [pole], Items = new Dictionary<string, PlaceableItem>(map.Items) { ["silo"] = new("silo", 1) } };
        var placement = new PoweredMachinePlanner().Place(map, "silo", pole);
        Assert.NotNull(placement);
        Assert.True(new SpatialCollisionField(map).PlacementClear(silo, placement.Position, placement.Direction));
        Assert.True(new WorldBox(new(3, 3), new(8, 8)).Overlaps(silo.CollisionBox.Translate(placement.Position)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ChemicalPlacementLeavesItsExternalFluidConnectionClearOfExistingPipes(int clearance)
    {
        var map = SteamPowerPlannerTests.Map(true);
        var pole = new SpatialEntity("p", "pole", new(5.5, 5.5), new(new(5.35, 5.35), new(5.65, 5.65)), 0, "own", Power: new(0, 1));
        var chemical = new EntityGeometry("chemical", "assembling-machine", new(new(-1.4, -1.4), new(1.4, 1.4)),
            map.Prototypes["boiler"].Mask, 3, 3, FluidBoxes: [new(1, "input", [new(1, "normal", 0, "input",
                [new(0, -1), new(1, 0), new(0, 1), new(-1, 0)], ["default"])])]);
        var pipe = PipeRoutePlannerTests.Map().Prototypes["pipe"];
        map = map with { Entities = [pole], Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            { ["chemical"] = chemical, ["pipe"] = pipe }, Items = new Dictionary<string, PlaceableItem>(map.Items)
            { ["chemical"] = new("chemical", 10), ["pipe"] = new("pipe", 100) } };
        var planner = new PoweredMachinePlanner();
        var first = planner.Place(map, "chemical", pole)!;
        var offset = ExtractionPlanner.Rotate(new(0, -1 - clearance), first.Direction);
        var portCell = new MapPosition(first.Position.X + offset.X, first.Position.Y + offset.Y);
        map = map with { Entities = [pole, new("existing-pipe", "pipe", portCell, pipe.CollisionBox.Translate(portCell), 0, "own")] };
        var replanned = planner.Place(map, "chemical", pole);
        Assert.True(replanned is null || replanned.Position != first.Position || replanned.Direction != first.Direction);
    }

    [Fact]
    public void InstallationUsesANearbyOwnedNetworkInsteadOfTheFirstHistoricalPole()
    {
        var map = SteamPowerPlannerTests.Map(true);
        SpatialEntity Pole(string id, double distance, bool connected = true) =>
            new(id, "pole", new(map.Actor.Position.X + distance, map.Actor.Position.Y),
                new(new(0, 0), new(.3, .3)), 0, "own", Power: connected ? new(0, 1) : null);
        map = map with { Entities = [Pole("01-old", 20), Pole("99-near", 4), Pole("unconnected", 1, false), Pole("foreign", .5)] };
        Assert.Equal("99-near", new PoweredMachinePlanner().NearestSupply(map,
            new HashSet<string> { "01-old", "99-near", "unconnected" })!.Id);
    }

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
