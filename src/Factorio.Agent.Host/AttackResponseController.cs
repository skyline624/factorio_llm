using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Outcome: turrets-deployed, ring-complete (maintenance rearms it), no-turret, no-stock, skipped or failed.</summary>
public sealed record ClusterResponse(string Cluster, int Attacks, int NestsBuilt, int Nests, int TurretsReady, bool Walls, string Outcome);
public sealed record AttackResponseResult(string Status, IReadOnlyList<AttackRecord> Detected, SurvivalKitResult? Kit,
    IReadOnlyList<ClusterResponse> Responses, long Tick);

/// <summary>
/// Between goals: detects attacks on own industry clusters, equips the actor, then deploys turret nests on the attacked
/// clusters, those covering the attacked points first and at most four per response, from turrets and magazines the stock can
/// already make. Walls follow only when the stock also covers them: a missing wall technology never delays turrets. Nests are
/// registered turret and wall cells, so maintenance rebuilds and rearms them. On 2026-10-01 (seed 20261002, run 16) the actor
/// died three times inside the base where 3 turrets covered 61 of 151 industrial anchors, and perimeter goals were refused
/// because stone walls were not researched. Nothing starts while enemies attack the actor; the reflex fights first.
/// </summary>
public sealed class AttackResponseController(IGameClient game, IControllerJournal journal, string directory)
{
    public const int MaximumNestsPerAttack = 4;
    public const int MaximumClustersPerRound = 2;
    public const int MaximumAttempts = 3;
    /// <summary>Walls one two-layer nest needs at most for a side; corner nests share theirs. Only gates affordability.</summary>
    public const int WallsPerNest = 12;

    /// <summary>Detection only: new attack records from the registry, the native photograph, visible enemy units and reflex fights.</summary>
    public async Task<IReadOnlyList<AttackRecord>> DetectAsync(CancellationToken token)
    {
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        return (await DetectAsync(catalog, token)).Detected;
    }

    private async Task<(IReadOnlyList<AttackRecord> Detected, FactoryState Registry, FactorySnapshot Snapshot)> DetectAsync(ProductionCatalog catalog,
        CancellationToken token)
    {
        var registry = await new FactoryRegistry(directory).LoadAsync(catalog.Scope.WorldId, token);
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Actor identity changed before attack detection.");
        return (await new AttackMonitor(directory, journal).ObserveAsync(registry, snapshot, await EnemyUnitsAsync(token), token), registry, snapshot);
    }

    public async Task<AttackResponseResult> RunAsync(CancellationToken token)
    {
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var safety = await SafetyAsync(token);
        if (!safety.Alive || safety.ControlMode != "ai") return await DoneAsync("unavailable", [], null, [], safety.Tick, token);
        var (detected, registry, snapshot) = await DetectAsync(catalog, token);
        if (safety.Enemies.Count > 0) return await DoneAsync("deferred-enemies-near-actor", detected, null, [], safety.Tick, token);
        // Production here must neither reuse nor empty persistent cells, exactly as inside a strategic goal.
        using var reserved = ProductionReservations.EnterFactory(registry);
        var kit = await new SurvivalKitController(game, journal).EnsureAsync("attack-response", token);
        var clusters = IndustryClusters.Read(registry, snapshot);
        var open = (await new AttackLog(directory).LoadAsync(catalog.Scope.WorldId, token)).Records
            .Where(r => !r.Responded && r.Attempts < MaximumAttempts)
            .Select(r => (Record: r, Cluster: Locate(clusters, r))).Where(p => p.Cluster is not null)
            .GroupBy(p => p.Cluster!.Id, StringComparer.Ordinal).OrderByDescending(g => g.Max(p => p.Record.Tick))
            .Take(MaximumClustersPerRound).ToArray();
        var responses = new List<ClusterResponse>();
        foreach (var group in open)
        {
            if ((await SafetyAsync(token)).Enemies.Count > 0) break;
            var records = group.Select(p => p.Record).ToArray();
            var response = await RespondAsync(catalog, group.First().Cluster!, records, token);
            responses.Add(response);
            await MarkAsync(catalog, records, response, token);
        }
        return await DoneAsync("complete", detected, kit, responses, (await SafetyAsync(token)).Tick, token);
    }

