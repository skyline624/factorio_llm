using System.Text.Json;
using System.Text.Json.Serialization;
using Factorio.Agent.Ollama;

namespace Factorio.Agent.Host;

/// <summary>Loads private connection settings before opening a game session. Never logs the document.</summary>
public static class OllamaConfiguration
{
    public const string DefaultPath = "config/appsettings.local.json";

    public static async Task<OllamaOptions> LoadAsync(string? path, bool directCloud, string? environmentApiKey,
        CancellationToken token = default)
    {
        bool explicitPath = path is not null;
        path ??= DefaultPath;
        var settings = new Settings();
        if (explicitPath || File.Exists(path))
        {
            if (!File.Exists(path)) throw new FileNotFoundException("The requested Ollama configuration file does not exist.");
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Ollama configuration exceeds 64 KiB.");
            try
            {
                string json = await File.ReadAllTextAsync(path, token);
                var profile = JsonSerializer.Deserialize<Profile>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                    AllowDuplicateProperties = false
                });
                settings = profile?.Ollama ?? throw new InvalidDataException("The configuration requires an Ollama section.");
            }
            catch (JsonException)
            {
                // Parsing errors can echo a malformed secret or property name. Do not preserve the exception.
                throw new InvalidDataException("Invalid Ollama configuration JSON, field, or value type. Contents are not logged.");
            }
        }
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

    private sealed class Profile
    {
        public Settings? Ollama { get; init; }
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
