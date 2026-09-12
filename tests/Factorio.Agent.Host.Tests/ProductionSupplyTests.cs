using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ProductionSupplyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenewingAReserveCannotCollectItsOwnFuel(bool otherSource)
    {
        ProductionEntity Chest(string id) => new(id, "iron-chest", new(0, 0), null,
            Protocol.ToElement(new { output = new { items = new Dictionary<string, long> { ["wood"] = 44 } } }));
        var state = new ProductionState(new("world", "session", "actor", 1, 1), 100, "ai",
            new Dictionary<string, long>(), otherSource ? [Chest("reserve"), Chest("supply")] : [Chest("reserve")]);

        var selected = state.AvailableOutput("wood", new HashSet<string> { "reserve" });

        Assert.Equal(otherSource ? "supply" : null, selected?.Id);
        Assert.Equal(44, state.Entities[0].Count("output", "wood"));
    }
}
