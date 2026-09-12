using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Adds bounded passive pipe storage and verifies the native connected capacity after every build.</summary>
public sealed class FluidBufferController(IGameClient game, IControllerJournal journal)
{
    public async Task EnsureAsync(string machineId, string fluid, double requiredCapacity, ProductionCatalog catalog,
        SpatialController controller, CancellationToken token)
    {
        if (!double.IsFinite(requiredCapacity) || requiredCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(requiredCapacity));
        string pipeItem = catalog.Items.Where(p => p.Value.PlaceEntityType == "pipe").OrderBy(p => p.Key, StringComparer.Ordinal).First().Key;
        var spatial = new SpatialClient(game);
        var factory = new FactorySnapshotClient(game);
        double priorCapacity = -1;
        for (int added = 0; added <= 200; added++)
        {
            SpatialSnapshot map = await spatial.CaptureAsync([pipeItem], 48, token);
            FactorySnapshot stock = await factory.CaptureAsync(cancellationToken: token);
            if (map.Scope != catalog.Scope || stock.Scope != catalog.Scope)
                throw new InvalidDataException("Actor scope changed during fluid buffer construction.");
            var connected = FluidBufferPlanner.ConnectedPipeIds(map, machineId, fluid);
            var records = connected.SelectMany(id => stock.FluidRecordsAt(id)).DistinctBy(r => r.Id).ToArray();
            if (records.Any(r => r.Data.GetProperty("contents").EnumerateObject().Any(p => p.Name != fluid && p.Value.GetDouble() > 0)))
                throw new InvalidDataException("Output storage contains another fluid.");
            double capacity = records.Sum(r => r.Data.GetProperty("capacity").GetDouble());
            if (!double.IsFinite(capacity) || capacity < 0 || (added > 0 && capacity <= priorCapacity))
                throw new InvalidDataException("Constructed pipe did not increase verified connected output capacity.");
            await journal.AppendAsync("fluid-buffer-capacity", new
            {
                machineId,
                fluid,
                requiredCapacity,
                capacity,
                added,
                stock.SnapshotId,
                stock.CollectedTick,
                connected
            }, token);
            if (capacity >= requiredCapacity) return;
            if (added == 200) throw new InvalidOperationException("Output capacity exceeds the bounded pipe-storage construction budget.");
            var extension = new FluidBufferPlanner().Next(map, pipeItem, machineId, fluid)
                ?? throw new InvalidOperationException("No clear output storage extension in the observed area.");
            await new ProductionGoalExecutor(game, journal).RunAsync(pipeItem, 1, token);
            string id = await new PoweredMachineController(game, journal).BuildAtAsync(pipeItem,
                new(extension.Position, 0, 0), catalog, controller, token);
            await journal.AppendAsync("fluid-buffer-built", new { machineId, fluid, id, extension }, token);
            priorCapacity = capacity;
        }
    }
}
