using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Prepares finite native fluid stocks and connects them to configured material-processing inputs.</summary>
public sealed class FluidSupplyController(IGameClient game, IControllerJournal journal)
{
    public async Task EnsureAsync(string machineId, NativeRecipe recipe, IReadOnlyDictionary<string, double> needed,
        ProductionCatalog catalog, SpatialController controller, CancellationToken token)
    {
        var factory = new FactorySnapshotClient(game);
        var spatial = new SpatialClient(game);
        string pipeItem = catalog.Items.Where(p => p.Value.PlaceEntityType == "pipe").OrderBy(p => p.Key, StringComparer.Ordinal).First().Key;
        foreach (var input in recipe.Ingredients.Where(i => i.DeterministicFluid).GroupBy(i => i.Name))
        {
            if (input.Any(i => i.Temperature is not null || i.MinimumTemperature is not null || i.MaximumTemperature is not null))
                throw new InvalidOperationException("Temperature-constrained chemical inputs require a thermal supply plan.");
            double required = needed[input.Key];
            if (required <= 0) continue;
            FactorySnapshot stock = await factory.CaptureAsync(cancellationToken: token);
            RequireScope(stock.Scope);
            double total = stock.SummarizeStocks().Fluids.GetValueOrDefault(input.Key);
            double inMachine = stock.FluidStockAt(machineId, input.Key);
            if (total - inMachine < required)
                await new FluidProductionController(game, journal).RunAsync(input.Key, required + inMachine, token);
            var owned = await new ProductionController(game, journal).ObserveAsync(token);
            RequireScope(owned.Scope);
            await controller.TravelAsync(owned.Entities.Single(e => e.Id == machineId).Position, 8, catalog, token);
            SpatialSnapshot map = await spatial.CaptureAsync([pipeItem], 48, token);
            stock = await factory.CaptureAsync(cancellationToken: token);
            RequireScope(map.Scope);
            RequireScope(stock.Scope);
            var route = new FluidSupplyPlanner().Find(map, stock, pipeItem, machineId, input.Key)
                ?? throw new InvalidOperationException("No verified stocked fluid source has a compatible route to the machine.");
            await journal.AppendAsync("chemical-fluid-supply", new { machineId, fluid = input.Key, required, route, stock.SnapshotId, stock.CollectedTick }, token);
            await new PipeConnectionController(game, journal).RunAsync(route.SourceId, machineId, input.Key, token);
        }
        void RequireScope(ActorScope scope)
        {
            if (scope != catalog.Scope) throw new InvalidDataException("Actor scope changed during chemical fluid supply.");
        }
    }
}
