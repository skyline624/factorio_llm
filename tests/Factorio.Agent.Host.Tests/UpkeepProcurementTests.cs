using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class UpkeepProcurementTests
{
    [Fact]
    public void TheMostMissingRebuildPartsComeFirstWithinTheRoundBounds()
    {
        // Seed 20261107, 2026-10-08: 22 poles, 11 chests, 10 turrets, 9 arms and 6 furnaces were missing after attacks.
        var plan = UpkeepProcurement.Plan(new Dictionary<string, long>
        {
            ["small-electric-pole"] = 22, ["iron-chest"] = 11, ["gun-turret"] = 10, ["fast-inserter"] = 9, ["steel-furnace"] = 6,
            ["splitter"] = 2, ["transport-belt"] = 400, ["pipe"] = 0
        });
        Assert.Equal(UpkeepProcurement.MaximumItems, plan.Count);
        Assert.Equal(("transport-belt", UpkeepProcurement.MaximumUnits), plan[0]);
        Assert.Equal(("small-electric-pole", 22), plan[1]);
        Assert.DoesNotContain(plan, p => p.Item is "pipe" or "splitter");
        Assert.Empty(UpkeepProcurement.Plan(new Dictionary<string, long>()));
    }
}
