using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class ResearchSnapshotTests
{
    [Fact]
    public void ResearchObservationIsAnAllowedTypedReadCommand()
    {
        Assert.Contains("research_state", FactorioGameClient.BuildCommand(GameRequest.Create("research_state", new { technology = "automation" })));
    }

    [Theory]
    [InlineData(.5, true)]
    [InlineData(1.5, false)]
    [InlineData(-.5, false)]
    public void DurabilityCannotExceedTheCountedPacks(double units, bool valid)
    {
        var state = new ResearchSnapshot(new("w", "s", "a", 1, 1), 12, 1, "automation", false, 0,
            new Dictionary<string, LaboratoryPrototype>(), [], new Dictionary<string, double>(), true, true,
            new Dictionary<string, long> { ["red"] = 1 }, new Dictionary<string, double> { ["red"] = units });
        var response = new GameResponse(1, "r", true, 12, Protocol.ToElement(state));
        if (valid) Assert.Equal(units, ResearchSnapshot.Parse(response, "automation").ActorScienceUnits["red"]);
        else Assert.Throws<InvalidDataException>(() => ResearchSnapshot.Parse(response, "automation"));
    }
}
