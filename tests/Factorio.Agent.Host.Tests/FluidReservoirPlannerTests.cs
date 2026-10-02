using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FluidReservoirPlannerTests
{
    [Fact]
    public void ThreeCoProductsKeepIndependentTankRoutesBesideAShore()
    {
        // Synthetic shore and configured multi-output machine. No captured world or shipped game data is a fixture asset.
        var map = OilMaps.Map([]);
        var tank = new EntityGeometry("tank", "storage-tank", new(new(-1.3, -1.3), new(1.3, 1.3)),
            map.Prototypes["pipe"].Mask, 3, 3, FluidBoxes: [new(1, "none", Enumerable.Range(0, 4).Select(i =>
            {
                var at = i is 0 or 3 ? new MapPosition(-1, -1) : new(1, 1);
                return new FluidPortGeometry(i + 1, "normal", i * 4, "input-output",
                    [at, ExtractionPlanner.Rotate(at, 4), ExtractionPlanner.Rotate(at, 8), ExtractionPlanner.Rotate(at, 12)], ["default"]);
            }).ToArray())]);
        string[] fluids = ["heavy-oil", "light-oil", "petroleum-gas"];
        var placement = new PlacementCandidate(new(8.5, 4.5), 8, 0);
        var machine = new SpatialEntity("refinery", "oil-refinery", placement.Position,
            map.Prototypes["oil-refinery"].CollisionBox.Translate(placement.Position), 8, "agent",
            FluidConnections: FluidCellPlanner.Ports(map.Prototypes["oil-refinery"], placement).Select(p =>
                p.FlowDirection == "output" ? p with { Filter = fluids[p.BoxIndex - 3] } : p).ToArray());
        map = map with
        {
            Actor = map.Actor with { Position = new(3.5, 4.5) }, Entities = [.. map.Entities, machine],
            Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes) { ["tank"] = tank },
            Items = new Dictionary<string, PlaceableItem>(map.Items) { ["tank"] = new("tank", 50) },
            TilePrototypes = new Dictionary<string, CollisionMask>(map.TilePrototypes)
                { ["water"] = new(["water_tile"], false, false, false) },
            Rows = map.Rows.SelectMany(r => r.X >= 11 ? new[] { r with { Name = "water" } }
                : r.X + r.Length <= 11 ? new[] { r } : new[] { r with { Length = 11 - r.X }, new TileRun(r.Y, 11, r.X + r.Length - 11, "water") }).ToArray()
        };
        var plan = new FluidReservoirPlanner().FindAll(map, "tank", "pipe", machine.Id, fluids);
        Assert.NotNull(plan);
        Assert.Equal(3, plan.Count);
        foreach (var current in plan)
            foreach (var other in plan.Where(p => p.Fluid != current.Fluid))
            {
                var body = tank.CollisionBox.Translate(other.Site.Tank.Position);
                Assert.All(current.Site.Route.Pipes, pipe =>
                {
                    Assert.False(body.Contains(pipe));
                    Assert.DoesNotContain(other.Site.Route.Pipes, at => Math.Abs(at.X - pipe.X) + Math.Abs(at.Y - pipe.Y) <= 1.01);
                });
            }
    }
}
