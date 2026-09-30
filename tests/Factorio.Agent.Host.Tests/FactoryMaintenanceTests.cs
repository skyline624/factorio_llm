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

    private static FactoryCell Cell(string id, string kind, string status, params (string Role, string Id)[] entities) =>
        new(id, 0, new(0, 0, true), kind, kind == "wall" ? "stone-wall" : kind == "turret" ? "gun-turret" : "assembling-machine-1", null,
            entities.ToDictionary(e => e.Role, e => e.Id), status, 1,
            entities.ToDictionary(e => e.Role, e => new PlannedEntity(e.Role, kind == "wall" ? "stone-wall" : "gun-turret", new(double.Parse(e.Id, System.Globalization.CultureInfo.InvariantCulture), 0), 0)));
}
