using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record RocketLaunchResult(string SiloId, long StartTick, long EndTick, long RocketsBefore, long RocketsAfter);

/// <summary>Installs or reuses a silo, supplies native parts and requires the engine's launch counter to advance.</summary>
public sealed class RocketLaunchController(IGameClient game, IControllerJournal journal)
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
        string siloId = initial.Silos.Where(s => s.Name == prototype.EntityName && s.NetworkId is not null)
            .OrderByDescending(s => s.Status == "rocket_ready").ThenByDescending(s => s.Parts).ThenBy(s => s.Id, StringComparer.Ordinal)
            .Select(s => s.Id).FirstOrDefault()
            ?? await power.InstallAsync(siloItem, catalog, controller, token);
        using var reservation = ProductionReservations.Enter(new HashSet<string> { siloId });
        await journal.AppendAsync("rocket-start", new { siloItem, siloId, initial.CollectedTick, initial.RocketsLaunched, initial.Scope }, token);
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
            ObservedRocketSilo silo = state.Silos.Single(s => s.Id == siloId);
            RocketStep step = RocketPlanner.Next(prototype, silo, recipe);
            await journal.AppendAsync("rocket-measurement", new { state, step }, token);
            if (step.Kind == "launch")
            {
                await controller.ApproachEntityAsync(siloId, silo.Position, catalog, token);
                // Movement and defense can change the state. Never launch from a stale ready flag.
                state = await ReadAsync();
                if (state.RocketsLaunched > initial.RocketsLaunched) continue;
                if (RocketPlanner.Next(prototype, state.Silos.Single(s => s.Id == siloId), recipe).Kind != "launch") continue;
                await ActAsync("launch_rocket", new { entityId = siloId });
                // Even a completed action receipt is followed by an independent native counter read.
                continue;
            }
            if (silo.Status == "building_rocket" && (reserveFuel || silo.Energy <= 0))
            {
                double energy = Math.Max(1, step.RemainingCycles) * recipe.EnergySeconds * 60 * prototype.EnergyPerTick / prototype.CraftingSpeed;
                await power.MaintainFuelAsync(siloId, energy, catalog, controller, reserveFuel, token);
                reserveFuel = false;
                state = await ReadAsync();
                silo = state.Silos.Single(s => s.Id == siloId);
            }
            var batch = RocketPlanner.SupplyBatch(prototype, silo, recipe);
            foreach (var input in batch.Where(p => p.Value > 0))
            {
                await new ProductionGoalExecutor(game, journal).RunAsync(input.Key, input.Value, token);
                await controller.ApproachEntityAsync(siloId, silo.Position, catalog, token);
                state = await ReadAsync();
                silo = state.Silos.Single(s => s.Id == siloId);
                var fresh = RocketPlanner.SupplyBatch(prototype, silo, recipe);
                owned = await production.ObserveAsync(token);
                RequireScope(owned.Scope);
                int count = checked((int)Math.Min(fresh.GetValueOrDefault(input.Key), owned.Inventory.GetValueOrDefault(input.Key)));
                if (count > 0) await ActAsync("insert", new { entityId = siloId, inventory = "input", item = input.Key, count });
            }
            await ActAsync("wait", new { ticks = 60 });
        }
        throw new TimeoutException("Rocket execution exhausted its observation budget. Reconcile native state before continuing.");

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
