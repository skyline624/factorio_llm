using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FactoryThroughputTests
{
    [Fact]
    public void FinishedCraftsBetweenPhotographsGiveMeasuredRatesUtilizationAndStates()
    {
        // 2026-10-08: the three normal campaigns made no chemical science in ten minutes while most assemblers waited for ingredients.
        var catalog = Catalogs.Early();
        var first = Snapshot(3600, ("gears-1", "iron-gear-wheel", 100, "working"), ("gears-2", "iron-gear-wheel", 40, "item_ingredient_shortage"),
            ("red", "automation-science-pack", 10, "item_ingredient_shortage"));
        var (sample, none) = FactoryThroughput.Measure(first, null, catalog, new Dictionary<string, double>());
        Assert.Null(none);
        // Ten minutes later: one gear machine made 300 gears, the other none, and a new machine has no comparable counter.
        var second = Snapshot(3600 + 36000, ("gears-1", "iron-gear-wheel", 400, "working"), ("gears-2", "iron-gear-wheel", 40, "item_ingredient_shortage"),
            ("red", "automation-science-pack", 15, "item_ingredient_shortage"), ("gears-3", "iron-gear-wheel", 9999, "working"));
        var (_, report) = FactoryThroughput.Measure(second, sample, catalog, new Dictionary<string, double> { ["automation-science-pack"] = 18 });
        var gears = report!.Recipes.Single(r => r.Recipe == "iron-gear-wheel");
        Assert.Equal(3, gears.Machines);
        Assert.Equal(30, gears.MeasuredCraftsPerMinute, 3);
        // Three machines at speed 0.5 and 0.5 s per craft: 180 nominal crafts per minute.
        Assert.Equal(180, gears.NominalCraftsPerMinute, 3);
        Assert.Equal(new Dictionary<string, int> { ["item_ingredient_shortage"] = 1, ["working"] = 2 }, gears.States);
        Assert.Equal(30, report.ItemsPerMinute["iron-gear-wheel"], 3);
        Assert.Equal(0.5, report.ItemsPerMinute["automation-science-pack"], 3);
        Assert.Equal(new Dictionary<string, int> { ["no_research_in_progress"] = 1 }, report.LabStates);
        Assert.Equal((2, 4), (report.Working, report.Machines));
    }

    private static FactorySnapshot Snapshot(long tick, params (string Id, string Recipe, long Finished, string State)[] machines)
    {
        var records = machines.Select(m => new FactoryRecord($"work:{m.Id}", "work", m.Id, "machine-craft", JsonSerializer.SerializeToElement(new
        {
            recipe = m.Recipe, productsFinished = m.Finished, craftingSpeed = 0.5, statusName = m.State
        }))).Append(new FactoryRecord("work:lab", "work", "lab", "lab-research",
            JsonSerializer.SerializeToElement(new { statusName = "no_research_in_progress" }))).ToArray();
        return new("s", new("world", "session", "actor", 1, 1), tick, tick, JsonSerializer.SerializeToElement(new { atomic = true }), records);
    }
}
