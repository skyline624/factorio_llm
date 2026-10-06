using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FactoryZoneAnchorTests
{
    [Fact]
    public void NewBandsAimAtTheCentroidOfReadySmelterOutputs()
    {
        // The observed seed: copper and iron smelters near (33, 26) and (51, 33), the first band beside the engine at (-30, -30).
        var state = State(Smelter("copper", new(33.5, 26.5), "ready"), Smelter("iron", new(51.5, 33.5), "ready"),
            Smelter("old", new(-90, -90), "depleted"),
            new FactoryCell("gears", 1, new(0, 0, true), "assembler", "assembling-machine-1", "iron-gear-wheel",
                new Dictionary<string, string>(), "ready", 1, 0,
                new Dictionary<string, PlannedEntity> { ["output-chest"] = new("output-chest", "iron-chest", new(-30, -30), 0) }));
        Assert.Equal(new MapPosition(42.5, 30), FactoryCellBuilder.SmelterAnchor(state));
        Assert.Null(FactoryCellBuilder.SmelterAnchor(State(Smelter("planned", new(0, 0), "building"))));
    }

    private static FactoryCell Smelter(string id, MapPosition output, string status) => new(id, 0, new(1, 0, true), "smelter",
        "burner-mining-drill", "iron-plate", new Dictionary<string, string>(), status, 1, 0,
        new Dictionary<string, PlannedEntity> { ["output-chest"] = new("output-chest", "iron-chest", output, 0) });

    private static FactoryState State(params FactoryCell[] cells) => new(1, "world", [], cells);
}
