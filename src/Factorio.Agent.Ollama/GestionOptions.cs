using System.Text.Json.Serialization;

namespace Factorio.Agent.Gestion;

/// <summary>
/// Local OpenAI-compatible server. Either a concrete model id (the answer must name it exactly) or the Gestion alias,
/// which follows the engine the operator selected in Gestion; the answering model is then recorded with each proposal.
/// </summary>
public sealed record GestionOptions
{
    public const string ActiveModelAlias = "llm-actif";
    public required Uri BaseUrl { get; init; }
    public required string Model { get; init; }
    [JsonIgnore]
    public string? ApiKey { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(180);
    public int MaxTokens { get; init; } = 4096;

    public void Validate()
    {
        if (BaseUrl is null || !BaseUrl.IsAbsoluteUri || BaseUrl.Scheme != "https" || !string.IsNullOrEmpty(BaseUrl.UserInfo) ||
            !string.IsNullOrEmpty(BaseUrl.Query) || !string.IsNullOrEmpty(BaseUrl.Fragment))
            throw new ArgumentException("Gestion BaseUrl must be an absolute https URL without credentials, query or fragment.");
        if (string.IsNullOrEmpty(Model) || Model.Length > 128 || Model.Any(c => c <= ' ' || c >= 127))
            throw new ArgumentException("Gestion Model must be 1 to 128 printable ASCII characters.");
        if (string.IsNullOrWhiteSpace(ApiKey) || ApiKey.Length > 4096 || ApiKey.Any(c => c <= ' ' || c >= 127))
            throw new ArgumentException("Gestion requires Gestion.ApiKey or GESTION_API_KEY (non-empty ASCII without whitespace).");
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        if (MaxTokens is < 256 or > 32768)
            throw new ArgumentOutOfRangeException(nameof(MaxTokens));
    }

    public override string ToString() => $"GestionOptions {{ BaseUrl = {BaseUrl}, Model = {Model}, ApiKey = [redacted] }}";
}
