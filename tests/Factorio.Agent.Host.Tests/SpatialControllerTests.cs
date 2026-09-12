using Factorio.Agent.Core;
using Factorio.Agent.Host;
using System.Text.Json;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class SpatialControllerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Interrupted_move_is_stopped_by_identity_even_when_mutation_responses_are_lost(
        bool loseSubmission, bool loseCancellation)
    {
        using var interruption = new CancellationTokenSource();
        var game = new InterruptingGame(interruption, loseSubmission, loseCancellation);
        var journal = new Journal();
        await using (var controller = new SpatialController(game, journal))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                controller.NavigateAsync(new(5, 0), cancellationToken: interruption.Token));
        }
        Assert.Equal("cancelled", game.Status);
        Assert.Single(game.Calls, action => action == "submit");
        Assert.Single(game.Calls, action => action == "cancel");
        Assert.Contains("operation", game.Calls);
        Assert.Equal(game.OperationId, game.CancelledId);
        Assert.Contains("final-receipt", journal.Types);
    }

    private sealed class Journal : IControllerJournal
    {
        public List<string> Types { get; } = [];
        public Task AppendAsync(string type, object data, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Types.Add(type);
            return Task.CompletedTask;
        }
    }

    private sealed class InterruptingGame(CancellationTokenSource interruption, bool loseSubmit, bool loseCancel) : IGameClient
    {
        public List<string> Calls { get; } = [];
        public string? OperationId { get; private set; }
        public string? CancelledId { get; private set; }
        public string Status { get; private set; } = "running";

        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(request.Action);
            SpatialSnapshot map = SpatialPlannerTests.Map([]);
            object data;
            switch (request.Action)
            {
                case "observe":
                    data = new
                    {
                        map.Scope, collectedTick = 100,
                        coverage = new { atomic = true, collectionStartTick = 100, collectionEndTick = 100,
                            enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility" },
                        agent = new { alive = true, controlMode = "ai", stopUnconfirmed = false, position = map.Actor.Position,
                            health = 250, weapon = new { ready = false, rounds = 0, range = 0 } },
                        enemies = Array.Empty<object>()
                    };
                    break;
                case "spatial": data = map; break;
                case "submit":
                    OperationId = request.Arguments.Deserialize<OperationSubmission>(Protocol.Json)!.OperationId;
                    interruption.Cancel();
                    if (loseSubmit) throw new IOException("Accepted move, response lost.");
                    data = Receipt();
                    break;
                case "operation":
                    Assert.Equal(OperationId, request.Arguments.GetProperty("operationId").GetString());
                    data = Receipt();
                    break;
                case "cancel":
                    CancelledId = request.Arguments.GetProperty("operationId").GetString();
                    Status = "cancelled";
                    if (loseCancel) throw new IOException("Stopped move, response lost.");
                    data = Receipt();
                    break;
                default: throw new InvalidOperationException(request.Action);
            }
            return Task.FromResult(new GameResponse(1, request.RequestId, true, 100, Protocol.ToElement(data)));
        }

        private object Receipt() => new { operationId = OperationId, kind = "move", status = Status,
            acceptedTick = 100, updatedTick = 100, effects = new { } };
    }
}
