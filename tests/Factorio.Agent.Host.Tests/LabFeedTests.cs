using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class LabFeedTests
{
    private const string Red = "automation-science-pack", Green = "logistic-science-pack";

    [Theory]
    [InlineData(0, 50, true)]
    [InlineData(9, 50, true)]
    [InlineData(10, 50, false)]
    [InlineData(0, 0, false)]
    public void EmptyLabsAreFedFirstOnlyWhenAnOutputChestHoldsTheirPack(long inLab, long inChest, bool expected)
    {
        var snapshot = Snapshot(("lab", Red, inLab), ("out", Red, inChest));
        Assert.Equal(expected, FactoryResearchController.LabsWaitForStockedPacks(State(), snapshot, [Red]));
    }

    [Fact]
    public void PacksTheTechnologyDoesNotUseDoNotTriggerTheTour() =>
        Assert.False(FactoryResearchController.LabsWaitForStockedPacks(State(), Snapshot(("lab", Red, 0), ("out", Red, 50)), [Green]));

    [Fact]
    public void AFactoryWithoutReadyLabsHasNothingToFeed()
    {
        var state = State() with { Cells = State().Cells.Where(c => c.Kind != "lab").ToArray() };
        Assert.False(FactoryResearchController.LabsWaitForStockedPacks(state, Snapshot(("out", Red, 50)), [Red]));
    }

    private static FactoryState State()
    {
        FactoryCell Cell(string id, string kind, string? recipe, Dictionary<string, string> entities) =>
            new(id, 1, new(0, 0, true), kind, kind == "lab" ? "lab" : "assembling-machine-1", recipe, entities, "ready", 100,
                Plan: new Dictionary<string, PlannedEntity>());
        return new(1, "w", [new(1, new(0, 0), 4, 6, 6)],
        [
            Cell("science", "assembler", Red, new() { ["machine"] = "machine", ["input-chest"] = "in", ["output-chest"] = "out" }),
            Cell("laboratory", "lab", null, new() { ["machine"] = "lab" })
        ]);
    }

    private static FactorySnapshot Snapshot(params (string Entity, string Item, long Count)[] inventories)
    {
        var records = new List<FactoryRecord>();
        foreach (string id in new[] { "machine", "in", "out", "lab" })
            records.Add(new(id, "entity", id, id, JsonSerializer.SerializeToElement(new { role = "factory", type = id == "lab" ? "lab" : "container" })));
        foreach (var (entity, item, count) in inventories)
            records.Add(new($"{entity}-items", "inventory", entity, $"{entity}-items",
                JsonSerializer.SerializeToElement(new { items = new Dictionary<string, long> { [item] = count } })));
        return new("s", new("w", "s", "a", 1, 1), 10, 20, JsonSerializer.SerializeToElement(new { atomic = true }), records);
    }
}
