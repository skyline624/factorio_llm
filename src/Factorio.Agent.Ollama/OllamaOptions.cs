using System.Text.Json.Serialization;

namespace Factorio.Agent.Ollama;

public sealed record OllamaOptions
{
    public const string DefaultModel = "glm-5.3-flash:cloud";
    public static readonly Uri CloudBaseUrl = new("https://ollama.com/");

    public Uri BaseUrl { get; init; } = new("http://localhost:11434/");
    [JsonIgnore]
    public string? ApiKey { get; init; }
    public bool IsDirectCloud => BaseUrl == CloudBaseUrl;
    // The hosted catalog names the same model without the local gateway's :cloud tag.
    public string RequestModel => IsDirectCloud ? "glm-5.3-flash" : Model;
    public string Model { get; init; } = DefaultModel;
    public string ThinkingEffort { get; init; } = "low";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public int MaxAttempts { get; init; } = 2;
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        if (BaseUrl is null || !BaseUrl.IsAbsoluteUri || (!BaseUrl.IsLoopback && !IsDirectCloud) ||
            BaseUrl.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(BaseUrl.UserInfo) ||
            !string.IsNullOrEmpty(BaseUrl.Query) || !string.IsNullOrEmpty(BaseUrl.Fragment))
            throw new ArgumentException("BaseUrl must identify an HTTP(S) loopback gateway or https://ollama.com/ without credentials, query or fragment.");
        if (IsDirectCloud && (string.IsNullOrWhiteSpace(ApiKey) || ApiKey.Length > 4096 ||
            ApiKey.Any(c => c <= ' ' || c >= 127)))
            throw new ArgumentException("Direct Ollama Cloud requires a valid Ollama.ApiKey or OLLAMA_API_KEY (non-empty ASCII without whitespace).");
        if (!IsDirectCloud && ApiKey is not null)
            throw new ArgumentException("An API key may only be sent to the direct Ollama Cloud endpoint.");
        if (Model != DefaultModel)
            throw new ArgumentException($"This profile requires the explicitly selected model {DefaultModel}.");
        if (ThinkingEffort is not ("low" or "high" or "max"))
            throw new ArgumentException("ThinkingEffort must be low, high or max; this model always reasons.");
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        if (MaxAttempts is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts));
        if (InitialRetryDelay < TimeSpan.Zero || MaxRetryDelay < InitialRetryDelay ||
            MaxRetryDelay > TimeSpan.FromMinutes(1))
            throw new ArgumentException("Retry delays must be non-negative, ordered, and bounded to one minute.");
    }

    public override string ToString() => $"OllamaOptions {{ BaseUrl = {BaseUrl}, Model = {Model}, ApiKey = [redacted] }}";
}
