using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Extends an owned native electric network to a fixed consumer, verifying each constructed link.</summary>
internal sealed class PowerGridController(IGameClient game, IControllerJournal journal)
{
    public async Task ConnectAsync(string machineId, ProductionCatalog catalog, SpatialController controller, CancellationToken token)
    {
        var production = new ProductionController(game, journal);
        var executor = new ProductionGoalExecutor(game, journal);
        var power = new PoweredMachineController(game, journal);
        var spatial = new SpatialClient(game);
        var state = await ObserveAsync();
        var machine = state.Entities.Single(e => e.Id == machineId);
        var poles = catalog.Items.Where(p => p.Value.PlaceEntityType == "electric-pole"
            && (state.Inventory.GetValueOrDefault(p.Key) > 0 || catalog.Recipes.Any(r => r.Enabled
                && r.Products.Any(product => product.Name == p.Key && product.DeterministicItem)))).Select(p => p.Key).ToArray();
        if (poles.Length is 0 or > 16) throw new InvalidOperationException("No bounded constructible native pole catalog.");
        await controller.ApproachEntityAsync(machineId, machine.Position, catalog, token);
        var map = await MapAsync();
        var target = map.Entities.Single(e => e.Id == machineId);
        WorldBox footprint = target.Bounds;
        if (!map.Prototypes[target.Name].IsElectric) throw new InvalidDataException("Grid target is not an electric consumer.");
        if (target.Power?.NetworkId is not null) return;
        var names = poles.Select(item => catalog.Items[item].PlaceEntity).ToHashSet(StringComparer.Ordinal);
        if (!state.Entities.Any(e => names.Contains(e.Name)))
        {
            await new SteamPowerController(game, journal).RunAsync(token);
            state = await ObserveAsync();
        }
        var source = state.Entities.Where(e => names.Contains(e.Name)).OrderBy(e => e.Position.DistanceTo(machine.Position)).First();
        await controller.TravelAsync(source.Position, 8, catalog, token);
        map = await MapAsync();
        string poleItem = PowerGridPlanner.ChoosePole(map, poles, state.Inventory);
        for (int links = 0; links < 128; links++)
        {
            map = await MapAsync();
            state = await ObserveAsync();
            var owned = state.Entities.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            var link = await ControllerPlanning.RunAsync(t => new PowerGridPlanner().Next(map, poleItem, footprint, owned, t),
                controller, TimeSpan.FromMinutes(2), token);
            if (link.Status == PowerGridSearchStatus.Connected)
            {
                long network = map.Entities.Single(e => e.Id == link.SourceId).Power!.NetworkId!.Value;
                await controller.ApproachEntityAsync(machineId, machine.Position, catalog, token);
                map = await MapAsync();
                if (map.Entities.Single(e => e.Id == machineId).Power?.NetworkId != network)
                    throw new InvalidDataException("Native consumer did not join the supplying pole network.");
                await journal.AppendAsync("power-grid-connected", new { machineId, network, links, map.CollectedTick }, token);
                return;
            }
            if (link.Status != PowerGridSearchStatus.Extension || link.Pole is null || link.SourceId is null)
                throw new InvalidOperationException($"Observed grid extension ended with {link.Status}; no global impossibility is inferred.");
            var previous = map.Entities.Single(e => e.Id == link.SourceId);
            await executor.RunAsync(poleItem, 1, token);
            string added = await power.BuildAtAsync(poleItem, link.Pole, catalog, controller, token);
            map = await MapAsync();
            var connected = map.Entities.Single(e => e.Id == added);
            if (connected.Power?.NetworkId is not { } actual || actual != map.Entities.Single(e => e.Id == previous.Id).Power?.NetworkId)
                throw new InvalidDataException("Constructed pole did not join its planned native network; retain partial construction.");
            await journal.AppendAsync("power-grid-link", new { machineId, poleItem, sourceId = previous.Id, poleId = added,
                link.Pole, network = actual, map.CollectedTick }, token);
            await controller.TravelAsync(connected.Position, 3, catalog, token);
        }
        throw new TimeoutException("Electric grid extension exhausted its 128-link budget.");

        async Task<ProductionState> ObserveAsync()
        {
            var value = await production.ObserveAsync(token);
            if (value.Scope != catalog.Scope || value.ControlMode != "ai") throw new InvalidDataException("Grid construction actor changed.");
            return value;
        }
        async Task<SpatialSnapshot> MapAsync()
        {
            var value = await spatial.CaptureAsync(poles, 48, token);
            if (value.Scope != catalog.Scope) throw new InvalidDataException("Grid construction map changed.");
            return value;
        }
    }
}
