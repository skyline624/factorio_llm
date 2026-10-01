namespace Factorio.Agent.Codex;

public sealed record CodexOptions
{
    public const string SupportedModel = "gpt-6.1-sol";
    public string Executable { get; init; } = "codex";
    public string Model { get; init; } = SupportedModel;
    public string ThinkingEffort { get; init; } = "low";
    public int RequestTimeoutSeconds { get; init; } = 90;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Executable) || Executable.Any(char.IsControl))
            throw new ArgumentException("Codex executable must be a command or executable path.");
        if (Model != SupportedModel)
            throw new ArgumentException("This explicit ChatGPT transport requires gpt-6.1-sol; no substitute is selected.");
        if (ThinkingEffort is not ("low" or "medium" or "high" or "xhigh" or "max"))
            throw new ArgumentException("Unsupported GPT-6.1 Sol reasoning effort.");
        if (RequestTimeoutSeconds is < 1 or > 300)
            throw new ArgumentException("Codex inference timeout must be between 1 and 300 seconds.");
    }
}
