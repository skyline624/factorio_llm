namespace Factorio.Agent.Ollama;

public sealed record OllamaOptions
{
    public const string DefaultModel = "glm-5.3-flash:cloud";

    public Uri BaseUrl { get; init; } = new("http://localhost:11434/");
    public string Model { get; init; } = DefaultModel;
    public string ThinkingEffort { get; init; } = "low";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public int MaxAttempts { get; init; } = 2;
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        if (BaseUrl is null || !BaseUrl.IsAbsoluteUri || !BaseUrl.IsLoopback ||
            BaseUrl.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(BaseUrl.UserInfo) ||
            !string.IsNullOrEmpty(BaseUrl.Query) || !string.IsNullOrEmpty(BaseUrl.Fragment))
            throw new ArgumentException("BaseUrl must identify an HTTP(S) Ollama loopback gateway without credentials, query or fragment.");
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
}
