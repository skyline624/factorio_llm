using System.Text.Json;
using System.Text.Json.Serialization;
using Factorio.Agent.Core;

namespace Factorio.Agent.Infrastructure;

public sealed class SpatialClient(IGameClient game)
{
    public async Task<SpatialSnapshot> CaptureAsync(IReadOnlyList<string>? items = null, int radius = 32,
        CancellationToken cancellationToken = default)
    {
        if (radius is < 4 or > 48) throw new ArgumentOutOfRangeException(nameof(radius));
        return SpatialSnapshot.Parse(await game.ExecuteAsync(GameRequest.Create("spatial", new { radius, items = items ?? [] }), cancellationToken));
    }

    public async Task<PlacementValidation> ValidateAsync(ActorScope scope, string item, IReadOnlyList<PlacementCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        GameResponse response = await game.ExecuteAsync(GameRequest.Create("validate_placement", new
        {
            scope, item, candidates = candidates.Select(c => new { c.Position, c.Direction }).ToArray()
        }), cancellationToken);
        if (!response.Ok) throw new GameRpcException(response.Error!);
        PlacementValidation validation = response.Data.Deserialize<PlacementValidation>(Protocol.Json)
            ?? throw new InvalidDataException("Missing native placement validation.");
        if (validation.Scope != scope || validation.CollectedTick != response.Tick || validation.Item != item
            || validation.Candidates.Count != candidates.Count)
            throw new InvalidDataException("Placement validation scope or correlation mismatch.");
        for (int index = 0; index < candidates.Count; index++)
            if (validation.Candidates[index].Index != index + 1 || validation.Candidates[index].Position != candidates[index].Position
                || validation.Candidates[index].Direction != candidates[index].Direction)
                throw new InvalidDataException("Placement candidate correlation mismatch.");
        return validation;
    }
}

public sealed record ValidatedPlacement(int Index, MapPosition Position, int Direction, bool Allowed, bool InReach);
public sealed record PlacementValidation(ActorScope Scope, long CollectedTick, string Item,
    [property: JsonConverter(typeof(NativeArrayConverter<ValidatedPlacement>))] IReadOnlyList<ValidatedPlacement> Candidates);
