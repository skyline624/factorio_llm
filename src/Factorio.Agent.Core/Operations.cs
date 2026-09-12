using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Factorio.Agent.Core;

public sealed record ActorScope(string WorldId, string SessionId, string ActorId, long Incarnation, long Generation);
public sealed record MapPosition(double X, double Y)
{
    public double DistanceTo(MapPosition other) => Math.Sqrt(Math.Pow(X - other.X, 2) + Math.Pow(Y - other.Y, 2));
}

/// <summary>An immutable submission is retained verbatim when its outcome is unknown.</summary>
public sealed record OperationSubmission(string OperationId, string Fingerprint, ActorScope Scope,
    string Kind, JsonElement Args, JsonElement Preconditions, long DeadlineTick)
{
    public static OperationSubmission Create(ActorScope scope, string kind, object arguments,
        long deadlineTick, object? preconditions = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (deadlineTick <= 0) throw new ArgumentOutOfRangeException(nameof(deadlineTick));
        JsonElement args = Protocol.ToElement(arguments);
        JsonElement conditions = Protocol.ToElement(preconditions ?? new { });
        string id = Guid.NewGuid().ToString("N");
        string canonical = JsonSerializer.Serialize(new { scope, kind, args, preconditions = conditions, deadlineTick }, Protocol.Json);
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new(id, fingerprint, scope, kind, args, conditions, deadlineTick);
    }
}

public sealed record OperationReceipt(string OperationId, string Kind, string Status, long? AcceptedTick,
    long UpdatedTick, JsonElement Effects, GameError? Error, JsonElement Evidence)
{
    private static readonly HashSet<string> Statuses = ["rejected", "accepted", "running", "completed", "partial", "failed", "cancelled"];
    public bool IsTerminal => Status is "rejected" or "completed" or "partial" or "failed" or "cancelled";

    public static OperationReceipt Parse(JsonElement data, string expectedId)
    {
        string id = data.GetProperty("operationId").GetString() ?? throw new InvalidDataException("Missing operation id.");
        if (id != expectedId) throw new InvalidDataException("Operation correlation mismatch.");
        string kind = data.GetProperty("kind").GetString() ?? throw new InvalidDataException("Missing operation kind.");
        string status = data.GetProperty("status").GetString() ?? "";
        if (!Statuses.Contains(status)) throw new InvalidDataException("Unknown operation status.");
        long? accepted = data.TryGetProperty("acceptedTick", out JsonElement tick) && tick.ValueKind == JsonValueKind.Number ? tick.GetInt64() : null;
        JsonElement effects = data.GetProperty("effects").Clone();
        if (effects.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) throw new InvalidDataException("Missing operation effects.");
        GameError? error = data.TryGetProperty("error", out JsonElement errorNode) && errorNode.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<GameError>(errorNode, Protocol.Json) : null;
        return new(id, kind, status, accepted, data.GetProperty("updatedTick").GetInt64(), effects, error, data.Clone());
    }
}

public sealed class GameRpcException(GameError error) : Exception($"{error.Code}: {error.Message}")
{
    public GameError Error { get; } = error;
}

/// <summary>The game may have acted. Query this operation; never manufacture a replacement id.</summary>
public sealed class OperationOutcomeUnknownException(string operationId, Exception cause)
    : Exception($"Outcome of operation {operationId} is unknown. Query its receipt and reconcile the world.", cause)
{
    public string OperationId { get; } = operationId;
}
