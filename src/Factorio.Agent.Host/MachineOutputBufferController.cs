using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Drains a blocked deterministic machine through a native transport line so its inputs can keep flowing.</summary>
public sealed class MachineOutputBufferController(IGameClient game, IControllerJournal journal)
{
    public async Task EnsureAsync(string machineId, string reservedSourceId, ProductionCatalog catalog,
        SpatialController controller, CancellationToken token)
    {
        var stock = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        RequireScope(stock.Scope);
        var work = stock.Records.Single(r => r.EntityId == machineId && r.Kind == "work");
        var recipe = catalog.Recipes.Single(r => r.Name == work.Data.GetProperty("recipe").GetString());
        if (recipe.Products.Count != 1 || !recipe.Products[0].DeterministicItem)
            throw new InvalidOperationException("Automatic output storage currently requires one deterministic solid product.");
        string item = recipe.Products[0].Name;
        var equipment = new BeltTransportEquipment("transport-belt", "inserter", "small-electric-pole");
        const string containerItem = "wooden-chest";
        var spatial = new SpatialClient(game);
        var map = await spatial.CaptureAsync([equipment.Belt, equipment.Inserter, equipment.Pole, containerItem], 48, token);
        RequireScope(map.Scope);
        string? targetId = null;
        foreach (var container in map.Entities.Where(e => map.Prototypes[e.Name].Type == "container" && e.Id != reservedSourceId
            && e.Force == map.Entities.Single(m => m.Id == machineId).Force))
        {
            if (BeltTransportNetwork.Find(map, machineId, container.Id) is not null) { targetId = container.Id; break; }
        }
        if (targetId is null)
        {
            var placement = await ControllerPlanning.RunAsync(cancellation => new MachineOutputBufferPlanner()
                .Find(map, machineId, containerItem, equipment, cancellation), controller, TimeSpan.FromMinutes(5), token)
                ?? throw new InvalidOperationException("No reachable output container and transport layout found for the blocked machine.");
            await journal.AppendAsync("machine-output-buffer-plan", new { machineId, item, placement, map.Scope, map.CollectedTick }, token);
            await new ProductionController(game, journal).ProduceAsync(containerItem, 1, token,
                new HashSet<string>(StringComparer.Ordinal) { reservedSourceId, machineId });
            targetId = await new PoweredMachineController(game, journal).BuildAtAsync(containerItem, placement, catalog, controller, token);
        }
        var drained = await new BeltTransportController(game, journal).RunAsync(machineId, targetId, item, 1, token);
        await journal.AppendAsync("machine-output-buffer-ready", new { machineId, targetId, item, drained }, token);

        void RequireScope(ActorScope scope)
        {
            if (scope != catalog.Scope) throw new InvalidDataException("Actor scope changed during output storage installation.");
        }
    }
}
