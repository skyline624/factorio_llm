using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;
using Xunit;

namespace Factorio.Agent.Infrastructure.Tests;

public sealed class OperationClientTests
{
    [Fact]
    public async Task LostSubmissionResponseIsNotRetriedAndCanBeReconciledById()
    {
        var game = new RecordingGame();
        var client = new OperationClient(game);
        var submission = OperationSubmission.Create(new("world", "session", "actor", 1, 1), "craft", new { recipe = "iron-gear-wheel", count = 1 }, 500);
        game.Handler = _ => throw new TimeoutException("Reply was lost after mutation.");
        var error = await Assert.ThrowsAsync<OperationOutcomeUnknownException>(() => client.SubmitAsync(submission));
        Assert.Equal(submission.OperationId, error.OperationId);
        Assert.Single(game.Calls);
        game.Handler = request => new(1, request.RequestId, true, 123, Protocol.ToElement(new
        {
            operationId = submission.OperationId, kind = "craft", status = "completed", acceptedTick = 100,
            updatedTick = 123, effects = new { produced = 1 }
        }));
        OperationReceipt receipt = await client.QueryAsync(error.OperationId);
        Assert.Equal("completed", receipt.Status);
        Assert.Equal(["submit", "operation"], game.Calls.Select(c => c.Action));
    }

    [Fact]
    public async Task CancellingNetworkWaitDoesNotPretendToCancelGameOperation()
    {
        var game = new RecordingGame();
        var client = new OperationClient(game);
        game.Handler = _ => throw new OperationCanceledException();
        await Assert.ThrowsAsync<OperationOutcomeUnknownException>(() => client.WaitAsync("known-id", TimeSpan.FromSeconds(1)));
        Assert.Single(game.Calls);
        Assert.Equal("operation", game.Calls[0].Action);
    }

    [Fact]
    public async Task MalformedReceiptAfterSubmissionLeavesOutcomeUnknown()
    {
        var game = new RecordingGame
        {
            Handler = request => new(1, request.RequestId, true, 4, Protocol.ToElement(new { missing = "receipt" }))
        };
        var submission = OperationSubmission.Create(new("world", "session", "actor", 1, 1), "craft", new { count = 1 }, 500);
        await Assert.ThrowsAsync<OperationOutcomeUnknownException>(() => new OperationClient(game).SubmitAsync(submission));
        Assert.Single(game.Calls);
    }

    [Fact]
    public void AcknowledgementIsNotCompletion()
    {
        var data = Protocol.ToElement(new { operationId = "id", kind = "craft", status = "accepted", acceptedTick = 4, updatedTick = 4, effects = new { } });
        Assert.False(OperationReceipt.Parse(data, "id").IsTerminal);
        Assert.Throws<InvalidDataException>(() => OperationReceipt.Parse(data, "another-id"));
    }

    [Theory]
    [InlineData("engine_error")]
    [InlineData("serialization_error")]
    public async Task FailureAfterHandlerExecutionDoesNotProveNoMutation(string code)
    {
        var game = new RecordingGame
        {
            Handler = request => new(1, request.RequestId, false, 4, Protocol.ToElement(new { }), new(code, "The handler ran before the response failed."))
        };
        var submission = OperationSubmission.Create(new("world", "session", "actor", 1, 1), "craft", new { count = 1 }, 500);
        var error = await Assert.ThrowsAsync<OperationOutcomeUnknownException>(() => new OperationClient(game).SubmitAsync(submission));
        Assert.Equal(submission.OperationId, error.OperationId);
        Assert.Single(game.Calls);
    }

    private sealed class RecordingGame : IGameClient
    {
        public Func<GameRequest, GameResponse> Handler { get; set; } = _ => throw new InvalidOperationException();
        public List<GameRequest> Calls { get; } = [];
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request);
            return Task.FromResult(Handler(request));
        }
    }
}
