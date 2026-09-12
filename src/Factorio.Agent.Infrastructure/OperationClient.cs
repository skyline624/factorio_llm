using Factorio.Agent.Core;
using System.Net.Sockets;
using System.Text.Json;

namespace Factorio.Agent.Infrastructure;

public sealed class OperationClient(IGameClient game, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<OperationReceipt> SubmitAsync(OperationSubmission submission, CancellationToken cancellationToken = default)
    {
        try
        {
            return await CallAsync("submit", submission, submission.OperationId, cancellationToken);
        }
        catch (Exception error) when (MayHaveActed(error))
        {
            throw new OperationOutcomeUnknownException(submission.OperationId, error);
        }
    }

    public Task<OperationReceipt> QueryAsync(string operationId, CancellationToken cancellationToken = default) =>
        CallAsync("operation", new { operationId }, operationId, cancellationToken);

    public async Task<OperationReceipt> CancelAsync(string operationId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await CallAsync("cancel", new { operationId }, operationId, cancellationToken);
        }
        catch (Exception error) when (MayHaveActed(error))
        {
            throw new OperationOutcomeUnknownException(operationId, error);
        }
    }

    public async Task<OperationReceipt> WaitAsync(string operationId, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                OperationReceipt receipt = await QueryAsync(operationId, deadline.Token);
                if (receipt.IsTerminal) return receipt;
                await Task.Delay(TimeSpan.FromMilliseconds(100), clock, deadline.Token);
            }
        }
        catch (Exception error) when (MayHaveActed(error))
        {
            throw new OperationOutcomeUnknownException(operationId, error);
        }
    }

    private async Task<OperationReceipt> CallAsync(string action, object args, string operationId, CancellationToken token)
    {
        GameResponse response = await game.ExecuteAsync(GameRequest.Create(action, args), token);
        if (!response.Ok) throw new GameRpcException(response.Error!);
        try
        {
            return OperationReceipt.Parse(response.Data, operationId);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Invalid operation receipt; the mutation outcome is not established.", error);
        }
    }

    private static bool MayHaveActed(Exception error) =>
        error is IOException or InvalidDataException or SocketException or TimeoutException or OperationCanceledException
        or GameRpcException { Error.Code: "engine_error" or "serialization_error" };
}