    private async Task<ClusterResponse> RespondAsync(ProductionCatalog catalog, IndustryCluster cluster, IReadOnlyList<AttackRecord> records,
        CancellationToken token)
    {
        var stock = SurvivalKitController.Stock(await new ProductionController(game, journal).ObserveAsync(token));
        string? turret = catalog.Turrets?.Keys.OrderByDescending(k => stock.GetValueOrDefault(k) > 0)
            .ThenByDescending(k => FactoryDirector.Enabled(catalog, k)).ThenBy(k => k, StringComparer.Ordinal).FirstOrDefault();
        string? wall = catalog.Items.Where(p => p.Value.PlaceEntityType == "wall").Select(p => p.Key).Order(StringComparer.Ordinal).FirstOrDefault();
        if (turret is null || wall is null || stock.GetValueOrDefault(turret) == 0 && !FactoryDirector.Enabled(catalog, turret))
            return await RefusedAsync("no-turret");
        string ammunition;
        try { ammunition = DefenseDeploymentPlanner.ChooseAmmunition(catalog.Turrets![turret], catalog, stock); }
        catch (InvalidOperationException) { return await RefusedAsync("no-ammunition"); }
        int magazines = FactoryMaintenance.Magazines(0, catalog.Items[ammunition].MagazineSize!.Value);
        int nests = Affordable(catalog, turret, ammunition, magazines, stock, MaximumNestsPerAttack);
        if (nests == 0) return await RefusedAsync("no-stock");
        bool walls = SurvivalKitPlanner.Shortfall(catalog, new Dictionary<string, long>
        {
            [turret] = nests, [ammunition] = (long)nests * magazines, [wall] = (long)nests * WallsPerNest
        }, stock).Count == 0;
        var points = records.SelectMany(r => r.Points).Distinct().ToArray();
        await journal.AppendAsync("attack-response-plan", new { cluster = cluster.Id, attacks = records.Count, turret, ammunition, nests, walls }, token);
        try
        {
            var ring = await new PerimeterDefenseController(game, journal, directory)
                .RunClusterAsync(cluster, points, nests, wall, walls, turret, token: token);
            string outcome = ring.Skipped is not null ? "skipped" : ring.NestsBuilt > 0 ? "turrets-deployed" : ring.AlreadyComplete ? "ring-complete" : "no-stock";
            return new(cluster.Id, records.Count, ring.NestsBuilt, ring.Nests, ring.TurretsReady, walls, outcome);
        }
        catch (Exception error) when (FactoryResearchController.Recoverable(error, token))
        {
            await journal.AppendAsync("attack-response-failed", new { cluster = cluster.Id, error = error.GetType().Name, error.Message }, token);
            return new(cluster.Id, records.Count, 0, 0, 0, walls, "failed");
        }

        async Task<ClusterResponse> RefusedAsync(string outcome)
        {
            await journal.AppendAsync("attack-response-unavailable", new { cluster = cluster.Id, outcome, turret }, token);
            return new(cluster.Id, records.Count, 0, 0, 0, false, outcome);
        }
    }

    /// <summary>A deployed or already complete ring answers its attacks; anything else is retried at most three times.</summary>
    private async Task MarkAsync(ProductionCatalog catalog, IReadOnlyList<AttackRecord> answered, ClusterResponse response, CancellationToken token)
    {
        var log = new AttackLog(directory);
        var state = await log.LoadAsync(catalog.Scope.WorldId, token);
        bool done = response.Outcome is "turrets-deployed" or "ring-complete";
        await log.SaveAsync(state with
        {
            Records = state.Records.Select(r => answered.Any(a => a.Tick == r.Tick && a.Cluster == r.Cluster)
                ? r with { Responded = done, NestsAdded = r.NestsAdded + response.NestsBuilt, Attempts = r.Attempts + 1, Outcome = response.Outcome }
                : r).ToArray()
        }, token);
    }

    private async Task<AttackResponseResult> DoneAsync(string status, IReadOnlyList<AttackRecord> detected, SurvivalKitResult? kit,
        IReadOnlyList<ClusterResponse> responses, long tick, CancellationToken token)
    {
        var result = new AttackResponseResult(status, detected, kit, responses, tick);
        await journal.AppendAsync("attack-response", result, token);
        return result;
    }

    /// <summary>The cluster that holds an attack: by name, else by its attacked points after growth renamed it.</summary>
    public static IndustryCluster? Locate(IReadOnlyList<IndustryCluster> clusters, AttackRecord record) =>
        clusters.FirstOrDefault(c => c.Id == record.Cluster) ?? clusters
            .Select(c => (Cluster: c, Distance: record.Points.Count == 0 ? double.PositiveInfinity : record.Points.Min(p => IndustryClusterPlanner.Distance(c.Box, p))))
            .Where(p => p.Distance <= AttackDetector.NearIndustry).OrderBy(p => p.Distance).ThenBy(p => p.Cluster.Id, StringComparer.Ordinal)
            .Select(p => p.Cluster).FirstOrDefault();

    /// <summary>Most nests, up to the maximum, whose turret and full magazine reserve the stock can make without mining.</summary>
    public static int Affordable(ProductionCatalog catalog, string turret, string ammunition, int magazinesPerNest,
        IReadOnlyDictionary<string, long> stock, int maximum)
    {
        for (int nests = maximum; nests > 0; nests--)
            if (SurvivalKitPlanner.Shortfall(catalog, new Dictionary<string, long> { [turret] = nests, [ammunition] = (long)nests * magazinesPerNest }, stock).Count == 0)
                return nests;
        return 0;
    }

    private async Task<SafetyObservation> SafetyAsync(CancellationToken token) =>
        SafetyObservation.Parse(await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 32, limit = 200, entityLimit = 1 }), token));

    /// <summary>Enemy units the actor normally sees within 64 tiles; spawners and worms are standing threats, not attacks.</summary>
    private async Task<IReadOnlyList<VisibleThreat>> EnemyUnitsAsync(CancellationToken token)
    {
        var response = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 64, limit = 200, entityLimit = 1 }), token);
        if (!response.Ok) throw new GameRpcException(response.Error!);
        var enemies = response.Data.GetProperty("enemies");
        return enemies.ValueKind != JsonValueKind.Array ? [] : enemies.EnumerateArray()
            .Where(e => e.TryGetProperty("type", out var type) && type.GetString() == "unit")
            .Select(e => new VisibleThreat(e.GetProperty("id").GetString()!, e.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!)).ToArray();
    }
}
