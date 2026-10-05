using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Reconnects standing fluid consumers, including adopted extractors whose poles are recorded as links.</summary>
public sealed class FluidPowerRepair(IGameClient game, IControllerJournal journal, string directory)
{
    public const int MaximumIslands = 16;

    public async Task<int> RunAsync(CancellationToken token = default, IReadOnlySet<string>? targetCellIds = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        token = deadline.Token;
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var registry = new FactoryRegistry(directory);
        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        var snapshot = await CaptureAsync();
        int repaired = 0;
        await using var controller = new SpatialController(game, journal);
        foreach (var initial in state.Cells.Where(c => c.Status == "ready"
            && c.Kind is FluidCellBuilder.ExtractorKind or FluidCellBuilder.MachineKind
            && (targetCellIds is null || targetCellIds.Contains(c.Id))))
        {
            // A previous link can reconnect the entire island; never repair its other consumers from an old photograph.
            state = await registry.LoadAsync(catalog.Scope.WorldId, token);
            var cell = state.Cells.Single(c => c.Id == initial.Id);
            snapshot = await CaptureAsync();
            var target = Target(cell, snapshot);
            if (target is null) continue;
            if (repaired >= MaximumIslands)
            {
                await journal.AppendAsync("fluid-power-repair-deferred", new { cell.Id, reason = "island-budget", snapshot.CollectedTick }, token);
                break;
            }
            var (pole, plan) = target.Value;
            var position = pole.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
            int surface = pole.Data.GetProperty("surfaceIndex").GetInt32();
            var zones = game is IDangerZoneReader reader
                ? await reader.ReadActiveDeathsAsync(catalog.Scope, surface, snapshot.CollectedTick, token) : [];
            if (zones.Any(z => DangerZones.Covers(z, position)))
            {
                await journal.AppendAsync("fluid-power-repair-deferred", new { cell.Id, reason = "recent-death", snapshot.CollectedTick }, token);
                continue;
            }
            var carried = FactoryLogistics.Carried(snapshot);
            string poleItem = catalog.Items.Where(p => p.Value.PlaceEntityType == "electric-pole"
                    && (carried.GetValueOrDefault(p.Key) > 0 || FactoryDirector.Enabled(catalog, p.Key)))
                .OrderBy(p => p.Key != plan.Item).ThenBy(p => p.Key != "small-electric-pole")
                .ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).FirstOrDefault()
                ?? throw new InvalidOperationException("No obtainable native pole can reconnect the fluid factory.");
            var ground = new FactoryGround(state, await new PowerExpansionController(game, journal, directory).SteamItemsAsync(catalog, token));
            var bare = ground with { Steam = null };
            var builder = new FactoryCellBuilder(game, journal, directory);
            // The surviving anchor can be a large pole while only small replacement poles are obtainable.
            string[] items = [.. ground.Items.Append(poleItem).Append(plan.Item).Distinct(StringComparer.Ordinal)];
            if (items.Length > 16) throw new InvalidOperationException("Too many reserved-ground items for one fluid power observation.");
            int links = 0;
            long before = snapshot.CollectedTick;
            await new CellPowerLinker(game, journal).ConnectAsync(pole.EntityId, position, poleItem, items, catalog, controller,
                [map => FactoryGround.Reserve(map, ground.Boxes(map), poleItem), map => FactoryGround.Reserve(map, bare.Boxes(map), poleItem)],
                count => builder.EnsureCarriedAsync(registry, catalog, poleItem, count, token),
                async (id, placement) =>
                {
                    var current = await registry.LoadAsync(catalog.Scope.WorldId, token);
                    var latest = current.Cells.Single(c => c.Id == cell.Id);
                    await registry.SaveAsync(current.With(FactoryCellBuilder.WithLink(latest, id, placement, poleItem)), token);
                    links++;
                }, token);
            snapshot = await CaptureAsync();
            string consumer = cell.Entities[cell.Kind == FluidCellBuilder.ExtractorKind ? "drill" : "machine"];
            if (FactoryPower.IsFed(snapshot, consumer) != true)
                throw new InvalidDataException("The repaired fluid pole did not restore its native consumer's generator connection.");
            repaired++;
            await journal.AppendAsync("fluid-power-repair", new { cell.Id, poleId = pole.EntityId, consumer, poleItem, links, before, snapshot.CollectedTick }, token);
        }
        return repaired;

        async Task<FactorySnapshot> CaptureAsync()
        {
            var known = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            if (known.Scope != catalog.Scope) throw new InvalidDataException("Actor identity changed during fluid power repair; reconcile registered links.");
            return known;
        }
    }

    /// <summary>A standing planned pole on the consumer's unfed native network. Adopted cells may only have link-n roles.</summary>
    internal static (FactoryRecord Pole, PlannedEntity Plan)? Target(FactoryCell cell, FactorySnapshot snapshot)
    {
        if (cell.Status != "ready" || cell.Kind is not (FluidCellBuilder.ExtractorKind or FluidCellBuilder.MachineKind) || cell.Plan is null)
            return null;
        string role = cell.Kind == FluidCellBuilder.ExtractorKind ? "drill" : "machine";
        if (!cell.Entities.TryGetValue(role, out string? id)) return null;
        var consumer = snapshot.Records.SingleOrDefault(r => r.Kind == "entity" && r.EntityId == id);
        if (consumer is null || consumer.Data.GetProperty("role").GetString() != "factory" || FactoryPower.IsFed(snapshot, id) != false
            || !consumer.Data.TryGetProperty("electricNetworkId", out var network) || network.ValueKind != JsonValueKind.Number) return null;
        var position = consumer.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
        var poles = cell.Entities.Where(p => p.Key == "pole" || p.Key.StartsWith("link-", StringComparison.Ordinal))
            .Select(p => (Role: p.Key, Pole: snapshot.Records.SingleOrDefault(r => r.Kind == "entity" && r.EntityId == p.Value), Plan: cell.Plan.GetValueOrDefault(p.Key)))
            .Where(p => p.Pole is not null && p.Plan is not null && p.Pole.Data.GetProperty("role").GetString() == "factory"
                && p.Pole.Data.GetProperty("type").GetString() == "electric-pole"
                && p.Pole.Data.TryGetProperty("electricNetworkId", out var other) && other.ValueKind == JsonValueKind.Number
                && other.GetInt64() == network.GetInt64()
                && p.Pole.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!.DistanceTo(p.Plan.Position) < .01)
            .OrderBy(p => p.Role != "pole").ThenBy(p => p.Plan!.Position.DistanceTo(position)).ThenBy(p => p.Role, StringComparer.Ordinal).FirstOrDefault();
        return poles.Pole is null ? null : (poles.Pole, poles.Plan!);
    }
}
