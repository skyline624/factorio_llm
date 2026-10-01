using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Detected attacks of one world, newest last and bounded, with the memory that deduplicates evidence.</summary>
public sealed record AttackLogState(int Version, string WorldId, IReadOnlyList<AttackRecord> Records, AttackMemory Memory);

/// <summary>Durable attack evidence beside the factory registry: detections inside goals are answered between goals.</summary>
public sealed class AttackLog(string directory)
{
    public const int MaximumRecords = 32;
    public string Path => System.IO.Path.Combine(directory, "attack-log.json");

    public async Task<AttackLogState> LoadAsync(string worldId, CancellationToken token)
    {
        if (!File.Exists(Path)) return new(1, worldId, [], AttackMemory.Empty);
        var state = JsonSerializer.Deserialize<AttackLogState>(await File.ReadAllTextAsync(Path, token), Protocol.Json)
            ?? throw new InvalidDataException("Empty attack log.");
        if (state.Version != 1 || state.WorldId != worldId) throw new InvalidDataException("The attack log belongs to another world.");
        return state;
    }

    public Task SaveAsync(AttackLogState state, CancellationToken token) =>
        LocalJson.WriteAsync(Path, state with { Records = state.Records.TakeLast(MaximumRecords).ToArray() }, token);
}

/// <summary>
/// Detects attacks on known own industry from the registry, the native factory photograph (destroyed registered entities and
/// own entities below their native maximum health), enemy units the actor normally sees and the defense reflex fights, then
/// journals and persists one record per attacked cluster. No observer beyond the actor's normal visibility is used.
/// </summary>
public sealed class AttackMonitor(string directory, IControllerJournal journal, ReflexEventLog? reflexes = null)
{
    public async Task<IReadOnlyList<AttackRecord>> ObserveAsync(FactoryState state, FactorySnapshot snapshot, IReadOnlyList<VisibleThreat> enemies,
        CancellationToken token)
    {
        var log = new AttackLog(directory);
        var current = await log.LoadAsync(snapshot.Scope.WorldId, token);
        var evidence = new AttackEvidence(Destroyed(state, snapshot), Damaged(snapshot), enemies,
            (reflexes ?? ReflexEventLog.Shared).Since(current.Memory.ReflexTick));
        var (records, memory) = AttackDetector.Detect(snapshot.CollectedTick, IndustryClusters.Read(state, snapshot), evidence, current.Memory);
        await log.SaveAsync(current with { Records = [.. current.Records, .. records], Memory = memory }, token);
        foreach (var record in records) await journal.AppendAsync("attack-detected", record, token);
        return records;
    }

    /// <summary>
    /// Detection inside a maintenance round, which rebuilds destroyed entities right after: the evidence would otherwise be lost
    /// before the between-goals response. It never fails the round it serves.
    /// </summary>
    public async Task RecordQuietlyAsync(FactoryState state, FactorySnapshot snapshot, CancellationToken token)
    {
        try { await ObserveAsync(state, snapshot, [], token); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Detection only reads and records evidence; a fault in it must not stop rebuilding and rearming.
            await journal.AppendAsync("attack-monitor-error", new { error = error.GetType().Name, error.Message }, token);
        }
    }

    /// <summary>Registered entities of ready cells missing from the photograph, at their planned positions.</summary>
    internal static IReadOnlyList<DestroyedEntity> Destroyed(FactoryState state, FactorySnapshot snapshot) =>
        FactoryMaintenance.Missing(state, FactoryMaintenance.Present(snapshot)).Select(m => new DestroyedEntity(m.PreviousId, m.Plan.Position)).ToArray();

    /// <summary>Known own entities below their native maximum health.</summary>
    internal static IReadOnlyList<DamagedEntity> Damaged(FactorySnapshot snapshot) => snapshot.Records
        .Where(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "factory"
            && r.Data.TryGetProperty("health", out var health) && health.ValueKind == JsonValueKind.Number
            && r.Data.TryGetProperty("maxHealth", out var maximum) && maximum.ValueKind == JsonValueKind.Number
            && health.GetDouble() < maximum.GetDouble())
        .Select(r => new DamagedEntity(r.EntityId, r.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!,
            r.Data.GetProperty("health").GetDouble(), r.Data.GetProperty("maxHealth").GetDouble())).ToArray();
}
