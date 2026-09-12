using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record RuntimeSession(string Directory, string Executable, string Configuration,
    string ModsDirectory, string SavePath, int ServerProcessId, int GamePort, int RconPort,
    string RconPassword, string SessionId, string ProposedWorldId, uint Seed, bool IsFixture, DateTime? ServerStartTimeUtc = null)
{
    public string ManifestPath => Path.Combine(Directory, "session.json");
    public RconClient CreateRcon(bool keepConnectionOpen = false) => new(new RconOptions { Port = RconPort, Password = RconPassword,
        MaximumResponseBytes = 8 * 1024 * 1024, KeepConnectionOpen = keepConnectionOpen });
    public SessionGameClient CreateClient(ActorControlLease? controllerLease = null) =>
        new(this, new FactorioGameClient(CreateRcon(keepConnectionOpen: true)), controllerLease);
    public static async Task<RuntimeSession> ReadAsync(string file, CancellationToken token = default) =>
        JsonSerializer.Deserialize<RuntimeSession>(await File.ReadAllTextAsync(file, token), new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidDataException("Session file is empty.");

    public Task WriteAsync(CancellationToken token = default) => LocalJson.WriteAsync(ManifestPath, this, token);

    public async Task<GameResponse> HelloAsync(CancellationToken token = default)
    {
        await using var client = CreateClient();
        GameResponse response = await client.ExecuteAsync(GameRequest.Create("hello", new { sessionId = SessionId, worldId = ProposedWorldId }), token);
        if (!response.Ok) throw new GameRpcException(response.Error!);
        return response;
    }
}

public static partial class FactorioRuntime
{
    public static async Task<RuntimeSession> StartAsync(string root, string? installation, uint seed,
        bool fixture, CancellationToken token = default)
    {
        root = Path.GetFullPath(root);
        string install = Path.GetFullPath(installation ?? Path.Combine(root, "Factorio_2.0.77"));
        string executable = Path.Combine(install, "bin", "x64", "factorio.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Factorio executable not found. Supply --installation.", executable);
        string modSource = FindMod(root);
        string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        string directory = Path.Combine(root, ".runtime", (fixture ? "fixture-" : "campaign-") + id);
        Directory.CreateDirectory(directory);
        string config = Path.Combine(directory, "config.ini");
        string mods = Path.Combine(directory, "mods");
        await PrepareProfileAsync(install, directory, modSource, token);
        string settings = Path.Combine(directory, "server-settings.json");
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(new
        {
            name = "Factorio Agent — " + (fixture ? "qualification fixture" : "autonomous campaign"),
            description = "Local development session", max_players = 4,
            visibility = new { @public = false, lan = false },
            require_user_verification = false, allow_commands = "admins-only",
            auto_pause = false, auto_pause_when_players_connect = false,
            autosave_interval = 5, autosave_slots = 3, only_admins_can_pause_the_game = true
        }), token);
        string save = Path.Combine(directory, "initial.zip");
        string[] common = ["--config", config, "--mod-directory", mods];
        await RunToExitAsync(executable, [.. common, "--create", save, "--map-gen-seed", seed.ToString(CultureInfo.InvariantCulture),
            "--preset", "default"], Path.Combine(directory, "create.log"), token);
        int gamePort = FreeUdpPort();
        int rconPort = FreeTcpPort();
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        ProcessStartInfo start = CreateStart(executable, [.. common, "--start-server", save,
            "--bind", $"127.0.0.1:{gamePort}", "--rcon-bind", $"127.0.0.1:{rconPort}",
            "--rcon-password", password, "--server-settings", settings]);
        // Detached server must not keep the launching CLI's standard streams open.
        start.UseShellExecute = true;
        start.WindowStyle = ProcessWindowStyle.Hidden;
        using Process process = Process.Start(start) ?? throw new IOException("Failed to start Factorio.");
        var session = new RuntimeSession(directory, executable, config, mods, save, process.Id, gamePort, rconPort,
            password, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), seed, fixture, process.StartTime.ToUniversalTime());
        await session.WriteAsync(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        bool consoleReady = false;
        int consoleProbes = 0;
        while (!deadline.IsCancellationRequested)
        {
            if (process.HasExited) throw new IOException($"Factorio exited with code {process.ExitCode}. Inspect {directory}.");
            try
            {
                if (!consoleReady)
                {
                    // A fresh Factorio world can acknowledge the first Lua console command with
                    // an empty response without executing it. Probe with a read-only print,
                    // never by retrying an actor mutation whose outcome is not yet known.
                    string probe = await session.CreateRcon().ExecuteAsync("/silent-command rcon.print('factorio_agent_console_ready')", deadline.Token);
                    await File.AppendAllTextAsync(Path.Combine(directory, "console-startup.jsonl"),
                        JsonSerializer.Serialize(new { attempt = ++consoleProbes, response = probe }) + "\n", deadline.Token);
                    consoleReady = probe.Trim() == "factorio_agent_console_ready";
                    if (!consoleReady)
                    {
                        if (consoleProbes >= 2) throw new InvalidDataException($"Script console not ready. Inspect {session.ManifestPath} and console-startup.jsonl.");
                        continue;
                    }
                }
                await session.HelloAsync(deadline.Token);
                return session;
            }
            catch (Exception error) when (error is SocketException or TimeoutException or EndOfStreamException)
            {
                await Task.Delay(250, deadline.Token);
            }
        }
        throw new TimeoutException($"Server startup not confirmed. Session manifest: {session.ManifestPath}");
    }

    public static async Task<int> ConnectClientAsync(RuntimeSession session, CancellationToken token = default)
    {
        using var lease = ActorControlLease.Acquire(session.Directory);
        await RequireCurrentManifestAsync(session, token);
        await using var client = session.CreateClient(lease);
        GameResponse observed = await client.ExecuteAsync(GameRequest.Create("observe"), token);
        if (!observed.Ok) throw new GameRpcException(observed.Error!);
        JsonElement players = observed.Data.GetProperty("players");
        if (players.ValueKind == JsonValueKind.Array && players.EnumerateArray().Any(player => player.GetProperty("connected").GetBoolean()))
            throw new IOException("A player is already connected to this session.");
        foreach (string profile in Directory.EnumerateDirectories(session.Directory, "client*"))
        {
            string processFile = Path.Combine(profile, "process-id.txt");
            if (File.Exists(processFile) && int.TryParse(await File.ReadAllTextAsync(processFile, token), out int id)
                && IsManagedProcessAlive(id, session.Executable, null))
                throw new IOException("A managed client is still running; inspect it before opening another.");
        }
        string directory = Path.Combine(session.Directory, "client-" + Guid.NewGuid().ToString("N"));
        string install = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(session.Executable)!, "..", ".."));
        string source = Directory.EnumerateDirectories(session.ModsDirectory).Single(path => File.Exists(Path.Combine(path, "info.json")));
        await PrepareProfileAsync(install, directory, source, token);
        await File.WriteAllTextAsync(Path.Combine(directory, "player-data.json"), "{\"service-username\":\"FactorioAgentPilot\"}", token);
        ProcessStartInfo start = CreateStart(session.Executable, ["--config", Path.Combine(directory, "config.ini"),
            "--mod-directory", Path.Combine(directory, "mods"), "--mp-connect", $"127.0.0.1:{session.GamePort}",
            "--window-size", "1280x720", "--force-graphics-preset", "low", "--disable-audio"]);
        start.UseShellExecute = true;
        start.WindowStyle = ProcessWindowStyle.Minimized;
        using Process process = Process.Start(start)
            ?? throw new IOException("Failed to start client.");
        await File.WriteAllTextAsync(Path.Combine(directory, "process-id.txt"), process.Id.ToString(CultureInfo.InvariantCulture), token);
        return process.Id;
    }

    public static async Task<string> StopAsync(RuntimeSession session, CancellationToken token = default)
    {
        using var lease = ActorControlLease.Acquire(session.Directory);
        await RequireCurrentManifestAsync(session, token);
        using Process process = Process.GetProcessById(session.ServerProcessId);
        if (process.HasExited || !string.Equals(process.MainModule?.FileName, session.Executable, StringComparison.OrdinalIgnoreCase)
            || (session.ServerStartTimeUtc is { } expected && process.StartTime.ToUniversalTime() != expected))
            throw new InvalidOperationException("The saved process identity does not match the running Factorio server.");
        await using var client = session.CreateClient(lease);
        GameResponse prepared = await client.ExecuteAsync(GameRequest.Create("prepare_checkpoint", new { checkpointId = Guid.NewGuid().ToString("N") }), token);
        if (!prepared.Ok) throw new GameRpcException(prepared.Error!);
        string checkpointId = prepared.Data.GetProperty("checkpointId").GetString()!;
        long preparedTick = prepared.Data.GetProperty("preparedTick").GetInt64();
        if (!prepared.Data.GetProperty("awaitingController").GetBoolean() || prepared.Tick != preparedTick)
            throw new InvalidDataException("Native checkpoint preparation was not confirmed at a fixed tick.");
        string checkpoint = CheckpointStore.SavePath(session);
        string saveName = "prepared-" + Guid.NewGuid().ToString("N");
        string pending = Path.Combine(session.Directory, "saves", saveName + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(checkpoint)!);
        await session.CreateRcon().ExecuteAsync("/server-save " + saveName, token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (!File.Exists(pending) || new FileInfo(pending).Length == 0)
            await Task.Delay(100, deadline.Token);
        GameResponse frozen = await client.ExecuteAsync(GameRequest.Create("observe"), deadline.Token);
        var seal = new CheckpointSeal(checkpointId, prepared.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!,
            preparedTick, await CheckpointStore.HashAsync(pending, deadline.Token), DateTime.UtcNow);
        CheckpointStore.VerifyLoaded(frozen, session, seal, null);
        File.Move(pending, checkpoint, overwrite: true);
        await LocalJson.WriteAsync(CheckpointStore.SealPath(session), seal, deadline.Token);
        // Native networking teardown can outlast the save itself. Preserve its real
        // process identity and wait; a slow shutdown never authorizes another server.
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        string shutdownRecord = Path.Combine(session.Directory, $"shutdown-{checkpointId}.json");
        await LocalJson.WriteAsync(shutdownRecord, new { phase = "checkpoint-saved", session.ServerProcessId, seal }, deadline.Token);
        try
        {
            await session.CreateRcon().ExecuteAsync("/quit", deadline.Token);
        }
        catch (Exception error) when (error is EndOfStreamException or SocketException or TimeoutException) { }
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException($"Checkpoint saved at {checkpoint}, but shutdown of process {process.Id} is not confirmed. Inspect the existing process before resuming.");
        }
        await LocalJson.WriteAsync(shutdownRecord, new { phase = "process-exited", session.ServerProcessId, seal, confirmedUtc = DateTime.UtcNow }, CancellationToken.None);
        return checkpoint;
    }

    private static string FindMod(string root)
    {
        string parent = Path.Combine(root, "mod");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The Lua mod has not been built yet: mod/ is missing.");
        string[] candidates = Directory.GetFiles(parent, "info.json", SearchOption.AllDirectories);
        if (candidates.Length != 1) throw new IOException("Expected exactly one mod/info.json package.");
        return Path.GetDirectoryName(candidates[0])!;
    }

    private static async Task PrepareProfileAsync(string install, string directory, string modSource, CancellationToken token, string? writeDirectory = null)
    {
        Directory.CreateDirectory(directory);
        // Factorio's /server-save can terminate the server if this folder is absent
        // before its first automatic save.
        Directory.CreateDirectory(Path.Combine(directory, "saves"));
        string mods = Path.Combine(directory, "mods");
        Directory.CreateDirectory(mods);
        string target = Path.Combine(mods, "factorio_agent");
        foreach (string file in Directory.EnumerateFiles(modSource, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(target, Path.GetRelativePath(modSource, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
        }
        await File.WriteAllTextAsync(Path.Combine(mods, "mod-list.json"),
            "{\"mods\":[{\"name\":\"base\",\"enabled\":true},{\"name\":\"factorio_agent\",\"enabled\":true},{\"name\":\"space-age\",\"enabled\":false},{\"name\":\"quality\",\"enabled\":false},{\"name\":\"elevated-rails\",\"enabled\":false}]}", token);
        string config = $"[path]\nread-data={Path.Combine(install, "data").Replace('\\', '/')}\nwrite-data={(writeDirectory ?? directory).Replace('\\', '/')}\n[other]\ncheck-updates=false\n[graphics]\nfull-screen=false\n";
        await File.WriteAllTextAsync(Path.Combine(directory, "config.ini"), config, new UTF8Encoding(false), token);
    }

    private static ProcessStartInfo CreateStart(string executable, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    private static async Task RunToExitAsync(string executable, IEnumerable<string> args, string log, CancellationToken token)
    {
        ProcessStartInfo start = CreateStart(executable, args);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using Process process = Process.Start(start) ?? throw new IOException("Failed to launch Factorio.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            throw;
        }
        await File.WriteAllTextAsync(log, await stdout + await stderr, token);
        if (process.ExitCode != 0) throw new IOException($"Factorio exited with code {process.ExitCode}; inspect {log}.");
    }

    private static int FreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static int FreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }
}
