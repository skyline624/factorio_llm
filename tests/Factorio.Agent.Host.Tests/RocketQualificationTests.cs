using Factorio.Agent.Host;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class RocketQualificationTests
{
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
}
