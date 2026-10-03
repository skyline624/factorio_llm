using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FactoryLaboratoryAdoptionTests
{
    [Fact]
    public void AnUnregisteredPoweredLabKeepsItsNativeMachineAndFreePolePlans()
    {
        var (state, stock, map, catalog) = Scenario();
        var cell = FactoryLaboratoryAdoption.Plan(state, stock, map, catalog, "lab")!;
        Assert.Equal("lab", cell.Entities["machine"]);
        Assert.Equal("pole", cell.Entities["pole"]);
        Assert.Equal(0, cell.Zone);
        Assert.Equal("ready", cell.Status);
        Assert.Equal(new MapPosition(5.5, .5), cell.Plan!["machine"].Position);
        Assert.Equal(4, cell.Plan["machine"].Direction);
    }

    [Fact]
    public void ThePoleOfAnotherCellRetainsItsOwnerWhileTheLabIsReused()
    {
        var (state, stock, map, catalog) = Scenario();
        state = state.With(new FactoryCell("power", 0, new(0, 0, true), "power", "steam-engine", null,
            new Dictionary<string, string> { ["pole"] = "pole" }, "ready", 1));
        var cell = FactoryLaboratoryAdoption.Plan(state, stock, map, catalog, "lab")!;
        Assert.Single(cell.Entities);
        Assert.Single(cell.Plan!);
        Assert.Equal("lab", cell.Entities["machine"]);
    }

    [Theory]
    [InlineData("claimed")]
    [InlineData("planned")]
    [InlineData("unfed")]
    [InlineData("foreign")]
    [InlineData("moved")]
    public void ClaimedUnpoweredForeignOrChangedLabsCannotBeAdopted(string missing)
    {
        var (state, stock, map, catalog) = Scenario();
        if (missing == "claimed") state = state.With(new FactoryCell("old", 1, new(0, 0, true), "lab", "lab", null,
            new Dictionary<string, string> { ["machine"] = "lab" }, "ready", 1));
        if (missing == "planned") state = state.With(new FactoryCell("open", 1, new(0, 0, true), "lab", "lab", null,
            new Dictionary<string, string>(), "building", 1, Plan: new Dictionary<string, PlannedEntity>
                { ["machine"] = new("machine", "lab", new(5.5, .5), 4) }));
        if (missing == "unfed") stock = stock with { Records = stock.Records.Where(r => r.EntityId != "source").ToArray() };
        if (missing == "foreign") stock = stock with { Records = stock.Records.Select(r => r.EntityId == "lab" ? r with
            { Data = Protocol.ToElement(new { role = "foreign", type = "lab", position = new MapPosition(5.5, .5), direction = 4, electricNetworkId = 1 }) } : r).ToArray() };
        if (missing == "moved") map = map with { Entities = map.Entities.Select(e => e.Id == "lab" ? e with { Position = new(6.5, .5) } : e).ToArray() };
        Assert.Null(FactoryLaboratoryAdoption.Plan(state, stock, map, catalog, "lab"));
    }

    [Fact]
    public void AChangedActorScopeStopsRegistration()
    {
        var (state, stock, map, catalog) = Scenario();
        map = map with { Scope = map.Scope with { Generation = map.Scope.Generation + 1 } };
        Assert.Throws<InvalidDataException>(() => FactoryLaboratoryAdoption.Plan(state, stock, map, catalog, "lab"));
    }

    private static (FactoryState State, FactorySnapshot Stock, SpatialSnapshot Map, ProductionCatalog Catalog) Scenario()
    {
        var map = FactoryMaps.Grass(24);
        var geometry = new EntityGeometry("lab", "lab", new(new(-1.3, -1.3), new(1.3, 1.3)), map.Prototypes["iron-chest"].Mask, 3, 3);
        var lab = new SpatialEntity("lab", "lab", new(5.5, .5), geometry.CollisionBox.Translate(new(5.5, .5)), 4, "agent", Power: new(1000, 1));
        var pole = new SpatialEntity("pole", "small-electric-pole", new(3.5, .5),
            map.Prototypes["small-electric-pole"].CollisionBox.Translate(new(3.5, .5)), 0, "agent", Power: new(0, 1));
        map = map with { Prototypes = new Dictionary<string, EntityGeometry>(map.Prototypes) { ["lab"] = geometry }, Entities = [lab, pole] };
        var catalog = new ProductionCatalog(map.Scope, 100, [], new Dictionary<string, NativeItem>
            { ["lab"] = new(0, 50, PlaceEntity: "lab", PlaceEntityType: "lab"),
              ["small-electric-pole"] = new(0, 50, PlaceEntity: "small-electric-pole", PlaceEntityType: "electric-pole") },
            new Dictionary<string, NativeMaterial[]>(), new Dictionary<string, NativeFurnace>(), new Dictionary<string, bool>());
        FactoryRecord Entity(string id, string name, string type, MapPosition position, int direction) =>
            new("entity:" + id, "entity", id, name, Protocol.ToElement(new { role = "factory", type, position, direction, electricNetworkId = 1 }));
        var stock = new FactorySnapshot("photo", map.Scope, 100, 200, Protocol.ToElement(new { atomic = true }),
            [Entity("lab", "lab", "lab", lab.Position, 4), Entity("pole", "small-electric-pole", "electric-pole", pole.Position, 0),
                Entity("source", "steam-engine", "generator", new(0, 0), 0)]);
        return (new(1, map.Scope.WorldId, [], []), stock, map, catalog);
    }
}
