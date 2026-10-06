using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class StaleDeadlineTests
{
    [Fact]
    public async Task AWorkSubmissionRejectedForAStaleDeadlineIsPhotographedAndSubmittedAgain()
    {
        var game = new Game(rejections: 1);
        await using var controller = new SpatialController(game, new Journal());
        var receipt = await controller.WorkAsync("wait", new { ticks = 60 }, 600);
        Assert.Equal("completed", receipt.Status);
        Assert.Equal(2, game.Submissions.Count);
        Assert.NotEqual(game.Submissions[0].OperationId, game.Submissions[1].OperationId);
    }

    [Fact]
    public async Task RepeatedStaleDeadlinesEndWithTheRejection()
    {
        var game = new Game(rejections: 10);
        await using var controller = new SpatialController(game, new Journal());
        var receipt = await controller.WorkAsync("wait", new { ticks = 60 }, 600);
        Assert.Equal("rejected", receipt.Status);
        Assert.Equal(4, game.Submissions.Count);
    }

    [Fact]
    public async Task AnExhaustedPlanningBudgetIsATimeoutOrNoPlan()
    {
        var game = new Game(rejections: 0);
        await using var controller = new SpatialController(game, new Journal());
        static string? Endless(CancellationToken token)
        {
            while (true) token.ThrowIfCancellationRequested();
        }
        await Assert.ThrowsAsync<PlanningBudgetExceededException>(() =>
            ControllerPlanning.RunAsync(Endless, controller, TimeSpan.FromMilliseconds(50), CancellationToken.None));
        Assert.Null(await ControllerPlanning.TryRunAsync(Endless, controller, TimeSpan.FromMilliseconds(50), CancellationToken.None));
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ControllerPlanning.TryRunAsync(Endless, controller, TimeSpan.FromMinutes(1), cancelled.Token));
    }

    [Fact]
    public void OnlyTheDeadlineBoundRejectionCounts()
    {
        Assert.True(SpatialController.StaleDeadline(Receipt("rejected", "invalid_arguments", "deadlineTick is out of bounds")));
        Assert.False(SpatialController.StaleDeadline(Receipt("rejected", "invalid_arguments", "ticks is out of bounds")));
        Assert.False(SpatialController.StaleDeadline(Receipt("failed", "invalid_arguments", "deadlineTick is out of bounds")));
    }

    private static OperationReceipt Receipt(string status, string code, string message) => OperationReceipt.Parse(Protocol.ToElement(new
    {
        operationId = "op", kind = "wait", status, acceptedTick = 10, updatedTick = 10, effects = new { },
        error = new { code, message }
    }), "op");

    private sealed class Journal : IControllerJournal
    {
        public Task AppendAsync(string type, object data, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class Game(int rejections) : IGameClient
    {
        public List<OperationSubmission> Submissions { get; } = [];
        private long tick = 100;
        private OperationReceipt? receipt;
        private SpatialSnapshot Map => SpatialPlannerTests.Map([]) with { CollectedTick = tick };

        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            tick++;
            object data;
            switch (request.Action)
            {
                case "spatial": data = Map; break;
                case "observe":
                    var view = new Dictionary<string, object>
                    {
                        ["scope"] = Map.Scope, ["collectedTick"] = tick,
                        ["coverage"] = new { atomic = true, collectionStartTick = tick, collectionEndTick = tick,
                            enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility", enemiesTruncated = false },
                        ["agent"] = new { alive = true, controlMode = "ai", stopUnconfirmed = false, position = Map.Actor.Position,
                            health = 250, weapon = new { ready = true, rounds = 100, range = 15 } },
                        ["enemies"] = Array.Empty<object>()
                    };
                    if (receipt is not null) view["operation"] = receipt.Evidence;
                    data = view;
                    break;
                case "submit":
                    var submission = request.Arguments.Deserialize<OperationSubmission>(Protocol.Json)!;
                    Submissions.Add(submission);
                    bool stale = Submissions.Count <= rejections;
                    var raw = Protocol.ToElement(new
                    {
                        operationId = submission.OperationId, kind = submission.Kind, status = stale ? "rejected" : "completed",
                        acceptedTick = tick, updatedTick = tick, effects = new { },
                        error = stale ? new { code = "invalid_arguments", message = "deadlineTick is out of bounds" } : null
                    });
                    receipt = OperationReceipt.Parse(raw, submission.OperationId);
                    data = raw;
                    break;
                case "operation":
                    data = receipt!.Evidence;
                    break;
                default: throw new InvalidOperationException(request.Action);
            }
            return Task.FromResult(new GameResponse(1, request.RequestId, true, tick, Protocol.ToElement(data)));
        }
    }
}
