using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>
/// Operator-approved restoration after the host killed a server without a sealed checkpoint. The normal resume refuses any
/// save older than the observed watermark; this command is the explicit, journaled exception: it archives the evidence it
/// replaces, records the lost ticks and marks the world as excluded from qualification, then resumes the chosen save.
/// </summary>
public static class HostLossRecovery
{
    public const string JournalFile = "host-loss-recoveries.jsonl";
    public const string ExclusionFile = "qualification-excluded.json";

    public static async Task<object> RunAsync(RuntimeSession prior, string save, string reason, string root, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500) throw new ArgumentException("A short operator reason is required.", nameof(reason));
        string directory = prior.Directory;
        string sealedSave = CheckpointStore.SavePath(prior);
        save = Path.GetFullPath(save);
        if (!File.Exists(save) || !Path.GetFullPath(Path.GetDirectoryName(save)!).Equals(Path.GetFullPath(Path.GetDirectoryName(sealedSave)!),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The restored save must be one of this campaign's own saves.", nameof(save));
        bool sealedRestore = save.Equals(Path.GetFullPath(sealedSave), StringComparison.OrdinalIgnoreCase);
        string watermarkPath = Path.Combine(directory, "observation-watermark.json");
        string memoryPath = Path.Combine(directory, "strategic-memory.json");
        string sealPath = CheckpointStore.SealPath(prior);
        var watermark = File.Exists(watermarkPath)
            ? JsonSerializer.Deserialize<SessionGameClient.Watermark>(await File.ReadAllTextAsync(watermarkPath, token), Protocol.Json) : null;
        var seal = File.Exists(sealPath)
            ? JsonSerializer.Deserialize<CheckpointSeal>(await File.ReadAllTextAsync(sealPath, token), Protocol.Json) : null;

        string archive = Path.Combine(directory, $"host-loss-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(archive);
        foreach (string file in new[] { watermarkPath, sealPath, memoryPath, sealedSave })
            if (File.Exists(file)) File.Copy(file, Path.Combine(archive, Path.GetFileName(file)));
        string restoredHash = await CheckpointStore.HashAsync(save, token);
        var record = new
        {
            recordedUtc = DateTime.UtcNow, reason, restoredSave = Path.GetFileName(save), restoredSha256 = restoredHash, sealedRestore,
            previousWatermark = watermark, previousSeal = seal, archive = Path.GetFileName(archive), qualifying = false,
            interpretation = "Host loss: the world returns to an earlier save; the later observed history is lost, never replayed."
        };
        await File.AppendAllTextAsync(Path.Combine(directory, JournalFile), JsonSerializer.Serialize(record, Protocol.Json) + Environment.NewLine, token);
        await LocalJson.WriteAsync(Path.Combine(directory, ExclusionFile), new { qualifying = false, reason = "host-loss-recovery",
            journal = JournalFile }, token);

        if (!sealedRestore)
        {
            // An autosave carries no prepared seal: it is loaded through the conservative legacy inspection.
            File.Copy(save, sealedSave, overwrite: true);
            File.Delete(sealPath);
        }
        File.Delete(watermarkPath);
        var session = await FactorioRuntime.ResumeAsync(prior, root, token, hostLossRecovery: true);

        // The strategic memory may point past the restored tick or at a goal begun in the lost history.
        string run = Path.GetDirectoryName(session.SavePath)!;
        using var loaded = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, "resumed-observation.json"), token));
        long tick = loaded.RootElement.GetProperty("tick").GetInt64();
        ActorScope scope = loaded.RootElement.GetProperty("data").GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!;
        if (File.Exists(memoryPath))
        {
            var memory = JsonSerializer.Deserialize<StrategicMemory>(await File.ReadAllTextAsync(memoryPath, token), Protocol.Json)!;
            string note = $"Host loss recovery: the world was restored to tick {tick}; goals and effects after it were lost and are not replayed.";
            await LocalJson.WriteAsync(memoryPath, memory with
            {
                Scope = scope, Tick = Math.Min(memory.Tick, tick), Pending = false, PendingJournal = null,
                Recovery = null, RecoveryDeathObserved = false, Deferred = null, PreviousResult = note
            }, token);
        }
        // Resource history refuses a world older than its last sighting; the lost sightings are dropped and the world reobserved.
        int droppedSightings = await new ResourceMemoryStore(directory).RestoreToAsync(tick, token);
        foreach (string leftover in new[] { StrategicCampaignController.RestartRequestFile, "restart-bin" })
            File.Delete(Path.Combine(directory, leftover));
        await File.AppendAllTextAsync(Path.Combine(directory, JournalFile), JsonSerializer.Serialize(new
        {
            recordedUtc = DateTime.UtcNow, phase = "resumed", loadedTick = tick, scope, run = Path.GetFileName(run), droppedSightings,
            lostTicks = watermark is null ? (long?)null : watermark.Tick - tick
        }, Protocol.Json) + Environment.NewLine, token);
        return new { session.ManifestPath, session.ServerProcessId, loadedTick = tick, lostTicks = watermark is null ? (long?)null : watermark.Tick - tick, archive };
    }
}
