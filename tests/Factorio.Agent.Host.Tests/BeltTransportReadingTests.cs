using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class BeltTransportReadingTests
{
    [Fact]
    public void CountsProductionConsumptionAndAnEngagedCraftWithoutLosingTransit()
    {
        var before = new BeltTransportReading(new(10, 5, false, 2), new(1, 2, false, 2), 0);
        var after = new BeltTransportReading(new(10, 7, false, 2), new(0, 3, true, 2), 1);
        Assert.Equal(3, after.DeliveredSince(before));
    }

    [Fact]
    public void ADisappearingItemIsNotReportedAsDelivery()
    {
        var before = new BeltTransportReading(new(10, 0, false, 0), new(0, 0, false, 0), 0);
        var after = new BeltTransportReading(new(9, 0, false, 0), new(0, 0, false, 0), 0);
        Assert.Throws<InvalidDataException>(() => after.DeliveredSince(before));
    }
}
