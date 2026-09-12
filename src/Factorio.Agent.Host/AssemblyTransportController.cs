using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

internal sealed record AssemblyOutputCollection(bool Connected, bool Collected, long Pending = 0, long InTransit = 0);

/// <summary>Tracks ingredient flows together while native assembly consumes them and collects its connected output storage.</summary>
internal sealed class AssemblyTransportController(IGameClient game, IControllerJournal journal, ProductionCatalog catalog,
    string machineId, SpatialController controller)
{
    private readonly Dictionary<string, BeltTransportFlow> inputs = new(StringComparer.Ordinal);
    private readonly SpatialClient spatial = new(game);
    private readonly FactorySnapshotClient factory = new(game);
    private static readonly BeltTransportEquipment Equipment = new("transport-belt", "inserter", "small-electric-pole");

    public IReadOnlySet<string> ReservedEntityIds => inputs.Values.SelectMany(f => f.Boundary.ReservedEntityIds)
        .Append(machineId).ToHashSet(StringComparer.Ordinal);

    public async Task ObserveAsync(FactorySnapshot stock, CancellationToken token)
    {
        if (inputs.Count == 0) return;
        var map = await MapAsync(token);
        foreach (var flow in inputs.Values)
            await LogAsync(flow, flow.Measure(map, stock), token);
    }

    public async Task<bool> TrySupplyAsync(string item, int needed, CancellationToken token)
    {
        var map = await MapAsync(token);
        var stock = await factory.CaptureAsync(cancellationToken: token);
        if (inputs.TryGetValue(item, out var active))
        {
            var measurement = active.Measure(map, stock);
            await LogAsync(active, measurement, token);
            if (measurement.Reading.Source.Count + measurement.Reading.Transit > 0 || measurement.Reading.Source.InProcess
                || new SolidSupplyPlanner().HasConnectedInputs(map, stock, catalog, active.Boundary.Root)) return true;
            await journal.AppendAsync("assembly-input-flow-exhausted", new { machineId, item, measurement }, token);
            inputs.Remove(item); // Close this verified interval before any explicit actor delivery of the same ingredient.
        }
        var candidates = new SolidSupplyPlanner().Candidates(map, stock, catalog, machineId, item, ProductionReservations.Current);
        if (candidates.Count(c => c.Existing) > 1) throw new InvalidOperationException("Multiple incoming lines for one ingredient need a combined destination balance.");
        foreach (var candidate in candidates.Take(16))
        {
            if (!candidate.Existing)
            {
                var plan = await ControllerPlanning.RunAsync(cancellation => new BeltTransportPlanner()
                    .Find(map, Equipment, candidate.SourceId, machineId, cancellation), controller, TimeSpan.FromMinutes(5), token);
                if (plan is null) continue;
            }
            await journal.AppendAsync("assembly-input-source", new { machineId, item, needed, candidate, stock.SnapshotId, stock.CollectedTick }, token);
            var prepared = await new BeltTransportController(game, journal).EnsureAsync(candidate.SourceId, machineId, item,
                Math.Clamp(needed, 1, 1000), token, ReservedEntityIds);
            if (prepared.Flow.Scope != catalog.Scope) throw new InvalidDataException("Assembly scope changed during ingredient connection.");
            inputs.Add(item, prepared.Flow);
            return true;
        }
        await journal.AppendAsync("assembly-input-source-unavailable", new { machineId, item, stock.SnapshotId, stock.CollectedTick, assessed = Math.Min(16, candidates.Count) }, token);
        return false;
    }

    public async Task<AssemblyOutputCollection> TryCollectOutputAsync(string item, long wanted, FactorySnapshot stock, CancellationToken token)
    {
        if (wanted <= 0) return new(false, false);
        var map = await MapAsync(token);
        if (stock.Scope != catalog.Scope) throw new InvalidDataException("Assembly output stock scope changed.");
        bool connected = false;
        long pending = 0;
        long inTransit = 0;
        foreach (var container in map.Entities.Where(e => e.Id != machineId && e.Force == map.Entities.Single(m => m.Id == machineId).Force
            && map.Prototypes[e.Name].Type == "container"))
        {
            var line = BeltTransportNetwork.Find(map, machineId, container.Id);
            if (line is null) continue;
            connected = true;
            if (ProductionReservations.Current.Contains(container.Id))
                throw new InvalidOperationException("The machine output feeds a reserved stock and cannot be collected by nested production.");
            var endpoint = MaterialEndpoint.From(stock, catalog, container.Id, item, false);
            var source = MaterialEndpoint.From(stock, catalog, machineId, item, true);
            var flow = BeltTransportReading.From(stock, source, endpoint, item, line.BeltIds, [line.SourceInserterId, line.TargetInserterId]);
            pending = checked(flow.Source.Count + flow.Transit);
            inTransit = flow.Transit;
            long count = endpoint.Read(stock, item).Count;
            if (count == 0) continue;
            await controller.ApproachEntityAsync(container.Id, container.Position, catalog, token);
            var taken = await controller.WorkAsync("take", new { entityId = container.Id, inventory = "output", item, count = Math.Min(wanted, count) }, 600, token: token);
            if (taken.Status != "completed") throw new InvalidOperationException("Connected assembly output collection needs reconciliation.");
            await journal.AppendAsync("assembly-output-storage-collected", new { machineId, containerId = container.Id, item, taken }, token);
            return new(true, true);
        }
        if (!connected && map.Entities.Any(e => e.PickupTargetId == machineId))
            throw new InvalidOperationException("An existing output extractor requires reconciliation before direct collection.");
        return new(connected, false, pending, inTransit);
    }

    private Task LogAsync(BeltTransportFlow flow, BeltTransportMeasurement measurement, CancellationToken token) =>
        journal.AppendAsync("assembly-input-flow", new { machineId, item = flow.Item, sourceId = flow.Boundary.SourceId, flow.StartTick, measurement }, token);

    private async Task<SpatialSnapshot> MapAsync(CancellationToken token)
    {
        var map = await spatial.CaptureAsync([Equipment.Belt, Equipment.Inserter, Equipment.Pole], 48, token);
        if (map.Scope != catalog.Scope) throw new InvalidDataException("Assembly transport scope changed.");
        if (!map.Entities.Any(e => e.Id == machineId)) throw new InvalidOperationException("Assembly transport requires the local machine to remain observed.");
        return map;
    }
}
