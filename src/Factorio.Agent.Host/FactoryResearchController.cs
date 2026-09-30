using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record FactoryResearchResult(string Technology, int Labs, int Rounds, long StartTick, long EndTick,
    IReadOnlyDictionary<string, long> Procured);

/// <summary>
/// Researches one available technology with persistent science cells and laboratories instead of hand-crafted packs.
/// The actor moves materials between cells; a recurring raw shortfall first adds a miner or smelter cell on a locally
/// observed patch, and only raw items that no cell can supply are procured by the older actor-driven production path.
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
        var rawRates = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var pack in technology.Ingredients)
            foreach (var (raw, rate) in (await director.AutomateAsync(pack.Name, Math.Min(120, technology.Count * pack.Amount / minutes), token)).RawPerMinute)
                rawRates[raw] = rawRates.GetValueOrDefault(raw) + rate;
        await director.EnsureLabsAsync(labs, token);
        await journal.AppendAsync("factory-research-start", new { technologyName, technology.Count, unitSeconds, labs, minutes }, token);

        var logistics = new FactoryLogistics(game, journal, directory);
        var executor = new ProductionGoalExecutor(game, journal);
        var procured = new Dictionary<string, long>(StringComparer.Ordinal);
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var growth = new RawCapacityGrowth();
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
            if (state.Selected is not null && state.Selected != technologyName)
                throw new InvalidOperationException("Another technology is selected. Reconcile research before replacing it.");
            if (state.Selected is null)
            {
                var selected = await controller.WorkAsync("research", new { technology = technologyName }, 600, token: token);
                if (selected.Status != "completed") throw new InvalidOperationException($"Research selection ended with {selected.Status}: {selected.Error?.Code}.");
            }
            var service = await logistics.ServiceAsync(40, token);
            var shortfall = service.Shortfall.Where(p => p.Value > 0).OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
            growth.Observe(shortfall.Select(p => p.Key));
            // Only materials that no ready cell makes are procured by the older actor-driven production path.
            bool procuredAny = false, grew = false;
            var cellProducts = (await new FactoryRegistry(directory).LoadAsync(observation.Scope.WorldId, token)).Cells
                .Where(c => c.Recipe is not null && c.Status == "ready").Select(c => c.Recipe!).ToHashSet(StringComparer.Ordinal);
            foreach (var (item, missing) in shortfall)
            {
                if (!grew && growth.Due(item) && ResourceCellPlanner.Supply(catalog, item) is not null && await GrowAsync(item))
                {
                    grew = true;
                    continue;
                }
                if (cellProducts.Contains(item) || service.Collected.ContainsKey(item)) continue;
                var carried = FactoryLogistics.Carried(await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token));
                int target = (int)Math.Min(1000, carried.GetValueOrDefault(item) + Math.Min(missing, 400));
                var stock = await executor.RunAsync(item, target, token);
                procured[item] = procured.GetValueOrDefault(item) + Math.Max(0, stock.FinalStock - stock.InitialStock);
                procuredAny = true;
            }
            if (procuredAny || grew) continue;
            if (service.Actions == 0 || service.Collected.Count == 0)
            {
                var waited = await controller.WorkAsync("wait", new { ticks = 900 }, 1200, token: token);
                if (waited.Status != "completed") throw new InvalidOperationException($"Waiting for the factory ended with {waited.Status}.");
            }
        }
        throw new TimeoutException("Factory research exhausted its round budget.");

        // One bounded cell per round, on deposits already in view: research never wanders off to explore.
        async Task<bool> GrowAsync(string item)
        {
            try
            {
                var state = await new FactoryRegistry(directory).LoadAsync(observation.Scope.WorldId, token);
                double rate = RawCapacityGrowth.TargetRate(rawRates.GetValueOrDefault(item), FactoryDirector.RawCapacity(state, item).PerMinute);
                var capacity = await director.EnsureRawAsync(item, rate, token, maximumNewCells: 1, explorationBudget: 0);
                if (capacity.Built > 0)
                {
                    growth.Grew(item);
                    return true;
                }
            }
            catch (Exception error) when (error is InvalidOperationException or TimeoutException)
            {
                await journal.AppendAsync("factory-raw-capacity-failed", new { item, error = error.Message }, token);
            }
            growth.Failed(item);
            return false;
        }
    }

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
