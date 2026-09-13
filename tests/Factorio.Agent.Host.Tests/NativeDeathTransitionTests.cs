using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class NativeDeathTransitionTests
{
    private static readonly ActorScope Previous = new("world", "session", "actor", 1, 3);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeDeathLinksThePreviousIncarnationWithoutResettingTime(bool waiting)
    {
        var result = NativeDeathTransition.Read(Observation(waiting: waiting), Previous, 100, awaitingRespawn: waiting);
        Assert.Equal(1, result.Incarnation);
        Assert.Equal(150, result.DeathTick);
        Assert.Equal(17, result.ActorUnitNumber);
    }

    [Theory]
    [InlineData("world")]
    [InlineData("jump")]
    [InlineData("old-death")]
    [InlineData("future-death")]
    [InlineData("manual")]
    [InlineData("incomplete")]
    public void UnexplainedReplacementCannotBecomeDeathRecovery(string fault) =>
        Assert.Throws<InvalidDataException>(() => NativeDeathTransition.Read(Observation(fault), Previous, 100));

    private static GameResponse Observation(string? fault = null, bool waiting = false) => new(1, "read", true, 200,
        Protocol.ToElement(new
        {
            scope = Previous with { WorldId = fault == "world" ? "other" : "world", Incarnation = waiting ? 1 : fault == "jump" ? 3 : 2, Generation = 5 },
            collectedTick = 200,
            agent = new { alive = !waiting, controlMode = fault == "manual" ? "manual" : "ai" },
            recovery = new { knownCorpsesComplete = fault != "incomplete", lastDeath = new
                { incarnation = 1, tick = fault == "old-death" ? 50 : fault == "future-death" ? 250 : 150,
                    unitNumber = 17, surfaceIndex = 1, position = new MapPosition(12, 8) } }
        }));
}
