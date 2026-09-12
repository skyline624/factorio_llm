using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class BeltTransportPlannerTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void CompleteLayoutUsesNativePickupDropAndAvailableElectricSupply(bool power, bool expected)
    {
        var map = Map(power);
        var plan = new BeltTransportPlanner().Find(map, new("belt", "arm", "pole"), "source", "target");
        Assert.Equal(expected, plan is not null);
        if (plan is null) return;
        var arm = map.Prototypes["arm"];
        MapPosition At(PlacementCandidate p, MapPosition offset)
        {
            var rotated = ExtractionPlanner.Rotate(offset, p.Direction);
            return new(p.Position.X + rotated.X, p.Position.Y + rotated.Y);
        }
        Assert.True(map.Entities.Single(e => e.Id == "source").Bounds.Contains(At(plan.SourceInserter, arm.InserterPickup!)));
        Assert.Equal(plan.Belts[0].Position, BeltRoutePlanner.Cell(At(plan.SourceInserter, arm.InserterDrop!)));
        Assert.Equal(plan.Belts[^1].Position, BeltRoutePlanner.Cell(At(plan.TargetInserter, arm.InserterPickup!)));
        Assert.True(map.Entities.Single(e => e.Id == "target").Bounds.Contains(At(plan.TargetInserter, arm.InserterDrop!)));
    }

    internal static SpatialSnapshot Map(bool power)
    {
        var map = BeltRoutePlannerTests.Map();
        return map with
        {
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes)
            {
                ["arm"] = new("arm", "inserter", new(new(-.15, -.15), new(.15, .15)), map.Prototypes["wall"].Mask, 1, 1,
                    IsElectric: true, InserterPickup: new(0, -1), InserterDrop: new(0, 1.2)),
                ["pole"] = new("pole", "electric-pole", new(new(-.15, -.15), new(.15, .15)), map.Prototypes["wall"].Mask, 1, 1,
                    SupplyArea: 3.5, MaxWireDistance: 7.5)
            },
            Items = new Dictionary<string, PlaceableItem>(map.Items) { ["arm"] = new("arm", 50), ["pole"] = new("pole", 50) },
            Entities =
            [new("source", "chest", new(.5, .5), new(new(.15, .15), new(.85, .85)), 0, "own"),
             new("target", "chest", new(8.5, .5), new(new(8.15, .15), new(8.85, .85)), 0, "own"),
             new("pole1", "pole", new(1.5, 3.5), new(new(1.35, 3.35), new(1.65, 3.65)), 0, "own", Power: new(0, power ? 1 : null)),
             new("pole2", "pole", new(7.5, 3.5), new(new(7.35, 3.35), new(7.65, 3.65)), 0, "own", Power: new(0, power ? 1 : null))]
        };
    }
}
