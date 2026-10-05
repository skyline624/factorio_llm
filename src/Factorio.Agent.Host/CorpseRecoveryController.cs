using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>A recovery outcome. RetryTick is when the first corpse left in a recent death zone becomes approachable.</summary>
public sealed record CorpseRecoveryResult(long Tick, string Outcome, IReadOnlyDictionary<string, long> Collected,
    IReadOnlyDictionary<string, long> Remaining, IReadOnlyList<string> CorpseIds, long? RetryTick = null);

public interface ICorpseRecovery
{
    Task<CorpseRecoveryResult> RunAsync(NativeDeathTransition death, ActorScope scope, CancellationToken token);
}

/// <summary>Moves only existing items from engine-proven corpses; routes and defense remain under the actor lease.</summary>
public sealed class CorpseRecoveryController(IGameClient game, IControllerJournal journal) : ICorpseRecovery
{
    /// <summary>The native observation maximum, two chunks: always inside the character's guaranteed 5x5-chunk sight.</summary>
    public const int InspectionRadius = 64;

    /// <summary>
    /// Tiles from known own industry beyond which a corpse waits: its death zone may have expired while the nests that killed
    /// the actor still stand. Base 2.0.77 corpses never expire (time_to_live 0), so waiting costs nothing. On 2026-10-01
    /// (seed 20261002) a recovery walked toward an old corpse far north-east, whose zone had expired, and the actor died at
    /// (96.6, -114.6).
    /// </summary>
    public const double IndustryReach = 48;
    /// <summary>Retry a body guarded by currently visible mobile enemies after one game minute.</summary>
    public const long VisibleThreatRetryTicks = 60 * 60;

    /// <summary>Own entity types that make a place worth defending: production, power, storage and turrets, not poles or corpses.</summary>
    private static readonly HashSet<string> IndustryTypes = new(StringComparer.Ordinal)
    {
        "assembling-machine", "furnace", "mining-drill", "lab", "boiler", "generator", "offshore-pump", "container",
        "inserter", "rocket-silo", "storage-tank", "ammo-turret", "radar"
    };

