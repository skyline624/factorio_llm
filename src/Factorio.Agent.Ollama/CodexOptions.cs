namespace Factorio.Agent.Codex;

public sealed record CodexOptions
{
    public const string SupportedModel = "gpt-6.1-sol";
    /// <summary>Explicitly selectable models; the resolved thread model must still match the selected one exactly.</summary>
    public static readonly IReadOnlySet<string> SupportedModels = new HashSet<string>(StringComparer.Ordinal) { SupportedModel, "gpt-6.1-terra" };
    public string Executable { get; init; } = "codex";
    public string Model { get; init; } = SupportedModel;
    public string ThinkingEffort { get; init; } = "low";
    public int RequestTimeoutSeconds { get; init; } = 90;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Executable) || Executable.Any(char.IsControl))
            throw new ArgumentException("Codex executable must be a command or executable path.");
        if (!SupportedModels.Contains(Model))
            throw new ArgumentException("This explicit ChatGPT transport requires gpt-6.1-sol or gpt-6.1-terra; no substitute is selected.");
        if (ThinkingEffort is not ("low" or "medium" or "high" or "xhigh" or "max"))
            throw new ArgumentException("Unsupported GPT-6.1 reasoning effort.");
        if (RequestTimeoutSeconds is < 1 or > 300)
            throw new ArgumentException("Codex inference timeout must be between 1 and 300 seconds.");
    }
}
