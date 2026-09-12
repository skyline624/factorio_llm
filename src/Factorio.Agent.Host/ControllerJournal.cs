using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

public interface IControllerJournal
{
    Task AppendAsync(string type, object data, CancellationToken token);
}

/// <summary>Durable interim journal. A mutation is sent only after its intent is flushed.</summary>
public sealed class ControllerJournal(string path) : IControllerJournal
{
    public async Task AppendAsync(string type, object data, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(file, new { type, recordedUtc = DateTime.UtcNow, data }, Protocol.Json, token);
        await file.WriteAsync("\n"u8.ToArray(), token);
        await file.FlushAsync(token);
        file.Flush(flushToDisk: true);
    }
}
