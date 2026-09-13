using Factorio.Agent.Host;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class RocketQualificationTests
{
    [Fact]
    public async Task PreparedRetreatCannotModifyANormalCampaign()
    {
        var session = new RuntimeSession("nonexistent-normal-campaign", "factorio.exe", "config.ini", "mods", "save.zip",
            0, 1, 2, "test-only", "session", "world", 1, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RetreatQualification(session).RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PreparedEquipmentCannotModifyANormalCampaign()
    {
        var session = new RuntimeSession("nonexistent-normal-campaign", "factorio.exe", "config.ini", "mods", "save.zip",
            0, 1, 2, "test-only", "session", "world", 1, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new EquipmentQualification(session).RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PreparedDeathCannotModifyANormalCampaign()
    {
        var session = new RuntimeSession("nonexistent-normal-campaign", "factorio.exe", "config.ini", "mods", "save.zip",
            0, 1, 2, "test-only", "session", "world", 1, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DeathRecoveryQualification(session).RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SmeltingFuelFixtureCannotModifyANormalCampaign()
    {
        var session = new RuntimeSession("nonexistent-normal-campaign", "factorio.exe", "config.ini", "mods", "save.zip",
            0, 1, 2, "test-only", "session", "world", 1, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SmeltingFuelQualification(session).RunAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("iron-ore")]
    [InlineData("iron-plate")]
    public async Task ElectricExtractionFixtureCannotModifyANormalCampaign(string item)
    {
        var session = new RuntimeSession("nonexistent-normal-campaign", "factorio.exe", "config.ini", "mods", "save.zip",
            0, 1, 2, "test-only", "session", "world", 1, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ElectricExtractionQualification(session, item).RunAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedQualificationRefusesNormalCampaignBeforeAnyFileOrNetworkAccess(bool furnace)
    {
        var session = new RuntimeSession("nonexistent-normal-campaign", "factorio.exe", "config.ini", "mods", "save.zip",
            0, 1, 2, "test-only", "session", "world", 1, false);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => furnace
            ? new FurnaceFuelQualification(session).RunAsync(CancellationToken.None)
            : new RocketQualification(session).RunAsync(CancellationToken.None));
        Assert.Contains("fixture", error.Message);
    }

    [Theory]
    [InlineData("steel-plate")]
    [InlineData("stone-brick")]
    public async Task FurnaceFleetFixtureCannotRunAgainstANormalCampaign(string item)
    {
        var session = new RuntimeSession("nonexistent-normal-campaign", "factorio.exe", "config.ini", "mods", "save.zip",
            0, 1, 2, "test-only", "session", "world", 1, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FurnaceFleetQualification(session, item).RunAsync(CancellationToken.None));
    }
}