    public async Task<CorpseRecoveryResult> RunAsync(NativeDeathTransition death, ActorScope scope, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        token = deadline.Token;
        await using var controller = new SpatialController(game, journal);
        ProductionCatalog? catalog = null;
        var collected = new Dictionary<string, long>(StringComparer.Ordinal);
        var unavailable = new HashSet<(string Corpse, string Item)>();
        // Verdicts of this attempt only: a later attempt observes the zones again.
        var verdicts = new Dictionary<NativeDeathTransition, string>();
        IReadOnlyList<NativeDeathTransition> zones = [];
        long lastTick = death.DeathTick;
        for (int step = 0; step < 256; step++)
        {
            var response = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = InspectionRadius, limit = 200, entityLimit = ProductionController.MaximumOwnEntities }), token);
            if (!response.Ok) throw new GameRpcException(response.Error!);
            var data = response.Data;
            var actor = data.GetProperty("agent");
            var corpses = ReadObservedCorpses(response, death, scope, lastTick);
            var industry = Industry(data);
            lastTick = response.Tick;
            if (step == 0)
            {
                zones = game is IDangerZoneReader reader
                    ? await reader.ReadActiveDeathsAsync(scope, death.SurfaceIndex, response.Tick, token) : [];
                await journal.AppendAsync("corpse-recovery-start", new
                {
                    scope, response.Tick, death, actorMainInventory = actor.GetProperty("inventory"), corpseIds = corpses.Select(c => c.Id),
                    activeDeathZones = zones
                }, token);
            }
            var remaining = corpses.SelectMany(c => c.Items).GroupBy(p => p.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => checked(g.Sum(p => p.Value)), StringComparer.Ordinal);
            var position = actor.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
            // A corpse in a recent death zone, or behind one on the straight way, waits unless this attempt saw the zone free.
            NativeDeathTransition[] Blocking(Corpse corpse) => zones.Where(z => verdicts.GetValueOrDefault(z) != "clear"
                && (DangerZones.Covers(z, corpse.Position) || DangerZones.Crosses(z, position, corpse.Position))).ToArray();
            var holding = corpses.Where(c => c.Items.Values.Any(n => n > 0)).ToArray();
            foreach (var zone in holding.SelectMany(Blocking).Distinct().Where(z => !verdicts.ContainsKey(z)).ToArray())
                verdicts[zone] = await InspectAsync(zone, position, scope, lastTick, token);
            var blocked = holding.Where(c => Blocking(c).Length > 0).ToArray();
            var distant = holding.Except(blocked).Where(c => !NearIndustry(industry, c.Position)).ToArray();
            // Expiry removes a historical death warning; it does not remove enemies still observed around the body.
            var safety = SafetyObservation.Parse(response);
            var mobileIds = data.GetProperty("enemies") is { ValueKind: JsonValueKind.Array } enemies
                ? enemies.EnumerateArray().Where(e => e.GetProperty("type").GetString() is "unit" or "unit-spawner")
                    .Select(e => e.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal)
                : [];
            var mobile = safety.Enemies.Where(e => mobileIds.Contains(e.Id)).ToArray();
            var guarded = holding.Except(blocked).Except(distant)
                .Where(c => mobile.Any(e => e.Position.DistanceTo(c.Position) <= DangerZones.Radius)).ToArray();
            if (guarded.Length > 0)
                await journal.AppendAsync("corpse-recovery-visible-threats", new
                {
                    response.Tick, radius = DangerZones.Radius,
                    corpses = guarded.Select(c => new { c.Id, c.Position }),
                    visibleEnemies = mobile.Where(e => guarded.Any(c => e.Position.DistanceTo(c.Position) <= DangerZones.Radius))
                }, token);
            var selected = corpses.Except(blocked).Except(distant).Except(guarded).OrderBy(c => c.Position.DistanceTo(position)).ThenBy(c => c.Id, StringComparer.Ordinal)
                .SelectMany(c => c.Items.Where(p => p.Value > 0 && !unavailable.Contains((c.Id, p.Key)))
                    .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (Corpse: c, Item: p.Key, Count: p.Value)))
                .FirstOrDefault();
            if (selected.Corpse is null)
            {
                if (blocked.Length > 0) return await DeferBlockedAsync(response.Tick, blocked, Blocking, verdicts, collected, remaining, corpses, token);
                if (guarded.Length > 0)
                {
                    var deferred = new CorpseRecoveryResult(response.Tick, "unsafe-corpses-deferred", collected, remaining,
                        corpses.Select(c => c.Id).ToArray(), response.Tick + VisibleThreatRetryTicks);
                    await journal.AppendAsync("corpse-recovery-result", deferred, token);
                    return deferred;
                }
                if (distant.Length > 0)
                    await journal.AppendAsync("corpse-recovery-distance-deferral", new { response.Tick, reach = IndustryReach,
                        corpses = distant.Select(c => new { c.Id, c.Position, c.Items }) }, token);
                var result = new CorpseRecoveryResult(response.Tick,
                    distant.Length > 0 ? "distant-corpses-deferred"
                        : remaining.Values.Any(n => n > 0) ? "items-deferred" : corpses.Count == 0 ? "no-surviving-corpse" : "collected",
                    collected, remaining, corpses.Select(c => c.Id).ToArray());
                await journal.AppendAsync("corpse-recovery-result", result, token);
                return result;
            }
            // This base-game action accepts normal quality; never erase an unsupported-quality remainder.
            if (selected.Item.Contains('@')) { unavailable.Add((selected.Corpse.Id, selected.Item)); continue; }
            catalog ??= ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
            if (catalog.Scope != scope) throw new InvalidDataException("The recovery production catalog belongs to another actor scope.");
            double reach = actor.GetProperty("reachDistance").GetDouble();
            if (!double.IsFinite(reach) || reach < 1) throw new InvalidDataException("Invalid native corpse interaction reach.");
            await controller.TravelAsync(selected.Corpse.Position, Math.Min(10, reach - .5), catalog, token);
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

    /// <summary>Positions of known own industry in an observation; empty when the observation lists no own entity.</summary>
    internal static IReadOnlyList<MapPosition> Industry(JsonElement observation) =>
        observation.TryGetProperty("entities", out var entities) && entities.ValueKind == JsonValueKind.Array
            ? entities.EnumerateArray().Where(e => e.TryGetProperty("type", out var type) && IndustryTypes.Contains(type.GetString() ?? ""))
                .Select(e => e.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!).ToArray()
            : [];

    /// <summary>A corpse is approached only near known own industry; with no industry known yet every corpse is.</summary>
    internal static bool NearIndustry(IReadOnlyList<MapPosition> industry, MapPosition corpse) =>
        industry.Count == 0 || industry.Any(p => p.DistanceTo(corpse) <= IndustryReach);

    internal static async Task<CorpseRecoveryResult> DeferAsync(IGameClient game, IControllerJournal journal,
        NativeDeathTransition death, ActorScope scope, long earliestTick, CancellationToken token)
    {
        var response = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 32, limit = 200 }), token);
        var corpses = ReadObservedCorpses(response, death, scope, earliestTick);
        var remaining = corpses.SelectMany(c => c.Items).GroupBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => checked(g.Sum(p => p.Value)), StringComparer.Ordinal);
        var result = new CorpseRecoveryResult(response.Tick, "unsafe-corpses-deferred",
            new Dictionary<string, long>(), remaining, corpses.Select(c => c.Id).ToArray());
        await journal.AppendAsync("corpse-recovery-result", result, token);
        return result;
    }

    // One normal observation from where the actor stands; it never walks closer to look.
    private async Task<string> InspectAsync(NativeDeathTransition zone, MapPosition position, ActorScope scope, long earliestTick,
        CancellationToken token)
    {
        if (position.DistanceTo(zone.Position) + DangerZones.Radius > InspectionRadius)
        {
            await journal.AppendAsync("danger-zone-inspection", new
            {
                zone, verdict = "out-of-sight", observer = position, distance = position.DistanceTo(zone.Position), observed = false
            }, token);
            return "out-of-sight";
        }
        var response = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = InspectionRadius, limit = 200 }), token);
        var safety = SafetyObservation.Parse(response);
        if (safety.Scope != scope || safety.Tick < earliestTick || !safety.Alive || safety.Position is null)
            throw new InvalidDataException("Danger zone inspection lost its living actor or native scope.");
        var types = response.Data.GetProperty("enemies") is { ValueKind: JsonValueKind.Array } nodes
            ? nodes.EnumerateArray().ToDictionary(n => n.GetProperty("id").GetString()!, n => n.GetProperty("type").GetString() ?? "",
                StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        double radius = response.Data.GetProperty("coverage").GetProperty("radius").GetDouble();
        string verdict = DangerZones.Inspect(zone, safety.Position, radius, safety.LocalEnemiesComplete,
            safety.Enemies.Select(e => (types[e.Id], e.Position)));
        await journal.AppendAsync("danger-zone-inspection", new
        {
            zone, verdict, safety.Tick, observer = safety.Position, distance = safety.Position.DistanceTo(zone.Position), observed = true,
            observedRadius = radius, enemiesComplete = safety.LocalEnemiesComplete,
            visibleEnemiesInZone = safety.Enemies.Where(e => DangerZones.Covers(zone, e.Position)).Select(e => types[e.Id])
                .GroupBy(t => t, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal)
        }, token);
        return verdict;
    }

    // Nothing is approached: the retry tick is when the zones holding the first corpse expire. Character corpses of the base
    // game never expire (time_to_live 0); a finite native lifetime ending before that tick is journaled as an accepted loss.
    private async Task<CorpseRecoveryResult> DeferBlockedAsync(long tick, IReadOnlyList<Corpse> blocked,
        Func<Corpse, NativeDeathTransition[]> blocking, IReadOnlyDictionary<NativeDeathTransition, string> verdicts,
        IReadOnlyDictionary<string, long> collected, IReadOnlyDictionary<string, long> remaining, IReadOnlyList<Corpse> corpses,
        CancellationToken token)
    {
        var waits = blocked.Select(c => (Corpse: c, Zones: blocking(c), Free: blocking(c).Max(DangerZones.Expires))).ToArray();
        long retry = waits.Min(w => w.Free);
        await journal.AppendAsync("corpse-recovery-danger-deferral", new
        {
            tick, retryTick = retry, radius = DangerZones.Radius, lifetimeTicks = DangerZones.LifetimeTicks,
            corpses = waits.Select(w => new
            {
                w.Corpse.Id, w.Corpse.Position, w.Corpse.DeathTick, w.Corpse.TimeToLive, approachableTick = w.Free,
                corpseExpiresTick = w.Corpse.TimeToLive is > 0 ? w.Corpse.DeathTick + w.Corpse.TimeToLive : null,
                lostBeforeApproach = w.Corpse.TimeToLive is > 0 && w.Corpse.DeathTick + w.Corpse.TimeToLive <= w.Free
                    ? w.Corpse.Items.Where(p => p.Value > 0).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) : null,
                zones = w.Zones.Select(z => new { z.DeathTick, z.Position, verdict = verdicts[z], expiresTick = DangerZones.Expires(z) })
            })
        }, token);
        var result = new CorpseRecoveryResult(tick, "unsafe-corpses-deferred", collected, remaining, corpses.Select(c => c.Id).ToArray(), retry);
        await journal.AppendAsync("corpse-recovery-result", result, token);
        return result;
    }

    private static IReadOnlyList<Corpse> ReadObservedCorpses(GameResponse response, NativeDeathTransition death,
        ActorScope scope, long earliestTick)
    {
        if (!response.Ok) throw new GameRpcException(response.Error!);
        var data = response.Data;
        var actor = data.GetProperty("agent");
        if (response.Tick < earliestTick || data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json) != scope
            || !actor.GetProperty("alive").GetBoolean() || actor.GetProperty("controlMode").GetString() != "ai"
            || data.GetProperty("collectedTick").GetInt64() != response.Tick
            || scope.Incarnation != death.Incarnation + 1
            || !data.GetProperty("recovery").GetProperty("knownCorpsesComplete").GetBoolean())
            throw new InvalidDataException("Corpse recovery lost its living actor, native scope or complete provenance observation.");
        return ReadCorpses(data.GetProperty("recovery").GetProperty("corpses"), death, response.Tick);
    }

    /// <summary><paramref name="TimeToLive"/> is the native prototype lifetime in ticks, 0 for never; absent from older mods.</summary>
    private sealed record Corpse(string Id, MapPosition Position, IReadOnlyDictionary<string, long> Items, long DeathTick, long? TimeToLive);

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
            long? lifetime = node.TryGetProperty("timeToLive", out var ttl) ? ttl.GetInt64() : null;
            if (incarnation < 1 || incarnation > death.Incarnation || deathTick < 0 || deathTick > tick || unit < 1 || lifetime < 0
                || !id.StartsWith($"corpse:{unit}:{deathTick}:", StringComparison.Ordinal)
                || result.Any(c => c.Id == id)) throw new InvalidDataException("Invalid native corpse provenance.");
            if (node.GetProperty("surfaceIndex").GetInt32() != death.SurfaceIndex)
                throw new InvalidDataException("Recovery across surfaces requires an explicit route.");
            var items = node.GetProperty("inventories").GetProperty("corpse").GetProperty("items")
                .Deserialize<Dictionary<string, long>>(Protocol.Json)!;
            if (items.Values.Any(n => n < 0)) throw new InvalidDataException("Negative native corpse stock.");
            result.Add(new(id, node.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!, items, deathTick, lifetime));
        }
        return result;
    }
}
