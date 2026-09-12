namespace Factorio.Agent.Infrastructure;

public sealed record RconOptions
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 27015;
    public required string Password { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
    public int MaximumResponseBytes { get; init; } = 2 * 1024 * 1024;
    public bool KeepConnectionOpen { get; init; }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(Password);
        if (Password.Contains('\0')) throw new ArgumentException("RCON password cannot contain NUL.");
        if (Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port));
        if (Timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Timeout));
        if (MaximumResponseBytes is < 1024 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumResponseBytes));
    }
}
