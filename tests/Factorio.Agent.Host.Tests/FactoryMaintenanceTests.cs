using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FactoryMaintenanceTests
{
    [Fact]
    public void DestroyedRegisteredEntitiesAreRebuiltTurretsFirstThenWalls()
    {
        var state = new FactoryState(1, "world", [], [
            Cell("cell-a", "assembler", "ready", ("machine", "1"), ("pole", "2")),
            Cell("wall-a", "wall", "ready", ("wall-0", "3"), ("wall-1", "4")),
            Cell("turret-a", "turret", "ready", ("turret", "5")),
            Cell("unfinished", "turret", "building", ("turret", "6")),
            Cell("legacy", "assembler", "ready", ("machine", "7")) with { Plan = null }
        ]);
        var missing = FactoryMaintenance.Missing(state, new HashSet<string> { "1", "4" });
        Assert.Equal(new[] { "5", "3", "2" }, missing.Select(m => m.PreviousId));
        Assert.Equal("turret-a", missing[0].Cell.Id);
        Assert.Equal(new MapPosition(3, 0), missing[1].Plan.Position);
        Assert.Equal("pole", missing[2].Role);
    }

    [Theory]
    [InlineData(0, 10, 10)]
    [InlineData(35, 10, 7)]
    [InlineData(95, 10, 1)]
    [InlineData(100, 10, 0)]
    [InlineData(140, 10, 0)]
    public void AmmunitionTopUpRestoresTheDeploymentReserveInWholeMagazines(long rounds, int magazine, int expected)
    {
        Assert.Equal(100, DefenseDeploymentPlanner.ReserveRounds);
        Assert.Equal(expected, FactoryMaintenance.Magazines(rounds, magazine));
    }

    [Fact]
    public async Task RegistriesWrittenBeforeRecordedPlansStillLoad()
    {
        string directory = Directory.CreateTempSubdirectory("factory-registry-").FullName;
        try
        {
            var registry = new FactoryRegistry(directory);
            await File.WriteAllTextAsync(registry.Path, """
                {"version":1,"worldId":"world","zones":[],"cells":[{"id":"cell-1","zone":1,"slot":{"band":0,"index":0,"north":true},
                "kind":"assembler","machineItem":"assembling-machine-1","recipe":"iron-gear-wheel","entities":{"machine":"12"},"status":"ready","tick":5}]}
                """);
            var legacy = await registry.LoadAsync("world", CancellationToken.None);
            Assert.Null(legacy.Cells.Single().Plan);
            var planned = Cell("wall-a", "wall", "ready", ("wall-0", "3"));
            await registry.SaveAsync(legacy.With(planned), CancellationToken.None);
            var reloaded = await registry.LoadAsync("world", CancellationToken.None);
            Assert.Equal(planned.Plan!["wall-0"], reloaded.Cells.Single(c => c.Id == "wall-a").Plan!["wall-0"]);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void CellsBuiltBeforeRecordedPlansRecoverThemFromNativeEntitiesWhileAllArePresent()
    {
        var catalog = Catalogs.Early() with
        {
            Items = new Dictionary<string, NativeItem>(Catalogs.Early().Items)
            {
                ["inserter"] = new(0, 50, PlaceEntity: "inserter", PlaceEntityType: "inserter"),
                ["iron-chest"] = new(0, 50, PlaceEntity: "iron-chest", PlaceEntityType: "container")
            }
        };
        var legacy = Cell("legacy", "assembler", "ready", ("machine", "1"), ("output-inserter", "2"), ("output-chest", "3")) with { Plan = null };
        var damaged = Cell("damaged", "assembler", "ready", ("machine", "4"), ("output-chest", "5")) with { Plan = null };
        var planned = Cell("planned", "assembler", "ready", ("machine", "6"));
        var state = new FactoryState(1, "world", [], [legacy, damaged, planned]);
        var snapshot = Snapshot(Entity("1", "assembling-machine-1", "assembling-machine", 3.5, -2.5),
            Entity("2", "inserter", "inserter", 3.5, 0.5, direction: 8), Entity("3", "iron-chest", "container", 3.5, 1.5),
            Entity("4", "assembling-machine-1", "assembling-machine", 9.5, -2.5), Entity("6", "assembling-machine-1", "assembling-machine", 15.5, -2.5));

        var recovered = FactoryMaintenance.RecoverPlans(state, snapshot, catalog).Single();
        Assert.Equal("legacy", recovered.Id);
        Assert.Equal(new PlannedEntity("output-inserter", "inserter", new(3.5, 0.5), 8), recovered.Plan!["output-inserter"]);
        Assert.Equal(new PlannedEntity("output-chest", "iron-chest", new(3.5, 1.5), 0), recovered.Plan["output-chest"]);
        Assert.Equal("assembling-machine-1", recovered.Plan["machine"].Item);
        // A recovered plan lets maintenance rebuild what the next attack destroys.
        Assert.Equal("output-chest", FactoryMaintenance.Missing(state.With(recovered), new HashSet<string> { "1", "2", "4", "6" })
            .Single(m => m.Cell.Id == "legacy").Role);
    }

    [Fact]
    public void RebuiltElectricEntitiesOffEveryPoweredNetworkAreReported()
    {
        var snapshot = Snapshot(Entity("engine", "steam-engine", "generator", 0, 0, network: 1),
            Entity("pole", "small-electric-pole", "electric-pole", 5.5, 0.5, network: 1),
            Entity("isolated", "small-electric-pole", "electric-pole", 30.5, 0.5, network: 2),
            Entity("machine", "assembling-machine-1", "assembling-machine", 7.5, 2.5, network: 1),
            Entity("unsupplied", "assembling-machine-1", "assembling-machine", 40.5, 2.5, electric: true),
            Entity("wall", "stone-wall", "wall", 12.5, 12.5));
        Assert.Equal(new[] { "isolated", "unsupplied" },
            FactoryMaintenance.Unpowered(snapshot, ["pole", "isolated", "machine", "unsupplied", "wall"]));
    }

    [Fact]
    public void PowerLinkPolesAreRegisteredAndPlannedSoMaintenanceRebuildsThem()
    {
        var cell = Cell("cell-a", "assembler", "building", ("machine", "1"), ("pole", "2"));
        var first = FactoryCellBuilder.WithLink(cell, "10", new(new(20.5, 0.5), 0, 0), "small-electric-pole");
        var second = FactoryCellBuilder.WithLink(first, "11", new(new(27.5, 0.5), 0, 0), "small-electric-pole");
        Assert.Equal("10", second.Entities["link-0"]);
        Assert.Equal("11", second.Entities["link-1"]);
        Assert.Equal(new PlannedEntity("link-1", "small-electric-pole", new(27.5, 0.5), 0), second.Plan!["link-1"]);
        var state = new FactoryState(1, "world", [], [second with { Status = "ready" }]);
        Assert.Equal("link-0", FactoryMaintenance.Missing(state, new HashSet<string> { "1", "2", "11" }).Single().Role);
    }

    [Fact]
    public void CellsSkippedByTransportListTheirMissingRolesAndWhetherARecordedPlanCanRebuildThem()
    {
        var planned = Cell("planned", "assembler", "ready", ("machine", "1"), ("output-chest", "2"));
        var legacy = Cell("legacy", "assembler", "ready", ("machine", "3"), ("output-chest", "4")) with { Plan = null };
        var degraded = FactoryMaintenance.Degraded([planned, legacy], new HashSet<string> { "1", "3" });
        Assert.Equal(new[] { "planned", "legacy" }, degraded.Select(d => d.Cell));
        Assert.All(degraded, d => Assert.Equal(new[] { "output-chest" }, d.Missing.Select(m => m.Role)));
        Assert.Equal(planned.Plan!["output-chest"].Item, degraded[0].Missing.Single().Item);
        Assert.Null(degraded[1].Missing.Single().Item);
        Assert.Empty(FactoryMaintenance.Degraded([planned], new HashSet<string> { "1", "2" }));
    }

    private static FactorySnapshot Snapshot(params FactoryRecord[] entities) =>
        new("snapshot", new("world", "session", "actor", 1, 2), 10, 3610, Protocol.ToElement(new { }), entities);

    private static FactoryRecord Entity(string id, string name, string type, double x, double y, int direction = 0, long? network = null, bool electric = false) =>
        new(id, "entity", id, name, network is not null || electric
            ? Protocol.ToElement(new { role = "factory", type, position = new MapPosition(x, y), direction, power = new { energy = 0, networkId = network } })
            : Protocol.ToElement(new { role = "factory", type, position = new MapPosition(x, y), direction }));

    private static FactoryCell Cell(string id, string kind, string status, params (string Role, string Id)[] entities) =>
        new(id, 0, new(0, 0, true), kind, kind == "wall" ? "stone-wall" : kind == "turret" ? "gun-turret" : "assembling-machine-1", null,
            entities.ToDictionary(e => e.Role, e => e.Id), status, 1,
            Plan: entities.ToDictionary(e => e.Role, e => new PlannedEntity(e.Role, kind == "wall" ? "stone-wall" : "gun-turret", new(double.Parse(e.Id, System.Globalization.CultureInfo.InvariantCulture), 0), 0)));
}
