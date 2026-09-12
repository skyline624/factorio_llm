using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Factorio.Agent.Infrastructure;

/// <summary>Serialized authenticated exchanges; a failed exchange closes the connection and is never replayed.</summary>
public sealed class RconClient : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const string BarrierCommand = "/silent-command rcon.print('factorio_agent_rcon_barrier')";
    private static readonly byte[] BarrierResponse = Utf8.GetBytes("factorio_agent_rcon_barrier\n");
    private readonly RconOptions options;
    private readonly SemaphoreSlim gate = new(1, 1);
    private TcpClient? connection;
    private int nextRequestId = 2;
    private bool disposed;

    public RconClient(RconOptions options)
    {
        options.Validate();
        this.options = options;
    }

    public async Task<string> ExecuteAsync(string command, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (command.Contains('\0')) throw new ArgumentException("RCON command cannot contain NUL.", nameof(command));
        if (Utf8.GetByteCount(command) > 256 * 1024)
            throw new ArgumentException("RCON command exceeds the 256 KiB limit.", nameof(command));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Timeout);
        try
        {
            await gate.WaitAsync(deadline.Token);
            try { return await ExchangeAsync(command, deadline.Token); }
            catch
            {
                CloseConnection();
                throw;
            }
            finally
            {
                if (!options.KeepConnectionOpen) CloseConnection();
                gate.Release();
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("RCON exchange timed out. The action outcome may be unknown; observe before retrying.", error);
        }
    }

    private async Task<string> ExchangeAsync(string command, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (nextRequestId > int.MaxValue - 2) CloseConnection();
        if (connection is null)
        {
            connection = new TcpClient { NoDelay = true };
            await connection.ConnectAsync(options.Host, options.Port, token);
            await WritePacketAsync(connection.GetStream(), 1, 3, options.Password, token);
            await AuthenticateAsync(connection.GetStream(), token);
        }
        NetworkStream stream = connection.GetStream();
        int commandId = nextRequestId++;
        int barrierId = nextRequestId++;
        await WritePacketAsync(stream, commandId, 2, command, token);
        using var response = new MemoryStream();
        bool barrierSent = false;
        while (true)
        {
            Packet packet = await ReadPacketAsync(stream, token);
            if (packet.Type != 0) throw new InvalidDataException("Unexpected RCON response type.");
            if (packet.Id == barrierId)
            {
                if (!barrierSent) throw new InvalidDataException("RCON barrier arrived before the command response.");
                if (!packet.Body.AsSpan().SequenceEqual(BarrierResponse))
                    throw new InvalidDataException("Unexpected RCON barrier response.");
                break;
            }
            if (packet.Id != commandId) throw new InvalidDataException("RCON response correlation mismatch.");
            if (response.Length + packet.Body.Length > options.MaximumResponseBytes)
                throw new InvalidDataException("RCON response exceeds configured size limit.");
            response.Write(packet.Body);
            if (!barrierSent)
            {
                // Factorio can drop a reply when commands are pipelined before its first response.
                // Its nonempty read-only barrier then terminates any remaining response chunks.
                await WritePacketAsync(stream, barrierId, 2, BarrierCommand, token);
                barrierSent = true;
            }
        }
        return Utf8.GetString(response.GetBuffer(), 0, checked((int)response.Length));
    }

    private void CloseConnection()
    {
        connection?.Dispose();
        connection = null;
        nextRequestId = 2;
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { disposed = true; CloseConnection(); }
        finally { gate.Release(); }
    }

    private async Task AuthenticateAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Packet packet = await ReadPacketAsync(stream, cancellationToken);
            if (packet.Id == -1) throw new UnauthorizedAccessException("RCON authentication rejected.");
            if (packet.Id != 1) throw new InvalidDataException("RCON authentication correlation mismatch.");
            if (packet.Type == 2) return;
            if (packet.Type != 0) throw new InvalidDataException("Unexpected RCON authentication response.");
        }
        throw new InvalidDataException("Missing RCON authentication acknowledgement.");
    }

    private async Task<Packet> ReadPacketAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 10 || length > options.MaximumResponseBytes + 10)
            throw new InvalidDataException("Invalid RCON packet length.");
        byte[] buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, cancellationToken);
        if (buffer[^1] != 0 || buffer[^2] != 0)
            throw new InvalidDataException("Invalid RCON packet terminators.");
        return new Packet(BinaryPrimitives.ReadInt32LittleEndian(buffer),
            BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(4)), buffer[8..^2]);
    }

    private static async Task WritePacketAsync(NetworkStream stream, int id, int type, string body,
        CancellationToken cancellationToken)
    {
        byte[] payload = Utf8.GetBytes(body);
        byte[] packet = new byte[payload.Length + 14];
        BinaryPrimitives.WriteInt32LittleEndian(packet, payload.Length + 10);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), type);
        payload.CopyTo(packet, 12);
        await stream.WriteAsync(packet, cancellationToken);
    }

    private sealed record Packet(int Id, int Type, byte[] Body);
}
