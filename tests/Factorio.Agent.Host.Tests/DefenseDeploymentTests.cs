using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class DefenseDeploymentTests
{
    private static InstalledTurret Turret(string id, long rounds = 100, bool active = true, double? range = 18) =>
        new(id, "gun-turret", new(0, 0), "ammo:" + id, rounds, active, range, "firearm-magazine");

    [Fact]
    public void ExistingEmptyTurretIsServicedBeforeAnyNewConstruction()
    {
        var state = new DefenseFactoryState(1, [], [Turret("loaded"), Turret("empty", 0, range: null)]);
        var step = DefenseDeploymentPlanner.Next(state, "gun-turret", 3, new(0, 0));
        Assert.Equal("service", step.Kind);
        Assert.Equal("empty", step.Turret?.Id);
    }

    [Fact]
    public void CompletionRequiresNativeEffectiveRangeAndReserveNotJustAMagazineCount()
    {
        var ready = new DefenseFactoryState(1, [], [Turret("ready")]);
        Assert.Equal("complete", DefenseDeploymentPlanner.Next(ready, "gun-turret", 1, new(0, 0)).Kind);
        foreach (var insufficient in new[] { Turret("partial", 99), Turret("unsupported", range: null) })
            Assert.Equal("service", DefenseDeploymentPlanner.Next(ready with { Turrets = [insufficient] }, "gun-turret", 1, new(0, 0)).Kind);
        Assert.Equal("build", DefenseDeploymentPlanner.Next(ready with { Turrets = [Turret("disabled", active: false)] }, "gun-turret", 1, new(0, 0)).Kind);
        Assert.Equal("build", DefenseDeploymentPlanner.Next(ready, "another-turret", 1, new(0, 0)).Kind);
    }

    [Fact]
    public void NextAnchorPrioritizesExposedIndustryAndIgnoresInactiveOrEmptyTurrets()
    {
        var state = new DefenseFactoryState(1, [new("near", new(0, 0)), new("remote", new(40, 0))], [Turret("loaded")]);
        Assert.Equal("remote", DefenseDeploymentPlanner.SelectAnchor(state, new(0, 0), new(0, 0)).Id);
        Assert.Equal(0, DefenseDeploymentPlanner.Coverage(state.Anchors[0], [Turret("empty", 0), Turret("off", active: false)]));
    }
}
