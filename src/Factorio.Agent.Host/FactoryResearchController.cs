using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record FactoryResearchResult(string Technology, int Labs, int Rounds, long StartTick, long EndTick,
    IReadOnlyDictionary<string, long> Procured);

/// <summary>
/// Researches one available technology with persistent science cells and laboratories instead of hand-crafted packs.
/// The actor moves materials between cells; a persistent raw shortfall below the demanded rate first adds a miner or
/// smelter cell on a locally observed patch. The older actor-driven production path procures only raw items whose cells
/// stopped delivering or failed to grow, boiler fuel while power starves, and materials that no ready cell makes.
/// </summary>
public sealed class FactoryResearchController(IGameClient game, IControllerJournal journal, string directory)
{
    public const double TargetResearchSeconds = 900;

    public async Task<FactoryResearchResult> RunAsync(string technologyName, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromHours(3));
        token = deadline.Token;
        var observation = await new TechnologyClient(game).ReadDependenciesAsync(technologyName, token);
        var technology = observation.Technologies[technologyName];
        var next = new TechnologyPlanner().Next(technologyName, observation.Technologies);
        if (next.Kind != "research" || next.Technology != technologyName)
            throw new InvalidOperationException("Prepare this technology's prerequisites before factory research.");
        await EnsurePowerAsync(token);

        double unitSeconds = technology.EnergyTicks / 60;
        int labs = (int)Math.Clamp(Math.Ceiling(technology.Count * unitSeconds / TargetResearchSeconds), 1, 10);
        double minutes = Math.Max(1, technology.Count * unitSeconds / labs / 60);
        var director = new FactoryDirector(game, journal, directory);
        AutomationPlan? plan = null;
        foreach (var pack in technology.Ingredients)
            plan = await director.AutomateAsync(pack.Name, Math.Min(120, technology.Count * pack.Amount / minutes), token);
        // Each plan covers every registered target, so the last one holds the whole factory's raw demand.
        var rawRates = new Dictionary<string, double>(plan?.RawPerMinute ?? new Dictionary<string, double>(), StringComparer.Ordinal);
        await director.EnsureLabsAsync(labs, token);
        var builder = new FactoryCellBuilder(game, journal, directory);
        await builder.RepairPowerAsync(token);
        await journal.AppendAsync("factory-research-start", new { technologyName, technology.Count, unitSeconds, labs, minutes }, token);

