using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// One factory radar on a fed electric network, built when a needed resource is neither remembered nor charted, so that the force
/// charts sectors over time instead of the actor walking blind into enemy ground. It is a factory cell: maintenance rebuilds it at
/// its plan and reports it when its network loses its generator. A single radar bounds power and construction; its long-range scan
/// (14 chunks in base 2.0.77) already fills the default map reading.
/// </summary>
public sealed class RadarController(IGameClient game, IControllerJournal journal, string directory)
{
    public const string Kind = "radar";
    /// <summary>Thirty-second waits between map readings, ten game minutes at most: a base radar scans a sector in about 34 s.</summary>
    public const int WaitTicks = 1800, WaitRounds = 20;
    /// <summary>Consecutive readings without a newly read chunk after which the radar is taken to chart nothing more for now.</summary>
    public const int StalledRounds = 3;

    /// <summary>The standing radar cell, powered from a fed network, built first when none stands; null when no radar is possible yet.</summary>
    public async Task<FactoryCell?> EnsureAsync(ProductionCatalog catalog, SpatialController controller, CancellationToken token)
    {
        var registry = new FactoryRegistry(directory);
        var cell = (await registry.LoadAsync(catalog.Scope.WorldId, token)).Cells.FirstOrDefault(c => c.Kind == Kind && c.Status == "ready");
        var known = await KnownAsync(catalog, token);
        if (cell?.Entities.GetValueOrDefault("machine") is { } standing && known.Records.Any(r => r.Kind == "entity" && r.EntityId == standing))
        {
            if (FactoryPower.IsFed(known, standing) != true) await new PowerGridController(game, journal).ConnectAsync(standing, catalog, controller, token);
            return cell;
        }
        string? item = catalog.Items.Where(p => p.Value.PlaceEntityType == "radar").Select(p => p.Key).Order(StringComparer.Ordinal).FirstOrDefault();
        var actor = known.Records.Single(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "actor");
        MapPosition? fed = CellPowerLinker.NearestFedPole(known, actor.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!);
        string? reason = item is null ? "no-radar-prototype"
            : FactoryLogistics.Carried(known).GetValueOrDefault(item) < 1 && !FactoryDirector.Enabled(catalog, item) ? "radar-not-researched"
            : fed is null ? "no-fed-network" : null;
        if (reason is not null)
        {
            await journal.AppendAsync("radar-unavailable", new { reason, item, destroyed = cell?.Entities, known.CollectedTick }, token);
            return null;
        }
        // Built beside the fed pole nearest the actor, the radar joins that network; the grid code extends it otherwise.
        var standingBefore = FactoryMaintenance.Present(known);
        await controller.TravelAsync(fed!, 8, catalog, token);
        string radarId = await new PoweredMachineController(game, journal).InstallAsync(item!, catalog, controller, token);
        await new PowerGridController(game, journal).ConnectAsync(radarId, catalog, controller, token);
        known = await KnownAsync(catalog, token);
        if (FactoryPower.IsFed(known, radarId) != true) throw new InvalidOperationException("The built radar did not join a network with a generator.");
        var record = known.Records.Single(r => r.Kind == "entity" && r.EntityId == radarId);
        var entities = new Dictionary<string, string>(StringComparer.Ordinal) { ["machine"] = radarId };
        var plan = new Dictionary<string, PlannedEntity>(StringComparer.Ordinal) { ["machine"] = Planned("machine", item!, record) };
        // Poles built for the radar are its power links: maintenance rebuilds them with it.
        foreach (var pole in known.Records.Where(r => r.Kind == "entity" && !standingBefore.Contains(r.EntityId)
                && r.Data.GetProperty("role").GetString() == "factory" && r.Data.GetProperty("type").GetString() == "electric-pole")
            .OrderBy(r => r.EntityId, StringComparer.Ordinal))
        {
            string role = $"link-{entities.Count}";
            string? poleItem = catalog.Items.Where(p => p.Value.PlaceEntity == pole.Name).Select(p => p.Key).Order(StringComparer.Ordinal).FirstOrDefault();
            if (poleItem is null) continue;
            entities[role] = pole.EntityId;
            plan[role] = Planned(role, poleItem, pole);
        }
        // A destroyed radar keeps its cell identity: still one radar, now at the new plan.
        var built = new FactoryCell(cell?.Id ?? $"radar-{Guid.NewGuid():N}", 0, new(0, 0, true), Kind, item!, null,
            entities, "ready", known.CollectedTick, Plan: plan);
        await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).With(built), token);
        await journal.AppendAsync("radar-built", new { cell = built.Id, radarId, built.Entities, plan,
            network = record.Data.GetProperty("electricNetworkId").GetInt64(), replaced = cell?.Entities, known.CollectedTick }, token);
        return built;

        static PlannedEntity Planned(string role, string item, FactoryRecord entity) => new(role, item,
            entity.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!, entity.Data.GetProperty("direction").GetInt32());
    }

    /// <summary>
    /// Ensures the radar, then reads the map every <see cref="WaitTicks"/> until a chunk shows one of the resources where the
    /// reading taken before showed none, the radar stops adding chunks, or the wait budget ends. Returns that reading, else null.
    /// Each wait starts with the radar powered: in a fixture the steam boiler ran dry and the stalled scan ended the wait early.
    /// </summary>
    internal async Task<ChartedResourceSnapshot?> ChartAsync(IReadOnlyList<string> names, ChartedResourceSnapshot? before,
        ChartedResourceSurvey survey, ProductionCatalog catalog, SpatialController controller, CancellationToken token)
    {
        var radar = await EnsureAsync(catalog, controller, token);
        if (radar is null) return null;
        string radarId = radar.Entities["machine"];
        var view = await new SpatialClient(game).CaptureAsync([radar.MachineItem], 48, token);
        if (view.Scope != catalog.Scope) throw new InvalidDataException("Actor identity changed while waiting for the radar.");
        double perTick = view.Prototypes[view.Items[radar.MachineItem].EntityName].EnergyPerTick ?? 0;
        var power = new PoweredMachineController(game, journal);
        var shown = (before?.Deposits ?? []).Select(d => (d.Name, d.Chunk)).ToHashSet();
        int read = -1, stalled = 0;
        for (int round = 0; ; round++)
        {
            var charted = await survey.ReadAsync(names, "radar-charting", token);
            if (charted is null) return null;
            if (charted.Deposits.Any(d => !shown.Contains((d.Name, d.Chunk)))) return charted;
            stalled = charted.Coverage.ReadChunks > read ? 0 : stalled + 1;
            read = charted.Coverage.ReadChunks;
            if (stalled >= StalledRounds || round >= WaitRounds)
            {
                await journal.AppendAsync("radar-charting-ended", new { cell = radar.Id, names, rounds = round, readChunks = read, stalled,
                    charted.CollectedTick }, token);
                return null;
            }
            // An unpowered radar gets fuel for the rest of the wait through the existing boiler maintenance; a powered one is left as is.
            await power.MaintainFuelAsync(radarId, perTick * WaitTicks * (WaitRounds - round), catalog, controller, false, token);
            await journal.AppendAsync("radar-charting-wait", new { cell = radar.Id, radar = radarId, round, readChunks = read,
                charted.CollectedTick, ticks = WaitTicks }, token);
            var waited = await controller.WorkAsync("wait", new { ticks = WaitTicks }, WaitTicks + 300, token: token);
            if (waited.Status != "completed") throw new InvalidOperationException("The radar charting wait did not complete; reconcile native effects.");
        }
    }

    private async Task<FactorySnapshot> KnownAsync(ProductionCatalog catalog, CancellationToken token)
    {
        var known = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        if (known.Scope != catalog.Scope) throw new InvalidDataException("Actor identity changed while placing the radar.");
        return known;
    }
}
