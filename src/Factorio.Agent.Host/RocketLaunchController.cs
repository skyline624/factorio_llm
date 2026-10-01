using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record RocketLaunchResult(string SiloId, long StartTick, long EndTick, long RocketsBefore, long RocketsAfter);

/// <summary>
/// Installs or reuses a silo, supplies native parts and requires the engine's launch counter to advance. With a factory registry,
/// a registered silo cell comes first; when no powered silo stands, the factory's cell is resumed or rebuilt at its plan, or built
/// when it has none, never doubled. The cell's inserter alone feeds the silo from the chest that factory logistics restocks, and
/// nothing is ever inserted into a cell's silo by hand.
/// </summary>
public sealed class RocketLaunchController(IGameClient game, IControllerJournal journal, string? factoryDirectory = null)
{
    public async Task<RocketLaunchResult> RunAsync(string siloItem, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromHours(2));
        token = deadline.Token;
        // Refresh the own-entity registry before the first specialized observation.
        var production = new ProductionController(game, journal);
        ProductionState owned = await production.ObserveAsync(token);
        RocketSnapshot initial = RocketSnapshot.Parse(await game.ExecuteAsync(GameRequest.Create("rocket_state"), token));
        ProductionCatalog catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        RequireScope(owned.Scope);
        RequireScope(catalog.Scope);
        if (!initial.Prototypes.TryGetValue(siloItem, out var prototype)
            || !catalog.Items.TryGetValue(siloItem, out var item) || item.PlaceEntityType != "rocket-silo")
            throw new InvalidOperationException("Launch requires an exact native silo item.");
        NativeRecipe recipe = catalog.Recipes.Single(r => r.Name == prototype.Recipe);
        if (!recipe.Enabled) throw new InvalidOperationException("Research the native rocket-part recipe before launching.");
        await using var controller = new SpatialController(game, journal);
        var power = new PoweredMachineController(game, journal);
        var registry = factoryDirectory is null ? null : new FactoryRegistry(factoryDirectory);
        var factory = registry is null ? null : await registry.LoadAsync(catalog.Scope.WorldId, token);
        var siloCells = factoryDirectory is null ? null : new SiloCellSupply(game, journal, factoryDirectory);
        FactoryCell? cell = factory is null ? null : SiloCellSupply.Registered(factory, initial, prototype);
        var cellSilos = SiloCellSupply.Silos(factory);
        string? siloId = cell?.Entities["machine"] ?? initial.Silos
            .Where(s => s.Name == prototype.EntityName && s.NetworkId is not null && !cellSilos.Contains(s.Id))
            .OrderByDescending(s => s.Status == "rocket_ready").ThenByDescending(s => s.Parts).ThenBy(s => s.Id, StringComparer.Ordinal)
            .Select(s => s.Id).FirstOrDefault();
        if (siloId is null && siloCells is not null)
        {
            // Without a standing silo, the factory's own cell is resumed or rebuilt, never doubled; a factory without one builds it
            // rather than a loose silo fed by hand.
            cell = await siloCells.RestoreAsync(factory!, siloItem, prototype, catalog, controller, token);
            if (cell is null && FactoryDirector.Available(catalog))
            {
                await new PowerExpansionController(game, journal, factoryDirectory!).EnsureCapacityForCellsAsync(siloItem, 1, true, token);
                cell = await new FactoryCellBuilder(game, journal, factoryDirectory!).BuildAsync(SiloCellPlanner.Kind, siloItem, prototype.Recipe, token);
            }
            siloId = cell?.Entities["machine"];
        }
        siloId ??= await power.InstallAsync(siloItem, catalog, controller, token);
        using var reservation = ProductionReservations.Enter(new HashSet<string> { siloId });
        // Nested production neither reuses nor empties factory cells, the silo cell's chest included.
        using var cellReservation = registry is null ? null : ProductionReservations.EnterFactory(await registry.LoadAsync(catalog.Scope.WorldId, token));
        var supply = cell is null ? null : siloCells;
        await journal.AppendAsync("rocket-start", new { siloItem, siloId, cell = cell?.Id, initial.CollectedTick, initial.RocketsLaunched, initial.Scope }, token);
        bool reserveFuel = true;
        for (int attempt = 0; attempt < 7200; attempt++)
        {
            token.ThrowIfCancellationRequested();
            RocketSnapshot state = await ReadAsync();
            if (state.RocketsLaunched > initial.RocketsLaunched)
            {
                var result = new RocketLaunchResult(siloId, initial.CollectedTick, state.CollectedTick, initial.RocketsLaunched, state.RocketsLaunched);
                await journal.AppendAsync("rocket-result", result, token);
                return result;
            }
            ObservedRocketSilo silo = Silo(state);
            RocketStep step = RocketPlanner.Next(prototype, silo, recipe);
            await journal.AppendAsync("rocket-measurement", new { state, step }, token);
            if (step.Kind == "launch")
            {
                await controller.ApproachEntityAsync(siloId, silo.Position, catalog, token);
                // Movement and defense can change the state. Never launch from a stale ready flag.
                state = await ReadAsync();
                if (state.RocketsLaunched > initial.RocketsLaunched) continue;
                if (RocketPlanner.Next(prototype, Silo(state), recipe).Kind != "launch") continue;
                await ActAsync("launch_rocket", new { entityId = siloId });
                // Even a completed action receipt is followed by an independent native counter read.
                continue;
            }
            // A cell is tended first, whatever the step: its maintenance rebuilds a destroyed pole before the silo's power is judged,
            // and a cell still faulty leaves its power to that maintenance rather than to a steam supply it cannot reach.
            bool healthy = supply is null || await supply.TendAsync(cell!.Id, prototype, recipe, catalog, async () => Silo(await ReadAsync()), token);
            if (healthy && silo.Status == "building_rocket" && (reserveFuel || silo.Energy <= 0))
            {
                double energy = Math.Max(1, step.RemainingCycles) * recipe.EnergySeconds * 60 * prototype.EnergyPerTick / prototype.CraftingSpeed;
                await power.MaintainFuelAsync(siloId, energy, catalog, controller, reserveFuel, token);
                reserveFuel = false;
                state = await ReadAsync();
                silo = Silo(state);
            }
            if (supply is not null)
            {
                // The cell's inserter feeds the silo meanwhile.
                await ActAsync("wait", new { ticks = 60 });
                continue;
            }
            var batch = RocketPlanner.SupplyBatch(prototype, silo, recipe).Where(p => p.Value > 0).ToArray();
            // Collect the bounded delivery before returning to the silo, instead of one round trip per ingredient.
            foreach (var input in batch)
                await new ProductionGoalExecutor(game, journal).RunAsync(input.Key, input.Value, token);
            if (batch.Length > 0)
                await controller.ApproachEntityAsync(siloId, silo.Position, catalog, token);
            foreach (var input in batch)
            {
                state = await ReadAsync();
                silo = Silo(state);
                var fresh = RocketPlanner.SupplyBatch(prototype, silo, recipe);
                owned = await production.ObserveAsync(token);
                RequireScope(owned.Scope);
                int count = checked((int)Math.Min(fresh.GetValueOrDefault(input.Key), owned.Inventory.GetValueOrDefault(input.Key)));
                if (count > 0) await ActAsync("insert", new { entityId = siloId, inventory = "input", item = input.Key, count });
            }
            await ActAsync("wait", new { ticks = 60 });
        }
        throw new TimeoutException("Rocket execution exhausted its observation budget. Reconcile native state before continuing.");

        // A destroyed silo ends this launch; the next one restores a cell's silo at its plan.
        ObservedRocketSilo Silo(RocketSnapshot value) => value.Silos.SingleOrDefault(s => s.Id == siloId)
            ?? throw new InvalidOperationException($"The launch silo {siloId} is no longer observed; reconcile before launching again.");
        async Task<RocketSnapshot> ReadAsync()
        {
            var value = RocketSnapshot.Parse(await game.ExecuteAsync(GameRequest.Create("rocket_state"), token));
            RequireScope(value.Scope);
            if (value.CollectedTick < initial.CollectedTick || value.RocketsLaunched < initial.RocketsLaunched)
                throw new InvalidDataException("Native rocket counters regressed.");
            return value;
        }
        async Task ActAsync(string kind, object args)
        {
            var receipt = await controller.WorkAsync(kind, args, 36000, token: token);
            if (receipt.Status != "completed")
                throw new InvalidOperationException($"Rocket action {kind} ended with {receipt.Status}: {receipt.Error?.Code}. Reconcile partial effects before continuing.");
        }
        void RequireScope(ActorScope scope)
        {
            if (scope != initial.Scope) throw new InvalidDataException("Actor changed during rocket execution; reconcile partial effects.");
        }
    }
}
