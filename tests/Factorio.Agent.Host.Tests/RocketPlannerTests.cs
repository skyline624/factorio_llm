using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class RocketPlannerTests
{
    [Fact]
    public void SupplyBatchAccountsForLoadedStocksAndNativeCapacity()
    {
        var silo = Silo() with { Inputs = new Dictionary<string, long> { ["processor"] = 48, ["structure"] = 50 },
            Insertable = new Dictionary<string, long> { ["processor"] = 100, ["structure"] = 100, ["fuel"] = 7 } };
        var batch = RocketPlanner.SupplyBatch(Prototype(), silo, Recipe());
        Assert.Equal(2, batch["processor"]);
        Assert.Equal(0, batch["structure"]);
        Assert.Equal(7, batch["fuel"]);
        Assert.Empty(RocketPlanner.SupplyBatch(Prototype(), silo with { Status = "rocket_flying" }, Recipe()));
    }

    [Fact]
    public void EngagedCycleAndLoadedInputsAreNotRequestedAgain()
    {
        var silo = Silo() with { Parts = 98, InProcess = true,
            Inputs = new Dictionary<string, long> { ["processor"] = 3, ["structure"] = 10 } };
        var plan = RocketPlanner.Next(Prototype(), silo, Recipe());
        Assert.Equal("supply", plan.Kind);
        Assert.Equal(7, plan.RequiredItems["processor"]);
        Assert.Equal(0, plan.RequiredItems["structure"]);
        Assert.Equal(10, plan.RequiredItems["fuel"]);
    }

    [Theory]
    [InlineData("rocket_ready", "launch")]
    [InlineData("create_rocket", "wait")]
    [InlineData("launch_started", "wait")]
    [InlineData("rocket_flying", "wait")]
    public void AssemblyAndLaunchPhasesNeverRefillResetPartCounters(string status, string expected)
    {
        var plan = RocketPlanner.Next(Prototype(), Silo() with { Parts = 0, Status = status, RocketPresent = true }, Recipe());
        Assert.Equal(expected, plan.Kind);
        Assert.Empty(plan.RequiredItems);
    }

    [Fact]
    public void FullPartCountWaitsForTheNativeRocketReadyPhase()
    {
        Assert.Equal("wait", RocketPlanner.Next(Prototype(), Silo() with { Parts = 100 }, Recipe()).Kind);
        Assert.Throws<InvalidDataException>(() => RocketPlanner.Next(Prototype(), Silo() with { Parts = 101 }, Recipe()));
    }

    [Fact]
    public void ReadyWithoutAnActualRocketIsNotALaunchableState()
    {
        Assert.Throws<InvalidDataException>(() => RocketPlanner.Next(Prototype(), Silo() with { Status = "rocket_ready" }, Recipe()));
    }

    [Fact]
    public void SnapshotMustBeAtomicAndMatchTheResponseTick()
    {
        var state = new RocketSnapshot(new("w", "s", "a", 1, 1), 12, 1, 0, true, true,
            new Dictionary<string, RocketSiloPrototype> { ["silo-item"] = Prototype() }, [Silo()]);
        Assert.Contains("rocket_state", FactorioGameClient.BuildCommand(GameRequest.Create("rocket_state")));
        Assert.Equal(12, RocketSnapshot.Parse(new(1, "r", true, 12, Protocol.ToElement(state))).CollectedTick);
        Assert.Throws<InvalidDataException>(() => RocketSnapshot.Parse(new(1, "r", true, 13, Protocol.ToElement(state))));
        Assert.Throws<InvalidDataException>(() => RocketSnapshot.Parse(new(1, "r", true, 12, Protocol.ToElement(state with { Atomic = false }))));
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("prototypes")]
    [InlineData("silos")]
    public void NullObservationFieldsCannotBecomeAnEmptySuccessfulState(string field)
    {
        var state = new RocketSnapshot(new("w", "s", "a", 1, 1), 12, 1, 0, true, true,
            new Dictionary<string, RocketSiloPrototype> { ["silo-item"] = Prototype() }, [Silo()]);
        var json = System.Text.Json.Nodes.JsonNode.Parse(Protocol.ToElement(state).GetRawText())!.AsObject();
        json[field] = null;
        Assert.Throws<InvalidDataException>(() => RocketSnapshot.Parse(new(1, "r", true, 12, Protocol.ToElement(json))));
    }

    private static RocketSiloPrototype Prototype() => new("silo", "parts", 100, 1, 66500);
    private static ObservedRocketSilo Silo() => new("owned", "silo", new(0, 0), "parts", 0, "building_rocket", false, false,
        1000, new Dictionary<string, long>(), new Dictionary<string, long>(), 1);
    private static NativeRecipe Recipe() => new("parts", true, "rocket-building", 3,
        [new("processor", "item", 10), new("structure", "item", 10), new("fuel", "item", 10)], [new("rocket-part", "item", 1)], true);
}
