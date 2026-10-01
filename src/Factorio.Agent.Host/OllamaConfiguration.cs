using System.Text.Json;
using System.Text.Json.Serialization;
using Factorio.Agent.Ollama;
using Factorio.Agent.Codex;

namespace Factorio.Agent.Host;

/// <summary>Loads private connection settings before opening a game session. Never logs the document.</summary>
public static class OllamaConfiguration
{
    public const string DefaultPath = "config/appsettings.local.json";

    /// <summary>The private profile belongs to the repository, not to whichever directory launched the host.</summary>
    public static string ResolveDefaultPath(string? searchFrom = null)
    {
        for (var directory = new DirectoryInfo(searchFrom ?? AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Factorio.Agent.sln")))
                return Path.Combine(directory.FullName, DefaultPath.Replace('/', Path.DirectorySeparatorChar));
        return Path.GetFullPath(DefaultPath);
    }

    public static async Task<OllamaOptions> LoadAsync(string? path, bool directCloud, string? environmentApiKey,
        CancellationToken token = default, string? searchFrom = null)
    {
        bool explicitPath = path is not null;
        path ??= ResolveDefaultPath(searchFrom);
        var settings = (await ReadAsync(path, explicitPath, token))?.Ollama
            ?? (explicitPath || File.Exists(path) ? throw new InvalidDataException("The configuration requires an Ollama section.") : new Settings());
        if (settings.Stream) throw new InvalidDataException("The strategic planner requires Stream=false.");
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var baseUrl))
            throw new InvalidDataException("Ollama BaseUrl must be an absolute URI.");
        if (directCloud) baseUrl = OllamaOptions.CloudBaseUrl;
        return new OllamaOptions
        {
            BaseUrl = baseUrl,
            ApiKey = baseUrl == OllamaOptions.CloudBaseUrl
                ? !string.IsNullOrWhiteSpace(settings.ApiKey) ? settings.ApiKey : environmentApiKey
                : null,
            Model = settings.Model,
            ThinkingEffort = settings.ThinkingEffort,
            MaxAttempts = 1
        };
    }

    /// <summary>
    /// The optional typed decision model runs only when its own section enables it, and only in shadow mode:
    /// it is never a substitute for the strategic model and never selects actions.
    /// </summary>
    public static async Task<DecisionModelOptions?> LoadDecisionAsync(string? path, CancellationToken token = default, string? searchFrom = null)
    {
        bool explicitPath = path is not null;
        path ??= ResolveDefaultPath(searchFrom);
        var decision = (await ReadAsync(path, explicitPath, token))?.DecisionModel;
        if (decision is not { Enabled: true }) return null;
        if (decision.Mode != "shadow") throw new InvalidDataException("The decision model supports only Mode=shadow.");
        if (!Uri.TryCreate(decision.BaseUrl, UriKind.Absolute, out var baseUrl) || !baseUrl.IsLoopback || baseUrl.Scheme != "http")
            throw new InvalidDataException("The decision model must be a local HTTP Ollama.");
        return new DecisionModelOptions { BaseUrl = baseUrl, Model = decision.Model };
    }

    public static async Task<string> LoadPlannerTransportAsync(string? path, CancellationToken token = default)
    {
        var profile = await ReadAsync(path ?? ResolveDefaultPath(), path is not null, token);
        string transport = profile?.Planner?.Transport ?? "ollama";
        if (transport is not ("ollama" or "codex-chatgpt"))
            throw new InvalidDataException("Planner.Transport must be ollama or codex-chatgpt.");
        return transport;
    }

    public static async Task<CodexOptions> LoadCodexAsync(string? path, CancellationToken token = default)
    {
        var profile = await ReadAsync(path ?? ResolveDefaultPath(), path is not null, token);
        if (profile?.Planner?.Transport != "codex-chatgpt" || profile.Codex is null)
            throw new InvalidDataException("Codex requires explicit Planner.Transport=codex-chatgpt and a Codex section.");
        profile.Codex.Validate();
        return profile.Codex;
    }

    private static async Task<Profile?> ReadAsync(string path, bool required, CancellationToken token)
    {
        if (!required && !File.Exists(path)) return null;
        if (!File.Exists(path)) throw new FileNotFoundException("The requested Ollama configuration file does not exist.");
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Ollama configuration exceeds 64 KiB.");
        try
        {
            return JsonSerializer.Deserialize<Profile>(await File.ReadAllTextAsync(path, token), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                AllowDuplicateProperties = false
            });
        }
        catch (JsonException)
        {
            // Parsing errors can echo a malformed secret or property name. Do not preserve the exception.
            throw new InvalidDataException("Invalid Ollama configuration JSON, field, or value type. Contents are not logged.");
        }
    }

    private sealed class Profile
    {
        public PlannerSettings? Planner { get; init; }
        public CodexOptions? Codex { get; init; }
        public Settings? Ollama { get; init; }
        public DecisionSettings? DecisionModel { get; init; }
    }

    private sealed class PlannerSettings
    {
        public string Transport { get; init; } = "ollama";
    }

    private sealed class DecisionSettings
    {
        public bool Enabled { get; init; }
        public string BaseUrl { get; init; } = "http://localhost:11434/";
        public string Model { get; init; } = "nimble:9b-q4_K_M";
        public string Mode { get; init; } = "shadow";
    }

    private sealed class Settings
    {
        public string BaseUrl { get; init; } = "http://localhost:11434/";
        public string? ApiKey { get; init; }
        public string Model { get; init; } = OllamaOptions.DefaultModel;
        public string ThinkingEffort { get; init; } = "low";
        public bool Stream { get; init; }
    }
}
