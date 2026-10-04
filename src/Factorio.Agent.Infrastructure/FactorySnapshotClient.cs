using Factorio.Agent.Core;
using System.Text.Json;

namespace Factorio.Agent.Infrastructure;

public sealed class FactorySnapshotClient(IGameClient game)
{
    public async Task<FactorySnapshot> CaptureAsync(IReadOnlyList<string>? capacityItems = null, int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (capacityItems is { Count: > 8 } || capacityItems?.Distinct(StringComparer.Ordinal).Count() != capacityItems?.Count
            || capacityItems?.Any(string.IsNullOrWhiteSpace) == true)
            throw new ArgumentException("At most eight distinct item names may be probed.", nameof(capacityItems));
        ActorScope? expectedScope = null;
        long previousCaptureTick = -1;
        for (int attempt = 0; ; attempt++)
        {
            try { return await CapturePagesAsync(); }
            catch (GameRpcException error) when (error.Error.Code == "snapshot_expired" && attempt < 2)
            {
                // Native retention can evict a read while another reader collects the factory.
                // Only restart this read: discard every partial page and keep mutation errors fatal.
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        async Task<FactorySnapshot> CapturePagesAsync()
        {
            FactorySnapshotPage first = FactorySnapshotPage.Parse(await game.ExecuteAsync(GameRequest.Create("factory_snapshot",
                capacityItems is null ? new { limit = pageSize } : (object)new { limit = pageSize, capacityItems }), cancellationToken));
            if (expectedScope is not null && first.SnapshotScope != expectedScope || first.CollectedTick < previousCaptureTick)
                throw new InvalidDataException("Factory actor scope or observation history changed while restarting an expired read.");
            expectedScope = first.SnapshotScope;
            previousCaptureTick = first.CollectedTick;
            var records = new List<FactoryRecord>(first.TotalRecords);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            FactorySnapshotPage page = first;
            while (true)
            {
                if (page.SnapshotId != first.SnapshotId || page.CollectedTick != first.CollectedTick
                    || page.ExpiresTick != first.ExpiresTick || page.TotalRecords != first.TotalRecords
                    || page.SnapshotScope != first.SnapshotScope || page.Scope != page.SnapshotScope
                    || page.Offset != records.Count || page.Records.Count > pageSize
                    || !JsonElement.DeepEquals(page.Coverage, first.Coverage))
                    throw new InvalidDataException("Factory snapshot identity, scope or page continuity changed during collection.");
                foreach (FactoryRecord record in page.Records)
                {
                    if (!ids.Add(record.Id)) throw new InvalidDataException("A stock record was repeated across pages.");
                    records.Add(record);
                }
                if (page.Complete) break;
                page = FactorySnapshotPage.Parse(await game.ExecuteAsync(GameRequest.Create("factory_snapshot",
                    new { snapshotId = first.SnapshotId, offset = page.NextOffset, limit = pageSize }), cancellationToken));
            }
            var snapshot = new FactorySnapshot(first.SnapshotId, first.SnapshotScope, first.CollectedTick, first.ExpiresTick,
                first.Coverage, records.AsReadOnly());
            // Validate count representations now, before callers can treat this as a usable complete snapshot.
            snapshot.SummarizeStocks();
            return snapshot;
        }
    }
}
