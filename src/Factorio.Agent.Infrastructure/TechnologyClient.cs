using System.Text.Json;
using System.Text.Json.Serialization;
using Factorio.Agent.Core;

namespace Factorio.Agent.Infrastructure;

public sealed record TechnologyObservation(ActorScope Scope, long StartTick, long EndTick,
    IReadOnlyDictionary<string, NativeTechnology> Technologies);

/// <summary>Reads the requested dependency closure, preserving the collection interval and checking actor identity.</summary>
public sealed class TechnologyClient(IGameClient game)
{
    private sealed record Page(
        [property: JsonConverter(typeof(NativeArrayConverter<NativeTechnology>))] IReadOnlyList<NativeTechnology> Items,
        int Total, int Offset, int Limit, long CollectedTick, bool Complete);

    public async Task<TechnologyObservation> ReadDependenciesAsync(string target, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        GameResponse before = await Observe();
        ActorScope scope = Scope(before);
        long lastTick = before.Tick;
        var values = new Dictionary<string, NativeTechnology>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        pending.Enqueue(target);
        while (pending.TryDequeue(out string? name))
        {
            if (values.ContainsKey(name)) continue;
            if (values.Count >= 256) throw new InvalidDataException("Technology dependency observation exceeded its budget.");
            GameResponse response = await game.ExecuteAsync(GameRequest.Create("technologies", new { name, limit = 1 }), token);
            if (!response.Ok) throw new GameRpcException(response.Error!);
            Page page = response.Data.Deserialize<Page>(Protocol.Json) ?? throw new InvalidDataException("Missing technology page.");
            if (page.CollectedTick != response.Tick || response.Tick < lastTick || page.Offset != 0
                || !page.Complete || page.Total != 1 || page.Items.Count != 1 || page.Items[0].Name != name)
                throw new InvalidDataException("Missing, incomplete or mismatched native technology observation.");
            lastTick = response.Tick;
            NativeTechnology technology = page.Items[0];
            values.Add(name, technology);
            if (!technology.Researched)
                foreach (string prerequisite in technology.Prerequisites) pending.Enqueue(prerequisite);
        }
        GameResponse after = await Observe();
        if (Scope(after) != scope || after.Tick < lastTick)
            throw new InvalidDataException("Actor identity or native time changed during technology collection.");
        return new(scope, before.Tick, after.Tick, values);

        async Task<GameResponse> Observe()
        {
            GameResponse response = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 1, limit = 1 }), token);
            if (!response.Ok) throw new GameRpcException(response.Error!);
            return response;
        }

        static ActorScope Scope(GameResponse response) => response.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)
            ?? throw new InvalidDataException("Missing actor identity for technology collection.");
    }
}
