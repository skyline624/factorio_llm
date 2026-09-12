using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Infrastructure;

public sealed class FactorioGameClient(RconClient rcon) : IGameClient
{
    public async Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
    {
        string command = BuildCommand(request);
        string json = await rcon.ExecuteAsync(command, cancellationToken);
        GameResponse response;
        try
        {
            response = JsonSerializer.Deserialize<GameResponse>(json, Protocol.Json)
                ?? throw new InvalidDataException("The Factorio mod returned no response.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Invalid mod response. Check the server log and installed mod version.", error);
        }
        if (response.ProtocolVersion != Protocol.Version || response.RequestId != request.RequestId)
            throw new InvalidDataException("Mod response protocol or request correlation mismatch.");
        if (!response.Ok && response.Error is null)
            throw new InvalidDataException("Failed mod response is missing the error details.");
        if (response.Ok && response.Error is not null)
            throw new InvalidDataException("Successful mod response contains an error.");
        return response;
    }

    public static string BuildCommand(GameRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProtocolVersion != Protocol.Version) throw new ArgumentException("Unsupported protocol version.");
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestId);
        if (request.RequestId.Length > 128) throw new ArgumentException("Request ID is too long.");
        if (!Protocol.Actions.Contains(request.Action)) throw new ArgumentException("Action is not allowed.");
        if (request.Arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("Arguments must be an object.");
        string json = JsonSerializer.Serialize(request, Protocol.Json);
        string equals = "=";
        while (json.Contains("]" + equals + "]", StringComparison.Ordinal)) equals += "=";
        return "/silent-command rcon.print(remote.call(\"factorio_agent\",\"execute\",[" + equals + "[" + json + "]" + equals + "]))";
    }
}
