using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class BoilerFuelChoiceTests
{
    private static readonly IReadOnlyDictionary<string, NativeItem> Items = new Dictionary<string, NativeItem>
    {
        ["coal"] = new(4_000_000, 50, "chemical"),
        ["wood"] = new(2_000_000, 100, "chemical")
    };

    [Fact]
    public void TheFuelAlreadyBurningWinsBecauseOneSlotHoldsOneItem()
    {
        // Campaign 2026-09-30 (seed 20261002): carried wood was inserted into a boiler holding coal and the engine refused it.
        var fuel = PoweredMachineController.ChooseFuel(["coal", "wood"], Items,
            loaded: new Dictionary<string, long> { ["coal"] = 3 }, carried: new Dictionary<string, long> { ["wood"] = 4 });
        Assert.Equal("coal", fuel);
    }

    [Fact]
    public void AnEmptyBoilerTakesCarriedFuelThenTheRichestOne()
    {
        Assert.Equal("wood", PoweredMachineController.ChooseFuel(["coal", "wood"], Items, new Dictionary<string, long>(),
            new Dictionary<string, long> { ["wood"] = 4 }));
        Assert.Equal("coal", PoweredMachineController.ChooseFuel(["coal", "wood"], Items, new Dictionary<string, long>(),
            new Dictionary<string, long>()));
    }
}
