using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Host;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class CheckpointStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "factorio-checkpoint-" + Guid.NewGuid().ToString("N"));
    private static readonly ActorScope Scope = new("world", "old-session", "actor", 1, 7);
    private RuntimeSession Session => new(directory, "factorio.exe", "config.ini", "mods", "active.zip", 0, 1, 2,
        "fixture", Scope.SessionId, Scope.WorldId, 1, true);

    public CheckpointStoreTests() => Directory.CreateDirectory(Path.Combine(directory, "saves"));

    [Fact]
    public async Task TamperedCheckpointIsRejectedBeforeLaunchingTheEngine()
    {
        await SealAsync();
        await File.AppendAllTextAsync(CheckpointStore.SavePath(Session), "modified");
        await Assert.ThrowsAsync<SessionDivergenceException>(() => CheckpointStore.VerifyFileAsync(Session, default));
    }

    [Theory]
    [InlineData(101, 1, 7)]
    [InlineData(100, 2, 7)]
    [InlineData(100, 1, 8)]
    public async Task ACheckpointCannotEraseLaterObservedHistory(long tick, long incarnation, long generation)
    {
        await SealAsync();
        await LocalJson.WriteAsync(Path.Combine(directory, "observation-watermark.json"),
            new SessionGameClient.Watermark("world", "later-session", tick, incarnation, generation));
        await Assert.ThrowsAsync<SessionDivergenceException>(() => CheckpointStore.VerifyFileAsync(Session, default));
    }

    [Fact]
    public async Task UnchangedFileAndFrozenEngineMustBothMatchTheSeal()
    {
        CheckpointSeal seal = await SealAsync();
        Assert.Equal(seal, await CheckpointStore.VerifyFileAsync(Session, default));
        Assert.Equal(Scope, CheckpointStore.VerifyLoaded(Observation(), Session, seal, null));
        Assert.Throws<SessionDivergenceException>(() => CheckpointStore.VerifyLoaded(Observation(tick: 101), Session, seal, null));
        Assert.Throws<SessionDivergenceException>(() => CheckpointStore.VerifyLoaded(Observation(prepared: false), Session, seal, null));
        Assert.Throws<SessionDivergenceException>(() => CheckpointStore.VerifyLoaded(Observation(checkpointId: "other"), Session, seal, null));
        Assert.Throws<SessionDivergenceException>(() => CheckpointStore.VerifyLoaded(Observation(scope: Scope with { ActorId = "other" }), Session, seal, null));
    }

    [Fact]
    public void LegacyCheckpointRequiresTheSameWorldSessionAndMonotonicHistory()
    {
        var watermark = new SessionGameClient.Watermark("world", "old-session", 100, 1, 7);
        Assert.Equal(Scope, CheckpointStore.VerifyLoaded(Observation(prepared: false), Session, null, watermark));
        Assert.Throws<SessionDivergenceException>(() => CheckpointStore.VerifyLoaded(Observation(tick: 99), Session, null, watermark));
        Assert.Throws<SessionDivergenceException>(() => CheckpointStore.VerifyLoaded(Observation(scope: Scope with { SessionId = "other" }), Session, null, watermark));
        Assert.Throws<SessionDivergenceException>(() => CheckpointStore.VerifyLoaded(Observation(), Session, null, watermark with { WorldId = "other" }));
    }

    [Fact]
    public void ActiveOperationInLegacySaveCannotBeReplayedAutomatically()
    {
        var observation = new GameResponse(1, "request", true, 100, Protocol.ToElement(new
        {
            scope = Scope, agent = new { awaitingController = false },
            operation = new { operationId = "active", kind = "move", status = "running", acceptedTick = 90,
                updatedTick = 100, effects = new { } }
        }));
        Assert.Throws<SessionDivergenceException>(() => CheckpointStore.VerifyLoaded(observation, Session, null, null));
    }

    [Fact]
    public async Task AtomicManifestReplacementRoundTripsWithoutLeavingAPendingFile()
    {
        await Session.WriteAsync();
        var resumed = Session with { SessionId = "new-session", ServerProcessId = 123 };
        await resumed.WriteAsync();
        Assert.Equal(resumed, await RuntimeSession.ReadAsync(Session.ManifestPath));
        Assert.False(File.Exists(Session.ManifestPath + ".pending"));
    }

    private async Task<CheckpointSeal> SealAsync()
    {
        await File.WriteAllTextAsync(CheckpointStore.SavePath(Session), "synthetic checkpoint bytes");
        var seal = new CheckpointSeal("checkpoint", Scope, 100, await CheckpointStore.HashAsync(CheckpointStore.SavePath(Session), default), DateTime.UtcNow);
        await LocalJson.WriteAsync(CheckpointStore.SealPath(Session), seal);
        return seal;
    }

    private static GameResponse Observation(long tick = 100, bool prepared = true, string checkpointId = "checkpoint", ActorScope? scope = null) =>
        new(1, "request", true, tick, JsonSerializer.SerializeToElement(new
        {
            scope = scope ?? Scope,
            agent = new { awaitingController = prepared, checkpointId }
        }, Protocol.Json));

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
