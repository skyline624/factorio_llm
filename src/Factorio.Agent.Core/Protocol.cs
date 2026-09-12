using System.Text.Json;
using System.Text.Json.Serialization;

namespace Factorio.Agent.Core;

public static class Protocol
{
    public const int Version = 1;
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    public static readonly IReadOnlySet<string> Actions = new HashSet<string>(StringComparer.Ordinal)
    {
        "hello", "observe", "factory_snapshot", "spatial", "validate_placement",
        "submit", "operation", "cancel", "recipes", "technologies", "production_catalog", "mark_fixture", "prepare_checkpoint"
    };

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Json);
}

public sealed record GameRequest(int ProtocolVersion, string RequestId, string Action, JsonElement Arguments)
{
    public static GameRequest Create(string action, object? arguments = null) =>
        new(Protocol.Version, Guid.NewGuid().ToString("N"), action, Protocol.ToElement(arguments ?? new { }));
}

public sealed record GameError(string Code, string Message);

public sealed record GameResponse(int ProtocolVersion, string RequestId, bool Ok, long Tick,
    JsonElement Data, GameError? Error = null);

public interface IGameClient
{
    Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default);
}
