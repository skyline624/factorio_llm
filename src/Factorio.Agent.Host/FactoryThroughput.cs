using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record ThroughputSample(long Tick, IReadOnlyDictionary<string, long> ProductsFinished);
/// <summary>Measured crafts of one recipe between two observations against the machines' nominal speed, with their native states.</summary>
public sealed record RecipeThroughput(string Recipe, int Machines, double NominalCraftsPerMinute, double MeasuredCraftsPerMinute,
    double Utilization, IReadOnlyDictionary<string, int> States);
public sealed record ThroughputReport(long FromTick, long ToTick, IReadOnlyList<RecipeThroughput> Recipes,
    IReadOnlyDictionary<string, double> ItemsPerMinute, IReadOnlyDictionary<string, double> Targets, IReadOnlyDictionary<string, int> LabStates,
    int Working, int Machines);
public sealed record ThroughputState(int Version, string WorldId, ThroughputSample Sample, ThroughputReport? Report);

/// <summary>
/// Native throughput of the own factory: each crafting machine's finished-product counter between two factory photographs, its
/// recipe products and its native state. On 2026-10-08 the three normal campaigns made no chemical science pack in ten minutes:
/// 34 to 52 assemblers waited for ingredients and 10 to 24 furnaces for fuel between the actor's hand deliveries. Planned rates
/// are capacity; this report is what the factory actually made.
/// </summary>
public sealed class FactoryThroughput(IGameClient game, IControllerJournal journal, string directory)
{
    private string StatePath => Path.Combine(directory, "factory-throughput.json");

    public async Task<ThroughputReport?> MeasureAsync(CancellationToken token)
    {
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Throughput photograph belongs to another actor scope.");
        var state = await new FactoryRegistry(directory).LoadAsync(catalog.Scope.WorldId, token);
        var previous = await LoadAsync(catalog.Scope.WorldId, token);
        var (sample, report) = Measure(snapshot, previous?.Sample, catalog, state.Targets ?? new Dictionary<string, double>());
        await LocalJson.WriteAsync(StatePath, new ThroughputState(1, catalog.Scope.WorldId, sample, report ?? previous?.Report), token);
        if (report is not null) await journal.AppendAsync("factory-throughput", report, token);
        return report;
    }

    /// <summary>The latest measured report of this world, if any.</summary>
    public async Task<ThroughputReport?> LatestAsync(string worldId, CancellationToken token) => (await LoadAsync(worldId, token))?.Report;

    internal static (ThroughputSample Sample, ThroughputReport? Report) Measure(FactorySnapshot snapshot, ThroughputSample? previous,
        ProductionCatalog catalog, IReadOnlyDictionary<string, double> targets)
    {
        var works = snapshot.Records.Where(r => r.Kind == "work" && r.Name == "machine-craft").ToArray();
        var finished = works.Where(r => r.Data.TryGetProperty("productsFinished", out var p) && p.ValueKind == JsonValueKind.Number)
            .ToDictionary(r => r.EntityId, r => r.Data.GetProperty("productsFinished").GetInt64(), StringComparer.Ordinal);
        var sample = new ThroughputSample(snapshot.CollectedTick, finished);
        if (previous is null || snapshot.CollectedTick <= previous.Tick) return (sample, null);
        double minutes = (snapshot.CollectedTick - previous.Tick) / 3600.0;
        var recipes = new List<RecipeThroughput>();
        var items = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var group in works.Where(r => Text(r, "recipe") is not null).GroupBy(r => Text(r, "recipe")!, StringComparer.Ordinal))
        {
            var recipe = catalog.Recipes.FirstOrDefault(r => r.Name == group.Key);
            double nominal = 0, crafts = 0;
            foreach (var work in group)
            {
                if (recipe is not null && recipe.EnergySeconds > 0 && work.Data.TryGetProperty("craftingSpeed", out var speed)
                    && speed.ValueKind == JsonValueKind.Number)
                    nominal += 60 * speed.GetDouble() / recipe.EnergySeconds;
                // A machine built or rebuilt since the last photograph has no comparable counter.
                if (finished.TryGetValue(work.EntityId, out long now) && previous.ProductsFinished.TryGetValue(work.EntityId, out long before)
                    && now >= before)
                    crafts += now - before;
            }
            double perMinute = crafts / minutes;
            foreach (var product in recipe?.Products.Where(p => p.DeterministicItem) ?? [])
                items[product.Name] = items.GetValueOrDefault(product.Name) + perMinute * product.Amount!.Value;
            recipes.Add(new(group.Key, group.Count(), Math.Round(nominal, 2), Math.Round(perMinute, 2),
                nominal > 0 ? Math.Round(perMinute / nominal, 3) : 0, States(group)));
        }
        var labs = States(snapshot.Records.Where(r => r.Kind == "work" && r.Name == "lab-research"));
        int working = works.Count(r => Text(r, "statusName") == "working");
        var report = new ThroughputReport(previous.Tick, snapshot.CollectedTick,
            recipes.OrderByDescending(r => r.Machines).ThenBy(r => r.Recipe, StringComparer.Ordinal).ToArray(),
            items.Where(p => p.Value > 0).OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => Math.Round(p.Value, 2), StringComparer.Ordinal),
            targets, labs, working, works.Length);
        return (sample, report);

        static Dictionary<string, int> States(IEnumerable<FactoryRecord> records) => records
            .GroupBy(r => Text(r, "statusName") ?? "unknown", StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    }

    private static string? Text(FactoryRecord record, string property) =>
        record.Data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private async Task<ThroughputState?> LoadAsync(string worldId, CancellationToken token)
    {
        if (!File.Exists(StatePath)) return null;
        var state = JsonSerializer.Deserialize<ThroughputState>(await File.ReadAllTextAsync(StatePath, token), Protocol.Json);
        return state is { Version: 1 } && state.WorldId == worldId ? state : null;
    }
}
