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
    public RconClient CreateRcon() => new(new RconOptions { Port = RconPort, Password = RconPassword, MaximumResponseBytes = 8 * 1024 * 1024 });
    public IGameClient CreateClient() => new SessionGameClient(this, new FactorioGameClient(CreateRcon()));
    public static async Task<RuntimeSession> ReadAsync(string file, CancellationToken token = default) =>
        JsonSerializer.Deserialize<RuntimeSession>(await File.ReadAllTextAsync(file, token), new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidDataException("Session file is empty.");

    public async Task WriteAsync(CancellationToken token = default)
    {
        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        await File.WriteAllTextAsync(ManifestPath, json, new UTF8Encoding(false), token);
    }

    public async Task<GameResponse> HelloAsync(CancellationToken token = default)
    {
        GameResponse response = await CreateClient().ExecuteAsync(GameRequest.Create("hello", new { sessionId = SessionId, worldId = ProposedWorldId }), token);
        if (!response.Ok) throw new GameRpcException(response.Error!);
        return response;
    }
}

public static class FactorioRuntime
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
        string directory = Path.Combine(session.Directory, "client");
        if (Directory.Exists(directory)) throw new IOException("A client profile already exists for this session; inspect its process before opening another.");
        string install = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(session.Executable)!, "..", ".."));
        string source = Directory.EnumerateDirectories(session.ModsDirectory).Single(path => File.Exists(Path.Combine(path, "info.json")));
        await PrepareProfileAsync(install, directory, source, token);
        await File.WriteAllTextAsync(Path.Combine(directory, "player-data.json"), "{\"service-username\":\"FactorioAgentPilot\"}", token);
        ProcessStartInfo start = CreateStart(session.Executable, ["--config", Path.Combine(directory, "config.ini"),
            "--mod-directory", Path.Combine(directory, "mods"), "--mp-connect", $"127.0.0.1:{session.GamePort}",
            "--window-size", "1280x720", "--force-graphics-preset", "low", "--disable-audio"]);
        start.UseShellExecute = true;
        start.WindowStyle = ProcessWindowStyle.Normal;
        using Process process = Process.Start(start)
            ?? throw new IOException("Failed to start client.");
        await File.WriteAllTextAsync(Path.Combine(directory, "process-id.txt"), process.Id.ToString(CultureInfo.InvariantCulture), token);
        return process.Id;
    }

    public static async Task<string> StopAsync(RuntimeSession session, CancellationToken token = default)
    {
        using Process process = Process.GetProcessById(session.ServerProcessId);
        if (process.HasExited || !string.Equals(process.MainModule?.FileName, session.Executable, StringComparison.OrdinalIgnoreCase)
            || (session.ServerStartTimeUtc is { } expected && process.StartTime.ToUniversalTime() != expected))
            throw new InvalidOperationException("The saved process identity does not match the running Factorio server.");
        string checkpoint = Path.Combine(session.Directory, "saves", "agent-checkpoint.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(checkpoint)!);
        DateTime requested = DateTime.UtcNow;
        await session.CreateRcon().ExecuteAsync("/server-save agent-checkpoint", token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (!File.Exists(checkpoint) || new FileInfo(checkpoint).Length == 0 || File.GetLastWriteTimeUtc(checkpoint) < requested.AddSeconds(-1))
            await Task.Delay(100, deadline.Token);
        try
        {
            await session.CreateRcon().ExecuteAsync("/quit", deadline.Token);
        }
        catch (Exception error) when (error is EndOfStreamException or SocketException or TimeoutException) { }
        await process.WaitForExitAsync(deadline.Token);
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

    private static async Task PrepareProfileAsync(string install, string directory, string modSource, CancellationToken token)
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
        string config = $"[path]\nread-data={Path.Combine(install, "data").Replace('\\', '/')}\nwrite-data={directory.Replace('\\', '/')}\n[other]\ncheck-updates=false\n[graphics]\nfull-screen=false\n";
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
