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
        await using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        // One write per record: a kill leaves at most one torn final line. If a previous write was torn (no final
        // newline), start on a fresh line instead of gluing onto it; readers still reject the torn line (fail closed).
        bool torn = false;
        if (file.Length > 0)
        {
            file.Seek(-1, SeekOrigin.End);
            torn = file.ReadByte() != '\n';
        }
        file.Seek(0, SeekOrigin.End);
        byte[] row = JsonSerializer.SerializeToUtf8Bytes(new { type, recordedUtc = DateTime.UtcNow, data }, Protocol.Json);
        byte[] bytes = new byte[(torn ? 1 : 0) + row.Length + 1];
        if (torn) bytes[0] = (byte)'\n';
        row.CopyTo(bytes, torn ? 1 : 0);
        bytes[^1] = (byte)'\n';
        await file.WriteAsync(bytes, token);
        await file.FlushAsync(token);
        file.Flush(flushToDisk: true);
    }
}