        var logistics = new FactoryLogistics(game, journal, directory);
        var executor = new ProductionGoalExecutor(game, journal);
        var procured = new Dictionary<string, long>(StringComparer.Ordinal);
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var growth = new RawCapacityGrowth();
        var delivery = new CellDelivery();
        await using var controller = new SpatialController(game, journal);
        long startTick = observation.EndTick;
        for (int round = 1; round <= 2000; round++)
        {
            var state = ResearchSnapshot.Parse(await game.ExecuteAsync(GameRequest.Create("research_state", new { technology = technologyName }), token), technologyName);
            if (state.Researched)
            {
                var result = new FactoryResearchResult(technologyName, labs, round - 1, startTick, state.CollectedTick, procured);
                await journal.AppendAsync("factory-research-result", result, token);
                return result;
            }
            if (state.Selected != technologyName) await SelectAsync(controller, journal, state.Selected, technologyName, token);
            var service = await logistics.ServiceAsync(40, token);
            var shortfall = service.Shortfall.Where(p => p.Value > 0).OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
            var factory = await new FactoryRegistry(directory).LoadAsync(observation.Scope.WorldId, token);
            growth.Observe(service);
            delivery.Observe(service, factory.Cells);
            bool procuredAny = false, grew = false;
            var cellProducts = factory.Cells.Where(c => c.Recipe is not null && c.Status == "ready").Select(c => c.Recipe!).ToHashSet(StringComparer.Ordinal);
            foreach (var (item, missing) in shortfall)
            {
                bool raw = ResourceCellPlanner.Supply(catalog, item) is not null;
                if (!grew && raw && await GrowAsync(item, factory, service.Tick))
                {
                    grew = true;
                    continue;
                }
                if (LeftToCells(item, raw, service, cellProducts, delivery, growth)) continue;
                var carried = FactoryLogistics.Carried(await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token));
                int target = (int)Math.Min(1000, carried.GetValueOrDefault(item) + Math.Min(missing, 400));
                StockGoalResult stock;
                using (ProductionReservations.EnterFactory(await new FactoryRegistry(directory).LoadAsync(observation.Scope.WorldId, token)))
                    stock = await executor.RunAsync(item, target, token);
                procured[item] = procured.GetValueOrDefault(item) + Math.Max(0, stock.FinalStock - stock.InitialStock);
                procuredAny = true;
            }
            if (procuredAny || grew) continue;
            // Cells that stay silent may sit on an unfed pole island (e.g. built before a failed link).
            if (service.Collected.Count == 0 && round % 8 == 0 && await builder.RepairPowerAsync(token) > 0) continue;
            if (service.Actions == 0 || service.Collected.Count == 0)
            {
                var waited = await controller.WorkAsync("wait", new { ticks = 900 }, 1200, token: token);
                if (waited.Status != "completed") throw new InvalidOperationException($"Waiting for the factory ended with {waited.Status}.");
            }
        }
        throw new TimeoutException("Factory research exhausted its round budget.");

        // One bounded cell per round, in view first, then a few steps toward remembered deposits: once a small patch is
        // depleted, growing in view only would leave the raw item to hand-fed procurement for the rest of the research.
        async Task<bool> GrowAsync(string item, FactoryState factory, long tick)
        {
            double capacity = FactoryDirector.RawCapacity(factory, item).PerMinute;
            double demand = growth.Demand(item, rawRates.GetValueOrDefault(item), capacity);
            if (!growth.Due(item, RawCapacityGrowth.Cells(factory, item), capacity, demand)) return false;
            try
            {
                if ((await director.EnsureRawAsync(item, demand, token, maximumNewCells: 1, explorationBudget: RawCapacityGrowth.ExplorationBudget)).Built > 0)
                {
                    growth.Grew(item);
                    return true;
                }
            }
            catch (Exception error) when (Recoverable(error, token))
            {
                // A changed actor identity needs reconciliation by the caller, never a procurement fallback.
                if (ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token)).Scope != catalog.Scope) throw;
                await journal.AppendAsync("factory-raw-capacity-failed", new { item, demand, error = error.GetType().Name, error.Message }, token);
            }
            growth.Failed(item, tick);
            return false;
        }
    }

    /// <summary>
    /// Selects the goal's technology. A different selection left by an earlier goal, for instance one that reached its
    /// deadline, is replaced: the strategic arbiter owns research, and the engine keeps the replaced technology's saved
    /// progress. On 2026-09-30 (seed 20261002) refusing such a leftover failed five research goals in a row.
    /// </summary>
    internal static async Task SelectAsync(SpatialController controller, IControllerJournal journal, string? selected, string technology,
        CancellationToken token)
    {
        var receipt = await controller.WorkAsync("research", new { technology, replace = selected is not null }, 600, token: token);
        if (receipt.Status != "completed") throw new InvalidOperationException($"Research selection ended with {receipt.Status}: {receipt.Error?.Code}.");
        if (selected is not null) await journal.AppendAsync("research-selection-replaced", new { previous = selected, technology }, token);
    }

    /// <summary>
    /// Whether a short item is left to the factory this round. Assembler products wait for their cells. A raw item waits
    /// only while resource cells delivered it recently, its growth has not failed and boiler fuel is not starving.
    /// </summary>
    internal static bool LeftToCells(string item, bool raw, LogisticsResult round, IReadOnlySet<string> cellProducts,
        CellDelivery delivery, RawCapacityGrowth growth) => raw
        ? !growth.HasFailed(item, round.Tick) && !(item == FactoryLogistics.Fuel && round.PowerStarved) && delivery.Covers(item, round.Tick)
        : cellProducts.Contains(item) || round.Collected.ContainsKey(item);

    /// <summary>
    /// Growth failures that leave the item to ordinary procurement: refused plans, spent budgets, disproven native proofs
    /// and the builder's own deadline. Cancelling the research itself still aborts it.
    /// </summary>
    internal static bool Recoverable(Exception error, CancellationToken outer) =>
        error is InvalidOperationException or TimeoutException or InvalidDataException
        || error is OperationCanceledException && !outer.IsCancellationRequested;

    private async Task EnsurePowerAsync(CancellationToken token)
    {
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        var generator = snapshot.Records.FirstOrDefault(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "factory"
            && FactoryCellBuilder.IsPowerSource(r.Data.GetProperty("type").GetString()!));
        if (generator is null)
        {
            await new SteamPowerController(game, journal).RunAsync(token);
            return;
        }
        var state = await new FactoryRegistry(directory).LoadAsync(snapshot.Scope.WorldId, token);
        if (state.Zones.Count > 0) return;
        // A new zone is placed near the observed network, so start beside the generator.
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        await using var controller = new SpatialController(game, journal);
        await controller.TravelAsync(generator.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!, 6, catalog, token);
    }
}
