using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class PowerSearchAnchorTests
{
    [Fact]
    public void FirstPowerSearchUsesKnownProductionBuildingsAndIgnoresOtherObjects()
    {
        var catalog = Catalog();
        var anchor = SteamPowerPlanner.FactoryAnchor([("furnace", new(10, 20)), ("drill", new(14, 24)), ("chest", new(500, 500))], catalog);
        Assert.Equal(new MapPosition(12, 22), anchor);
    }

    [Fact]
    public void WithoutAKnownFactoryNoLocationIsInvented()
    {
        Assert.Null(SteamPowerPlanner.FactoryAnchor([("chest", new(500, 500))], Catalog()));
    }

    private static ProductionCatalog Catalog() => new(new("world", "session", "actor", 1, 1), 1, [],
        new Dictionary<string, NativeItem> { ["furnace"] = new(0, 50, PlaceEntity: "furnace", PlaceEntityType: "furnace"),
            ["drill"] = new(0, 50, PlaceEntity: "drill", PlaceEntityType: "mining-drill"),
            ["chest"] = new(0, 50, PlaceEntity: "chest", PlaceEntityType: "container") },
        new Dictionary<string, NativeMaterial[]>(), new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>());
}
