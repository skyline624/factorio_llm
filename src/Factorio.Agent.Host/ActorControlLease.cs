namespace Factorio.Agent.Host;

/// <summary>Exclusive ownership across host processes; engine scope still fences pilot changes.</summary>
public sealed class ActorControlLease : IDisposable
{
    private readonly FileStream handle;
    private readonly string directory;
    private bool disposed;

    private ActorControlLease(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        handle = new FileStream(Path.Combine(this.directory, "actor-control.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    public static ActorControlLease Acquire(string directory)
    {
        try { return new(directory); }
        catch (IOException error)
        {
            throw new ActorControlUnavailableException("Exclusive actor control could not be acquired. Another controller may already own this session.", error);
        }
    }

    public void Validate(string sessionDirectory)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!string.Equals(directory, Path.GetFullPath(sessionDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The actor lease belongs to another runtime session.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        handle.Dispose();
    }
}

public sealed class ActorControlUnavailableException(string message, Exception cause) : InvalidOperationException(message, cause);
