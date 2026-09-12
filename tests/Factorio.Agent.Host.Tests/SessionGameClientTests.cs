using Factorio.Agent.Core;
using Factorio.Agent.Host;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class SessionGameClientTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "factorio-session-test-" + Guid.NewGuid().ToString("N"));
    public SessionGameClientTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task ReloadingOldSaveIsDetectedBeforeSubmittingMutation()
    {
        var session = Session();
        var game = new FakeGame { Tick = 1000 };
        await new SessionGameClient(session, game).ExecuteAsync(GameRequest.Create("observe"));
        game.Tick = 500;
        var restartedClient = new SessionGameClient(session, game);
        await Assert.ThrowsAsync<SessionDivergenceException>(() => restartedClient.ExecuteAsync(GameRequest.Create("submit", new { })));
        Assert.Equal(["observe", "observe"], game.Calls);
    }

    [Fact]
    public async Task NewerObservationAllowsOneSubmissionAndPersistsHighWatermark()
    {
        var game = new FakeGame { Tick = 10 };
        var client = new SessionGameClient(Session(), game);
        await client.ExecuteAsync(GameRequest.Create("observe"));
        game.Tick = 20;
        await client.ExecuteAsync(GameRequest.Create("submit"));
        Assert.Equal(["observe", "observe", "submit"], game.Calls);
        Assert.Contains("\"tick\":20", await File.ReadAllTextAsync(Path.Combine(directory, "observation-watermark.json")));
    }

    [Fact]
    public async Task ANewWorldCannotReuseTheOldManifest()
    {
        var game = new FakeGame { Tick = 10, World = "different-world" };
        await Assert.ThrowsAsync<SessionDivergenceException>(() => new SessionGameClient(Session(), game).ExecuteAsync(GameRequest.Create("observe")));
    }

    private RuntimeSession Session() => new(directory, "factorio.exe", "config.ini", "mods", "save.zip", 0, 1, 2, "fixture-only", "session", "world", 1, true);
    public void Dispose() => Directory.Delete(directory, recursive: true);

    private sealed class FakeGame : IGameClient
    {
        public long Tick { get; set; }
        public string World { get; set; } = "world";
        public List<string> Calls { get; } = [];
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request.Action);
            return Task.FromResult(new GameResponse(1, request.RequestId, true, Tick,
                Protocol.ToElement(new { scope = new ActorScope(World, "session", "actor", 1, 1) })));
        }
    }
}
