using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FluidCellBuilderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SulfurStaysByItsGasSupplyWhenTheSteamPumpIsFarAway(bool waterFirst)
    {
        var catalog = OilCatalogs.Oil();
        var gas = new MapPosition(100.5, -215.5);
        var photo = new FactorySnapshot("photo", catalog.Scope, 100, 200, Protocol.ToElement(new { }), [
            Entity("actor", "actor", new(0, 0)), Entity("pump", "factory", new(-60.5, -118.5)), Entity("refinery", "factory", gas),
            Fluid("water", "pump"), Fluid("petroleum-gas", "refinery")]);
        string[] fluids = waterFirst ? ["water", "petroleum-gas"] : ["petroleum-gas", "water"];
        Assert.Equal(gas, FluidCellBuilder.Anchor(photo, fluids, new Dictionary<string, string?>(), catalog));
        // A recipe using only water still reuses the existing source.
        Assert.Equal(new MapPosition(-60.5, -118.5), FluidCellBuilder.Anchor(photo, ["water"], new Dictionary<string, string?>(), catalog));

        static FactoryRecord Entity(string id, string role, MapPosition position) =>
            new(id, "entity", id, id, Protocol.ToElement(new { role, position }));
        static FactoryRecord Fluid(string name, string id) =>
            new(name, "fluid", id, name, Protocol.ToElement(new { contents = new Dictionary<string, double> { [name] = 90 }, sourceBoxes = new[] { new { entityId = id } } }));
    }

    [Fact]
    public void AnExtractorRateComesFromTheDepositUnderItsDrill()
    {
        var deposit = OilMaps.Crude("crude", new(8.5, .5), 150000);
        var jack = new SpatialEntity("jack", "pumpjack", deposit.Position, new WorldBox(new(-1.2, -1.2), new(1.2, 1.2)).Translate(deposit.Position), 0, "agent");
        var cell = Extractor("jack");
        // resources.lua: 150000 of a 300000 normal amount is a 50 % yield, 5 crude oil per second.
        Assert.Equal(300, FluidCellBuilder.Rate(OilMaps.Map([deposit, jack]), OilCatalogs.Oil(), cell)!.Value, 6);
        Assert.Null(FluidCellBuilder.Rate(OilMaps.Map([deposit]), OilCatalogs.Oil(), cell));
        Assert.Null(FluidCellBuilder.Rate(OilMaps.Map([jack]), OilCatalogs.Oil(), cell));
    }

    [Fact]
    public void TheNearestObservedTerrainFluidTileAnchorsANewPump()
    {
        var map = FactoryMaps.Grass(20, tile: (x, _) => x >= 10 ? "water" : "grass") with
        {
            TileFluids = new Dictionary<string, string> { ["water"] = "water" }
        };
        Assert.Equal(new MapPosition(10.5, -.5), FluidCellBuilder.Shore(map, "water", new(0, 0)));
        Assert.Null(FluidCellBuilder.Shore(map, "crude-oil", new(0, 0)));
    }

    [Fact]
    public void AnExtractorPlanNamesItsMachineDrillLikeMinerCells()
    {
        var map = OilMaps.Map([]);
        var layout = new FluidCellPlanner().Layouts(map, new("pumpjack", "inserter", "iron-chest", "small-electric-pole"),
            new(new(.5, .5), 0, 0), false, false).First();
        var plan = FluidCellBuilder.Roles(layout, "drill");
        // Maintenance configures a rebuilt "machine" with the cell recipe; an extractor's recipe field holds its product instead.
        Assert.Equal(["drill", "pole"], plan.Keys.Order());
        Assert.Equal("pumpjack", plan["drill"].Item);
    }

    [Fact]
    public void InterruptedFluidCellsResumeWithinTheirAttemptBudgetAndAreAbandonedAfterIt()
    {
        var plan = new Dictionary<string, PlannedEntity> { ["drill"] = new("drill", "pumpjack", new(8.5, .5), 0) };
        var worn = Extractor("jack-1") with { Id = "worn", Status = "building", Attempts = ResourceCellBuilder.MaximumAttempts, Plan = plan };
        var fresh = Extractor("jack-2") with { Id = "fresh", Status = "building", Attempts = 1, Plan = plan };
        var other = fresh with { Id = "sulfur", Kind = FluidCellBuilder.MachineKind, Recipe = "sulfur" };
        var state = new FactoryState(1, "world", [], [worn, fresh, other]);
        var (resume, abandoned) = FluidCellBuilder.Interrupted(state, c => c.Kind == FluidCellBuilder.ExtractorKind && c.Recipe == "crude-oil");
        Assert.Equal(fresh with { Attempts = 2 }, resume);
        Assert.Equal([worn with { Status = ResourceCellBuilder.Abandoned }], abandoned);
        // A cell registered before attempts were counted resumes as its first attempt.
        var legacy = FluidCellBuilder.Interrupted(state with { Cells = [fresh with { Attempts = 0 }] }, _ => true);
        Assert.Equal(1, legacy.Resume!.Attempts);
    }

    [Fact]
    public void AResumedCellBuildsItsLostPartsAgainAtTheirPlanPartsFirst()
    {
        var plan = new[] { "machine", "pole", "input-chest", "pipe-0", "pipe-1", "pump", "link-0" }
            .ToDictionary(r => r, r => new PlannedEntity(r, "item", new(0, 0), 0));
        var cell = new FactoryCell("cell", 0, new(0, 0, true), FluidCellBuilder.MachineKind, "chemical-plant", "sulfur",
            new Dictionary<string, string> { ["machine"] = "1", ["pole"] = "2", ["pipe-0"] = "3", ["pipe-1"] = "4", ["link-0"] = "5" },
            "building", 1, Attempts: 1, Plan: plan);
        // Pipe 4 and link 5 were destroyed while the build was interrupted.
        var standing = FluidCellBuilder.Standing(cell, new HashSet<string> { "1", "2", "3" });
        Assert.Equal(["machine", "pipe-0", "pole"], standing.Entities.Keys.Order());
        Assert.Same(cell.Plan, standing.Plan);
        Assert.Equal(["input-chest", "link-0", "pipe-1", "pump"], FluidCellBuilder.Unbuilt(standing));
    }

    [Fact]
    public void BuiltPipesAreRegisteredFromTheFactoryPhotographHoweverFarTheRouteRuns()
    {
        // The first pipes of a 70-tile route lie beyond the capture around the actor at its end; the photograph lists them all.
        var cell = new FactoryCell("cell", 0, new(0, 0, true), FluidCellBuilder.MachineKind, "chemical-plant", "sulfur",
            new Dictionary<string, string> { ["machine"] = "plant", ["pipe-0"] = "earlier" }, "building", 1, Attempts: 1,
            Plan: new Dictionary<string, PlannedEntity> { ["pipe-0"] = new("pipe-0", "pipe", new(9.5, .5), 0) });
        var scope = new ActorScope("world", "session", "actor", 1, 1);
        var snapshot = new FactorySnapshot("snapshot", scope, 100, 200, Protocol.ToElement(new { }), [
            Pipe("far", new(-60.5, .5)), Pipe("near", new(10.5, .5))]);
        var piped = FluidCellBuilder.WithPipes(cell, snapshot, ["near", "far"], "pipe");
        Assert.Equal(("far", new MapPosition(-60.5, .5)), (piped.Entities["pipe-1"], piped.Plan!["pipe-1"].Position));
        Assert.Equal(("near", new MapPosition(10.5, .5)), (piped.Entities["pipe-2"], piped.Plan!["pipe-2"].Position));
        Assert.Equal("earlier", piped.Entities["pipe-0"]);
        Assert.Throws<InvalidDataException>(() => FluidCellBuilder.WithPipes(cell, snapshot, ["near", "lost"], "pipe"));

        static FactoryRecord Pipe(string id, MapPosition position) =>
            new(id, "entity", id, "pipe", Protocol.ToElement(new { role = "factory", type = "pipe", position }));
    }

    private static FactoryCell Extractor(string drill) => new("cell", 0, new(0, 0, true), FluidCellBuilder.ExtractorKind, "pumpjack", "crude-oil",
        new Dictionary<string, string> { ["drill"] = drill }, "ready", 1);
}
