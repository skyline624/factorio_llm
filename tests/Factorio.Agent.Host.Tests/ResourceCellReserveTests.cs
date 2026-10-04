using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ResourceCellReserveTests
{
    private static readonly ResourceCellEquipment Miner = new("electric-mining-drill", "iron-chest", Pole: "small-electric-pole");
    private static readonly ResourceCellEquipment Smelter = Miner with { Furnace = "stone-furnace", Inserter = "inserter" };

    [Fact]
    public void RichSingleTileOutranksBroadNearlyDepletedPatch()
    {
        var rich = FactoryMaps.Patch("coal", 14, 0, 15, 1, amount: 10000).Single();
        var map = FactoryMaps.Grass(32, [.. FactoryMaps.Patch("coal", -6, -6, 6, 6, amount: 1), rich]);
        var row = Find(map, "coal").Row!;
        var drill = new ResourceCellPlanner().Layout(map, row, 0).Role("drill")!;
        Assert.True(Area(map, drill).Contains(rich.Position));
    }

    [Fact]
    public void TraceOreIsNotAnInstalledProductionSite()
    {
        var map = FactoryMaps.Grass(16, FactoryMaps.Patch("coal", -5, -5, 5, 5, amount: 2).ToArray());
        Assert.Equal(ResourceRowSearchStatus.NoSite, Find(map, "coal").Status);
    }

    [Fact]
    public void PlannedCellMustStillHaveReserveBeforeConstruction()
    {
        var map = FactoryMaps.Grass(16, FactoryMaps.Patch("coal", -5, -5, 5, 5).ToArray());
        var row = Find(map, "coal").Row!;
        var depleted = map with { Entities = map.Entities.Select(e => e with { Amount = 1 }).ToArray() };
        Assert.False(new ResourceCellPlanner().Fits(depleted, Catalogs.Raw(), row, 0));
    }

    [Theory]
    [InlineData(400, 1)]
    [InlineData(600, 2)]
    public void SharedOreCannotBeCountedInFullForEachNewCell(double amount, int expectedCells)
    {
        var map = FactoryMaps.Grass(16, FactoryMaps.Patch("coal", 0, 0, 1, 1, amount).ToArray());
        var search = Find(map, "coal", cells: 2);
        Assert.Equal(ResourceRowSearchStatus.Found, search.Status);
        Assert.Equal(expectedCells, search.Row!.Cells);
    }

    [Theory]
    [InlineData(200, ResourceRowSearchStatus.NoSite)]
    [InlineData(300, ResourceRowSearchStatus.Found)]
    public void SmelterReserveIncludesAllOreConsumedByTheRecipe(double amount, ResourceRowSearchStatus expected)
    {
        var map = FactoryMaps.Grass(16, FactoryMaps.Patch("stone", 0, 0, 1, 1, amount).ToArray());
        Assert.Equal(expected, Find(map, "stone-brick").Status);
    }

    [Fact]
    public void NativeMiningYieldConvertsDepositAmountIntoIngredientStock()
    {
        var catalog = Catalogs.Raw();
        catalog = catalog with
        {
            Mining = new Dictionary<string, NativeMaterial[]>(catalog.Mining)
            {
                ["stone"] = [catalog.Mining["stone"][0] with { Amount = 2 }]
            }
        };
        // 200 deposit units yield 400 stone, enough for ten minutes at the furnace limit (18.75 bricks/min).
        var map = FactoryMaps.Grass(16, FactoryMaps.Patch("stone", 0, 0, 1, 1, amount: 200).ToArray());
        Assert.Equal(ResourceRowSearchStatus.Found, Find(map, "stone-brick", catalog: catalog).Status);
    }

    [Theory]
    [InlineData("electric-mining-drill", ResourceRowSearchStatus.NoSite)]
    [InlineData("burner-mining-drill", ResourceRowSearchStatus.Found)]
    public void ObservedDrillsAlreadyCompetingForOreShareReserveByMiningSpeed(string existing, ResourceRowSearchStatus expected)
    {
        var map = FactoryMaps.Grass(16, FactoryMaps.Patch("coal", 0, 0, 1, 1, amount: 450).ToArray());
        Assert.Equal(ResourceRowSearchStatus.Found, Find(map, "coal").Status);
        var geometry = map.Prototypes[existing];
        var position = existing == "electric-mining-drill" ? new MapPosition(-1.5, .5) : new(0, 0);
        var drill = new SpatialEntity("existing", existing, position, geometry.CollisionBox.Translate(position), 0, "agent");
        map = map with { Entities = [.. map.Entities, drill] };
        var rich = map with { Entities = map.Entities.Select(e => e.Amount is not null ? e with { Amount = 900 } : e).ToArray() };
        Assert.Equal(ResourceRowSearchStatus.Found, Find(rich, "coal").Status); // The competing drill leaves a valid site.
        Assert.Equal(expected, Find(map, "coal").Status);
    }

    private static ResourceRowSearch Find(SpatialSnapshot map, string product, int cells = 1, ProductionCatalog? catalog = null)
    {
        catalog ??= Catalogs.Raw();
        var supply = ResourceCellPlanner.Supply(catalog, product)!;
        return new ResourceCellPlanner().Find(map, catalog, supply, supply.Kind == "smelter" ? Smelter : Miner, cells, new(0, 0));
    }

    private static WorldBox Area(SpatialSnapshot map, PlannedEntity drill)
    {
        double radius = map.Prototypes[drill.Item].MiningRadius!.Value;
        return new(new(drill.Position.X - radius, drill.Position.Y - radius), new(drill.Position.X + radius, drill.Position.Y + radius));
    }
}
