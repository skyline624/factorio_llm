using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record CorpseRecoveryResult(long Tick, string Outcome, IReadOnlyDictionary<string, long> Collected,
    IReadOnlyDictionary<string, long> Remaining, IReadOnlyList<string> CorpseIds);

public interface ICorpseRecovery
{
    Task<CorpseRecoveryResult> RunAsync(NativeDeathTransition death, ActorScope scope, CancellationToken token);
}

/// <summary>Moves only existing items from engine-proven corpses; routes and defense remain under the actor lease.</summary>
public sealed class CorpseRecoveryController(IGameClient game, IControllerJournal journal) : ICorpseRecovery
{
    public async Task<CorpseRecoveryResult> RunAsync(NativeDeathTransition death, ActorScope scope, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        token = deadline.Token;
        await using var controller = new SpatialController(game, journal);
        ProductionCatalog? catalog = null;
        var collected = new Dictionary<string, long>(StringComparer.Ordinal);
        var unavailable = new HashSet<(string Corpse, string Item)>();
        long lastTick = death.DeathTick;
        for (int step = 0; step < 256; step++)
        {
            var response = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 32, limit = 200 }), token);
            if (!response.Ok) throw new GameRpcException(response.Error!);
            var data = response.Data;
            var actor = data.GetProperty("agent");
            if (response.Tick < lastTick || data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json) != scope
                || !actor.GetProperty("alive").GetBoolean() || actor.GetProperty("controlMode").GetString() != "ai"
                || data.GetProperty("collectedTick").GetInt64() != response.Tick
                || scope.Incarnation != death.Incarnation + 1
                || !data.GetProperty("recovery").GetProperty("knownCorpsesComplete").GetBoolean())
                throw new InvalidDataException("Corpse recovery lost its living actor, native scope or complete provenance observation.");
            lastTick = response.Tick;
            var corpses = ReadCorpses(data.GetProperty("recovery").GetProperty("corpses"), death, response.Tick);
            if (step == 0) await journal.AppendAsync("corpse-recovery-start", new
                { scope, response.Tick, death, actorMainInventory = actor.GetProperty("inventory"), corpseIds = corpses.Select(c => c.Id) }, token);
            var remaining = corpses.SelectMany(c => c.Items).GroupBy(p => p.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => checked(g.Sum(p => p.Value)), StringComparer.Ordinal);
            var position = actor.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
            var selected = corpses.OrderBy(c => c.Position.DistanceTo(position)).ThenBy(c => c.Id, StringComparer.Ordinal)
                .SelectMany(c => c.Items.Where(p => p.Value > 0 && !unavailable.Contains((c.Id, p.Key)))
                    .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (Corpse: c, Item: p.Key, Count: p.Value)))
                .FirstOrDefault();
            if (selected.Corpse is null)
            {
                var result = new CorpseRecoveryResult(response.Tick,
                    remaining.Values.Any(n => n > 0) ? "items-deferred" : corpses.Count == 0 ? "no-surviving-corpse" : "collected",
                    collected, remaining, corpses.Select(c => c.Id).ToArray());
                await journal.AppendAsync("corpse-recovery-result", result, token);
                return result;
            }
            // This base-game action accepts normal quality; never erase an unsupported-quality remainder.
            if (selected.Item.Contains('@')) { unavailable.Add((selected.Corpse.Id, selected.Item)); continue; }
            catalog ??= ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
            if (catalog.Scope != scope) throw new InvalidDataException("The recovery production catalog belongs to another actor scope.");
            await controller.TravelAsync(selected.Corpse.Position, 1, catalog, token);
            var stock = await new FactorySnapshotClient(game).CaptureAsync([selected.Item], cancellationToken: token);
            if (stock.Scope != scope || stock.CollectedTick < lastTick) throw new InvalidDataException("Actor or tick changed while checking corpse recovery capacity.");
            lastTick = stock.CollectedTick;
            var actorEntity = stock.Records.Single(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "actor");
            string mainId = actorEntity.Data.GetProperty("mainInventoryId").GetString()!;
            var main = stock.Records.Single(r => r.Kind == "inventory" && r.Id == mainId && r.EntityId == actorEntity.EntityId);
            long capacity = main.Data.GetProperty("capacityHints").GetProperty(selected.Item).GetProperty("insertable").GetInt64();
            if (capacity < 0) throw new InvalidDataException("Invalid native recovery capacity.");
            var corpseInventory = stock.Records.SingleOrDefault(r => r.Kind == "inventory" && r.EntityId == selected.Corpse.Id);
            if (corpseInventory is null) continue; // Native destruction is observed again; no replacement body is selected by position.
            long available = corpseInventory.Data.GetProperty("items").TryGetProperty(selected.Item, out var amount) ? amount.GetInt64() : 0;
            if (available <= 0) continue;
            if (capacity == 0) { unavailable.Add((selected.Corpse.Id, selected.Item)); continue; }
            int count = checked((int)Math.Min(1000, Math.Min(available, capacity)));
            var receipt = await controller.WorkAsync("take", new { entityId = selected.Corpse.Id, inventory = "corpse", item = selected.Item, count },
                600, token: token);
            if (receipt.Status is not ("completed" or "partial")
                || receipt.Effects.GetProperty("targetId").GetString() != selected.Corpse.Id
                || receipt.Effects.GetProperty("item").GetString() != selected.Item)
                throw new InvalidDataException("Corpse transfer lacks a matching completed native effect.");
            long moved = receipt.Effects.GetProperty("transferred").GetInt64();
            if (moved < 1 || moved > count) throw new InvalidDataException("Invalid native corpse transfer quantity.");
            collected[selected.Item] = checked(collected.GetValueOrDefault(selected.Item) + moved);
        }
        throw new TimeoutException("Corpse recovery exhausted its transfer budget; partial effects remain journaled.");
    }

    private sealed record Corpse(string Id, MapPosition Position, IReadOnlyDictionary<string, long> Items);

    private static IReadOnlyList<Corpse> ReadCorpses(JsonElement value, NativeDeathTransition death, long tick)
    {
        if (value.ValueKind == JsonValueKind.Object && !value.EnumerateObject().Any()) return [];
        var result = new List<Corpse>();
        foreach (var node in value.EnumerateArray())
        {
            string id = node.GetProperty("id").GetString()!;
            long incarnation = node.GetProperty("incarnation").GetInt64();
            long deathTick = node.GetProperty("deathTick").GetInt64();
            long unit = node.GetProperty("actorUnitNumber").GetInt64();
            if (incarnation < 1 || incarnation > death.Incarnation || deathTick < 0 || deathTick > tick || unit < 1
                || !id.StartsWith($"corpse:{unit}:{deathTick}:", StringComparison.Ordinal)
                || result.Any(c => c.Id == id)) throw new InvalidDataException("Invalid native corpse provenance.");
            if (node.GetProperty("surfaceIndex").GetInt32() != death.SurfaceIndex)
                throw new InvalidDataException("Recovery across surfaces requires an explicit route.");
            var items = node.GetProperty("inventories").GetProperty("corpse").GetProperty("items")
                .Deserialize<Dictionary<string, long>>(Protocol.Json)!;
            if (items.Values.Any(n => n < 0)) throw new InvalidDataException("Negative native corpse stock.");
            result.Add(new(id, node.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!, items));
        }
        return result;
    }
}
