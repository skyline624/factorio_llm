using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class IndustryClusterTests
{
    private static IndustryMember Machine(string id, double x, double y) => new(id, new(new(x - 1.5, y - 1.5), new(x + 1.5, y + 1.5)));

    [Fact]
    public void DistantSitesFormSeparateClustersAndNeighboursShareOne()
    {
        // Campaign 2026-10-01 (seed 20261002): bands near (6..42, 22..45) and resource rows near (-72..-41, -26..18) did not
        // fit one ring; each site must get its own.
        var clusters = IndustryClusterPlanner.Group([
            Machine("120", 10, 30), Machine("121", 20, 30), new("zone-1", new(new(11, 34), new(35, 46)), Entity: false),
            Machine("7", -55, -15), Machine("8", -55, 10), Machine("9", -45, 0)
        ]);
        Assert.Equal(2, clusters.Count);
        var west = clusters.Single(c => c.Members.Contains("7"));
        Assert.Equal(new[] { "7", "8", "9" }, west.Members);
        Assert.Equal("cluster-7", west.Id);
        var bands = clusters.Single(c => c.Members.Contains("zone-1"));
        Assert.Equal("cluster-120", bands.Id);
        Assert.Equal(new WorldBox(new(8.5, 28.5), new(35, 46)), bands.Box);
    }

    [Fact]
    public void AChainLongerThanOneObservationIsSplitSoEveryClusterFits()
    {
        // Twelve machines ten tiles apart span 113 tiles: single linkage alone would make one ring that cannot be observed.
        var clusters = IndustryClusterPlanner.Group(Enumerable.Range(0, 12).Select(i => Machine($"{100 + i}", i * 10, 0)).ToArray());
        Assert.True(clusters.Count >= 2);
        Assert.All(clusters, c => Assert.True(c.Box.Width <= IndustryClusterPlanner.MaximumExtent && c.Box.Height <= IndustryClusterPlanner.MaximumExtent));
        Assert.Equal(12, clusters.Sum(c => c.Members.Count));
    }

    [Fact]
    public void GroupingIsIndependentOfInputOrder()
    {
        IndustryMember[] members = [Machine("3", 0, 0), Machine("1", 18, 0), Machine("2", 60, 60), Machine("4", 80, 60)];
        var first = IndustryClusterPlanner.Group(members);
        var second = IndustryClusterPlanner.Group(members.Reverse().ToArray());
        Assert.Equal(first.Select(c => (c.Id, string.Join(",", c.Members))), second.Select(c => (c.Id, string.Join(",", c.Members))));
        Assert.Equal(new[] { "cluster-1", "cluster-2" }, first.Select(c => c.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void KnownIndustryAndBandsAreMembersButConnectorsAndDefensesAreNot()
    {
        var zone = new FactoryZone(1, new(0, 0), 8, 3, 12);
        var state = new FactoryState(1, "world", [zone], [
            new("cell-a", 1, new(0, 0, true), "assembler", "assembling-machine-1", "iron-gear-wheel",
                new Dictionary<string, string> { ["machine"] = "10", ["output-inserter"] = "11", ["link-0"] = "12" }, "ready", 1),
            new("perimeter-turret@0,-8", 0, new(0, 0, true), "turret", "gun-turret", null, new Dictionary<string, string> { ["turret"] = "13" }, "ready", 1)
        ]);
        var snapshot = Snapshot(Entity("10", "assembling-machine", 1.5, 2.5), Entity("11", "inserter", 1.5, 4.5),
            Entity("12", "electric-pole", 60.5, 0.5), Entity("13", "ammo-turret", 0, -8), Entity("14", "furnace", -30, 5),
            Entity("15", "transport-belt", 40.5, 0.5), Entity("16", "furnace", 200, 200, surface: 2));
        var members = IndustryClusters.Members(state, snapshot);
        Assert.Equal(new[] { "10", "11", "14", "zone-1" }, members.Select(m => m.Id).Order(StringComparer.Ordinal));
        Assert.False(members.Single(m => m.Id == "zone-1").Entity);
        Assert.Equal(zone.Box, members.Single(m => m.Id == "zone-1").Box);
    }

    [Fact]
    public void ASupplyLineDoesNotChainTheRowAndTheBandsIntoOneSite()
    {
        // A trunk is a chain of one-tile belts: counted as industry, it would merge distant rows and bands into rings no observation covers.
        var state = new FactoryState(1, "world", [new FactoryZone(1, new(0, 0), 8, 3, 12)], [
            new("supply", 0, new(4, 0, true), SupplyLinePlanner.Kind, "transport-belt", "iron-plate", new Dictionary<string, string>
            {
                ["feeder-0"] = "20", ["collector-000"] = "21", ["trunk-000"] = "22", ["trunk-001"] = "23", ["depot-inserter"] = "24", ["output-chest"] = "25"
            }, "ready", 1)
        ]);
        var snapshot = Snapshot(Entity("20", "inserter", -60.5, 0.5), Entity("21", "transport-belt", -60.5, 1.5), Entity("22", "transport-belt", -30.5, 1.5),
            Entity("23", "transport-belt", -2.5, 6.5), Entity("24", "inserter", -1.5, 6.5), Entity("25", "container", -0.5, 6.5));
        Assert.Equal(new[] { "25", "zone-1" }, IndustryClusters.Members(state, snapshot).Select(m => m.Id).Order(StringComparer.Ordinal));
    }

    internal static FactorySnapshot Snapshot(params FactoryRecord[] entities) =>
        new("snapshot", new("world", "session", "actor", 1, 2), 10, 3610, Protocol.ToElement(new { }),
            [new("actor", "entity", "actor", "character", Protocol.ToElement(new { role = "actor", type = "character", surfaceIndex = 1,
                position = new MapPosition(0, 0), direction = 0 })), .. entities]);

    internal static FactoryRecord Entity(string id, string type, double x, double y, int surface = 1, double? health = null, double maxHealth = 200) =>
        new(id, "entity", id, type, health is null
            ? Protocol.ToElement(new { role = "factory", type, surfaceIndex = surface, position = new MapPosition(x, y), direction = 0 })
            : Protocol.ToElement(new { role = "factory", type, surfaceIndex = surface, position = new MapPosition(x, y), direction = 0, health, maxHealth }));
}
