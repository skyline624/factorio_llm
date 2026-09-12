using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record BeltTransportResult(string SourceId, string TargetId, string Item, int Requested, long Delivered,
    long StartTick, long EndTick, BeltTransportInstallation Installation, int PoweredSamples);

/// <summary>Installs or reuses an isolated native transport line and reconciles both endpoints with all transit.</summary>
public sealed class BeltTransportController(IGameClient game, IControllerJournal journal)
{
    public async Task<BeltTransportResult> RunAsync(string sourceId, string targetId, string item, int quantity, CancellationToken token = default,
        IReadOnlySet<string>? reservedEntityIds = null)
    {
        if (sourceId == targetId || quantity is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(quantity));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(45));
        token = deadline.Token;
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var production = new ProductionController(game, journal);
        var known = await production.ObserveAsync(token);
        RequireScope(known.Scope);
        var sourceEntity = known.Entities.Single(e => e.Id == sourceId);
        _ = known.Entities.Single(e => e.Id == targetId);
        var equipment = new BeltTransportEquipment("transport-belt", "inserter", "small-electric-pole");
        string[] items = [equipment.Belt, equipment.Inserter, equipment.Pole];
        var factory = new FactorySnapshotClient(game);
        var spatial = new SpatialClient(game);
        await using var controller = new SpatialController(game, journal);
        await controller.ApproachEntityAsync(sourceId, sourceEntity.Position, catalog, token);
        var map = await MapAsync();
        var initial = await StockAsync();
        var boundary = BeltTransportBoundary.From(map, initial, catalog, sourceId, targetId, item);
        var reserved = boundary.ReservedEntityIds.Concat(reservedEntityIds ?? new HashSet<string>()).Append(targetId).ToHashSet(StringComparer.Ordinal);
        var target = MaterialEndpoint.From(initial, catalog, targetId, item, false);
        if (target.Recipe is not null && map.Entities.Single(e => e.Id == targetId).Status is null)
            throw new InvalidDataException("Machine transport requires its native operating status; update the mod before construction.");
        target.Read(initial, item);
        await journal.AppendAsync("belt-transport-boundary", new { sourceId, targetId, boundary.Root, boundary.Buffers, boundary.Segments }, token);
        var installation = boundary.FindConnection(map, targetId);
        BeltTransportReading baseline;
        long startTick;
        if (installation is null)
        {
            if (map.Entities.Any(e => e.PickupTargetId == sourceId
                || (e.PickupPosition is not null && map.Entities.Single(s => s.Id == sourceId).Bounds.Contains(e.PickupPosition))))
                throw new InvalidOperationException("An existing source extractor needs reconciliation before adding another transport line.");
            var plan = await ControllerPlanning.RunAsync(cancellation => new BeltTransportPlanner().Find(map, equipment, sourceId, targetId, cancellation),
                controller, TimeSpan.FromMinutes(5), token)
                ?? throw new InvalidOperationException("No powered isolated belt-and-inserter layout found in the observed area.");
            await journal.AppendAsync("belt-transport-plan", new { sourceId, targetId, item, quantity, equipment, plan, map.Scope, map.CollectedTick }, token);
            await production.ProduceAsync(equipment.Belt, plan.Belts.Count, token, reserved);
            await production.ProduceAsync(equipment.Inserter, 2, token, reserved);
            if (plan.Poles.Count > 0) await production.ProduceAsync(equipment.Pole, plan.Poles.Count, token, reserved);
            var builder = new PoweredMachineController(game, journal);
            var remaining = plan.Poles.Select(p => p.Position).Concat(plan.Belts.Select(p => p.Position))
                .Append(plan.SourceInserter.Position).Append(plan.TargetInserter.Position).ToList();
            foreach (var pole in plan.Poles)
            {
                remaining.Remove(pole.Position);
                await builder.BuildAtAsync(equipment.Pole, pole, catalog, controller, token, remaining);
            }
            var beltIds = new List<string>();
            foreach (var belt in plan.Belts)
            {
                remaining.Remove(belt.Position);
                beltIds.Add(await builder.BuildAtAsync(equipment.Belt, belt, catalog, controller, token, remaining));
            }
            remaining.Remove(plan.TargetInserter.Position);
            string targetArm = await builder.BuildAtAsync(equipment.Inserter, plan.TargetInserter, catalog, controller, token, remaining);
            var beforeActivation = await StockAsync();
            baseline = boundary.Read(beforeActivation, target, item, beltIds, [targetArm]);
            startTick = beforeActivation.CollectedTick;
            string sourceArm = await builder.BuildAtAsync(equipment.Inserter, plan.SourceInserter, catalog, controller, token);
            installation = new(sourceArm, targetArm, beltIds);
            await journal.AppendAsync("belt-transport-installed", new { installation, baseline, startTick }, token);
        }
        else
        {
            baseline = boundary.Read(initial, target, item, installation.BeltIds,
                [installation.SourceInserterId, installation.TargetInserterId]);
            startTick = initial.CollectedTick;
            await journal.AppendAsync("belt-transport-reused", new { installation, baseline, startTick }, token);
        }
        double speed = map.Prototypes[map.Items[equipment.Belt].EntityName].BeltSpeed
            ?? throw new InvalidDataException("Missing native transport speed.");
        long observationBudget = BeltTransportTiming.ObservationBudget(quantity,
            installation.BeltIds.Count + boundary.Segments.Sum(s => s.Installation.BeltIds.Count), speed,
            BeltTransportTiming.SecondsPerItem(boundary.Root, map, catalog), BeltTransportTiming.SecondsPerItem(target, map, catalog));
        await journal.AppendAsync("belt-transport-window", new { startTick, observationBudget, quantity }, token);
        int powered = 0;
        bool outputPrepared = false;
        long outputInstallationTicks = 0;
        while (true)
        {
            var waited = await controller.WorkAsync("wait", new { ticks = 30 }, 600, token: token);
            if (waited.Status != "completed") throw new InvalidOperationException("Transport supervision did not complete its native wait.");
            map = await MapAsync();
            boundary.VerifyConnection(map, targetId, installation);
            var current = await StockAsync();
            var reading = boundary.Read(current, target, item, installation.BeltIds,
                [installation.SourceInserterId, installation.TargetInserterId]);
            long delivered = reading.DeliveredSince(baseline);
            bool energized = map.Entities.Single(e => e.Id == installation.SourceInserterId).Power?.Energy > 0
                && map.Entities.Single(e => e.Id == installation.TargetInserterId).Power?.Energy > 0;
            if (energized) powered++;
            await journal.AppendAsync("belt-transport-measurement", new { current.SnapshotId, current.CollectedTick, reading, delivered, energized }, token);
            if (delivered >= quantity)
            {
                var result = new BeltTransportResult(sourceId, targetId, item, quantity, delivered, startTick, current.CollectedTick, installation, powered);
                await journal.AppendAsync("belt-transport-result", result, token);
                return result;
            }
            if (!outputPrepared && target.Recipe is not null && map.Entities.Single(e => e.Id == targetId).Status == "full_output")
            {
                await new MachineOutputBufferController(game, journal).EnsureAsync(targetId, sourceId, catalog, controller, token, reserved);
                outputInstallationTicks = checked(outputInstallationTicks + (await StockAsync()).CollectedTick - current.CollectedTick);
                outputPrepared = true;
                // The next atomic snapshot includes all production and transfers while the output line was installed.
                continue;
            }
            if (boundary.Root.Recipe is null && reading.Source.Count == 0 && reading.Transit == 0)
                throw new InvalidOperationException($"The source is exhausted after {delivered} verified deliveries.");
            if (current.CollectedTick - startTick - outputInstallationTicks > observationBudget)
                throw new TimeoutException($"The native transport window ended after {delivered} verified deliveries; preserve the installed line.");
        }

        void RequireScope(ActorScope scope)
        {
            if (scope != catalog.Scope) throw new InvalidDataException("Actor scope changed during belt transport.");
        }
        async Task<SpatialSnapshot> MapAsync()
        {
            var observed = await spatial.CaptureAsync(items, 48, token);
            RequireScope(observed.Scope);
            if (!observed.Entities.Any(e => e.Id == sourceId) || !observed.Entities.Any(e => e.Id == targetId))
                throw new InvalidOperationException("Both transport endpoints must remain in the observed construction area.");
            return observed;
        }
        async Task<FactorySnapshot> StockAsync()
        {
            var observed = await factory.CaptureAsync(cancellationToken: token);
            RequireScope(observed.Scope);
            return observed;
        }
    }
}
