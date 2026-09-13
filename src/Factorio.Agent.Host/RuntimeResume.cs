using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public static partial class FactorioRuntime
{
    public static async Task<RuntimeSession> ResumeAsync(RuntimeSession prior, string root, CancellationToken token = default)
    {
        using var lease = ActorControlLease.Acquire(prior.Directory);
        await RequireCurrentManifestAsync(prior, token);
        if (IsManagedProcessAlive(prior.ServerProcessId, prior.Executable, prior.ServerStartTimeUtc))
            throw new InvalidOperationException("The recorded server is still running. Reconnect to it instead of restoring a save.");
        CheckpointSeal? seal = await CheckpointStore.VerifyFileAsync(prior, token);
        string watermarkPath = Path.Combine(prior.Directory, "observation-watermark.json");
        var watermark = File.Exists(watermarkPath)
            ? JsonSerializer.Deserialize<SessionGameClient.Watermark>(await File.ReadAllTextAsync(watermarkPath, token), Protocol.Json)
            : null;
        string run = Path.Combine(prior.Directory, "runs", Guid.NewGuid().ToString("N"));
        string install = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(prior.Executable)!, "..", ".."));
        await PrepareProfileAsync(install, run, FindMod(Path.GetFullPath(root)), token, prior.Directory);
        string save = Path.Combine(run, "active.zip");
        File.Copy(CheckpointStore.SavePath(prior), save);
        // Preserve the old manifest as evidence, including its private local credentials.
        await LocalJson.WriteAsync(Path.Combine(run, "previous-session.json"), prior, token);
        string copiedHash = await CheckpointStore.HashAsync(save, token);
        if (copiedHash != await CheckpointStore.HashAsync(CheckpointStore.SavePath(prior), token)
            || (seal is not null && copiedHash != seal.Sha256))
            throw new SessionDivergenceException("The checkpoint changed while preparing its isolated runtime copy.");
        var session = prior with
        {
            Configuration = Path.Combine(run, "config.ini"), ModsDirectory = Path.Combine(run, "mods"), SavePath = save,
            GamePort = FreeUdpPort(), RconPort = FreeTcpPort(), RconPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
            SessionId = Guid.NewGuid().ToString("N")
        };
        string evidence = Path.Combine(run, "resume.json");
        await LocalJson.WriteAsync(evidence, new { phase = "prepared", sourceHash = copiedHash, sealedCheckpoint = seal is not null }, token);
        ProcessStartInfo start = CreateStart(session.Executable, ["--config", session.Configuration, "--mod-directory", session.ModsDirectory,
            "--start-server", save, "--bind", $"127.0.0.1:{session.GamePort}", "--rcon-bind", $"127.0.0.1:{session.RconPort}",
            "--rcon-password", session.RconPassword, "--server-settings", Path.Combine(session.Directory, "server-settings.json")]);
        start.UseShellExecute = true;
        start.WindowStyle = ProcessWindowStyle.Hidden;
        using Process process = Process.Start(start) ?? throw new IOException("Failed to resume Factorio.");
        session = session with { ServerProcessId = process.Id, ServerStartTimeUtc = process.StartTime.ToUniversalTime() };
        // Once a process exists, its identity must survive cancellation or a failed handshake.
        await session.WriteAsync(CancellationToken.None);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        await using var raw = new FactorioGameClient(session.CreateRcon(keepConnectionOpen: true));
        GameResponse before = await ObserveLoadedAsync(session, process, raw, deadline.Token);
        CheckpointStore.VerifyLoaded(before, prior, seal, watermark);
        await LocalJson.WriteAsync(Path.Combine(run, "loaded-observation.json"), before, deadline.Token);
        await LocalJson.WriteAsync(evidence, new { phase = "hello-pending", sourceHash = copiedHash, sealedCheckpoint = seal is not null }, deadline.Token);
        // No retry: on an ambiguous response the manifest identifies the live process for inspection.
        GameResponse hello = await raw.ExecuteAsync(GameRequest.Create("hello", new
        {
            sessionId = session.SessionId, worldId = session.ProposedWorldId, checkpointId = seal?.CheckpointId
        }), deadline.Token);
        if (!hello.Ok) throw new GameRpcException(hello.Error!);
        await using var client = session.CreateClient(lease);
        GameResponse after = await client.ExecuteAsync(GameRequest.Create("observe"), deadline.Token);
        if (!after.Ok) throw new GameRpcException(after.Error!);
        ActorScope beforeScope = before.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!;
        ActorScope afterScope = after.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!;
        if (afterScope.ActorId != beforeScope.ActorId || afterScope.Incarnation != beforeScope.Incarnation
            || afterScope.Generation <= beforeScope.Generation || after.Data.GetProperty("agent").GetProperty("awaitingController").GetBoolean())
            throw new SessionDivergenceException("Resumed actor identity or controller handover was not confirmed.");
        await LocalJson.WriteAsync(Path.Combine(run, "resumed-observation.json"), after, deadline.Token);
        await LocalJson.WriteAsync(evidence, new { phase = "confirmed", sourceHash = copiedHash, sealedCheckpoint = seal is not null,
            beforeScope, afterScope, loadedTick = before.Tick, resumedTick = after.Tick }, deadline.Token);
        return session;
    }

    private static async Task<GameResponse> ObserveLoadedAsync(RuntimeSession session, Process process, IGameClient raw, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (process.HasExited) throw new IOException($"Factorio exited while resuming. Inspect {session.ManifestPath}.");
            try
            {
                // Read-only retries are safe while the TCP listener and script console become ready.
                string probe = await session.CreateRcon().ExecuteAsync("/silent-command rcon.print('factorio_agent_console_ready')", token);
                if (probe.Trim() == "factorio_agent_console_ready")
                    return await raw.ExecuteAsync(GameRequest.Create("observe"), token);
            }
            catch (Exception error) when (error is SocketException or TimeoutException or EndOfStreamException) { }
            await Task.Delay(250, token);
        }
    }

    private static async Task RequireCurrentManifestAsync(RuntimeSession session, CancellationToken token)
    {
        RuntimeSession current = await RuntimeSession.ReadAsync(session.ManifestPath, token);
        if (current != session) throw new SessionDivergenceException("The runtime manifest changed. Read the current session before controlling its lifecycle.");
    }

    private static bool IsManagedProcessAlive(int id, string executable, DateTime? started)
    {
        try
        {
            using Process process = Process.GetProcessById(id);
            // A retired Factorio PID may now belong to a protected system process. Reject its
            // inexpensive name first; never inspect privileged modules for an unrelated process.
            return !process.HasExited
                && string.Equals(process.ProcessName, Path.GetFileNameWithoutExtension(executable), StringComparison.OrdinalIgnoreCase)
                && string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)
                && (started is null || process.StartTime.ToUniversalTime() == started);
        }
        catch (ArgumentException) { return false; } // The recorded PID no longer exists.
    }
}
