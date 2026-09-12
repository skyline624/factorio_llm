using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;
using Xunit;

namespace Factorio.Agent.Infrastructure.Tests;

public sealed class RconTests
{
    private const string BarrierCommand = "/silent-command rcon.print('factorio_agent_rcon_barrier')";
    private const string BarrierResponse = "factorio_agent_rcon_barrier\n";

    [Fact]
    public async Task SendsBarrierOnlyAfterTheFirstCommandResponse()
    {
        await WithServer(async stream =>
        {
            await ReadAsync(stream);
            await WriteAsync(stream, 1, 2, []);
            Assert.Equal(2, (await ReadAsync(stream)).Id);
            await Task.Delay(50);
            Assert.False(stream.DataAvailable, "The next command must wait for Factorio's first response.");
            await WriteAsync(stream, 2, 0, Encoding.UTF8.GetBytes("first "));
            Assert.Equal(3, (await ReadAsync(stream)).Id);
            await WriteAsync(stream, 2, 0, Encoding.UTF8.GetBytes("last"));
            await WriteAsync(stream, 3, 0, Encoding.UTF8.GetBytes(BarrierResponse));
        }, async client => Assert.Equal("first last", await client.ExecuteAsync("example")));
    }

    [Fact]
    public async Task CompletesWhenServerIgnoresEmptyCommands()
    {
        await WithServer(async stream =>
        {
            await ReadAsync(stream);
            await WriteAsync(stream, 1, 2, []);
            Assert.Equal("example", Encoding.UTF8.GetString((await ReadAsync(stream)).Body));
            await WriteAsync(stream, 2, 0, Encoding.UTF8.GetBytes("executed once\n"));
            var barrier = await ReadAsync(stream);
            if (barrier.Body.Length == 0)
            {
                // Factorio 2.0.77 silently ignores an empty command even after executing the preceding command.
                await Task.Delay(600);
                return;
            }
            Assert.Equal(3, barrier.Id);
            Assert.Equal(2, barrier.Type);
            Assert.Equal(BarrierCommand, Encoding.UTF8.GetString(barrier.Body));
            await WriteAsync(stream, 3, 0, Encoding.UTF8.GetBytes(BarrierResponse));
        }, async client => Assert.Equal("executed once\n", await client.ExecuteAsync("example")),
            TimeSpan.FromMilliseconds(250));
    }

    [Theory]
    [InlineData("")]
    [InlineData("unexpected\n")]
    public async Task RejectsIncorrectBarrierContent(string content)
    {
        await WithServer(async stream =>
        {
            await ReadAsync(stream);
            await WriteAsync(stream, 1, 2, []);
            await ReadAsync(stream);
            await WriteAsync(stream, 2, 0, Encoding.UTF8.GetBytes("partial result"));
            await ReadAsync(stream);
            await WriteAsync(stream, 3, 0, Encoding.UTF8.GetBytes(content));
        }, async client => await Assert.ThrowsAsync<InvalidDataException>(() => client.ExecuteAsync("example")));
    }

    [Fact]
    public async Task ReassemblesFragmentedMultiPacketUnicodeResponse()
    {
        string expected = new('x', 5000);
        expected += "é世界";
        await WithServer(async stream =>
        {
            Assert.Equal(3, (await ReadAsync(stream)).Type);
            await WriteAsync(stream, 1, 0, []);
            await WriteAsync(stream, 1, 2, []);
            Assert.Equal("example", Encoding.UTF8.GetString((await ReadAsync(stream)).Body));
            byte[] bytes = Encoding.UTF8.GetBytes(expected);
            await WriteAsync(stream, 2, 0, bytes[..5001]);
            Assert.Equal(BarrierCommand, Encoding.UTF8.GetString((await ReadAsync(stream)).Body));
            await WriteAsync(stream, 2, 0, bytes[5001..]);
            await WriteAsync(stream, 3, 0, Encoding.UTF8.GetBytes(BarrierResponse));
        }, async client => Assert.Equal(expected, await client.ExecuteAsync("example")));
    }

    [Fact]
    public async Task RejectsFailedAuthenticationBeforeSendingCommand()
    {
        await WithServer(async stream =>
        {
            await ReadAsync(stream);
            await WriteAsync(stream, -1, 2, []);
        }, async client => await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.ExecuteAsync("example")));
    }

    [Fact]
    public async Task RejectsInvalidPacketLength()
    {
        await WithServer(async stream =>
        {
            await ReadAsync(stream);
            byte[] length = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, int.MaxValue);
            await stream.WriteAsync(length);
        }, async client => await Assert.ThrowsAsync<InvalidDataException>(() => client.ExecuteAsync("example")));
    }

    [Fact]
    public async Task RejectsUnrelatedResponseId()
    {
        await WithServer(async stream =>
        {
            await ReadAsync(stream);
            await WriteAsync(stream, 1, 2, []);
            await ReadAsync(stream);
            await WriteAsync(stream, 99, 0, []);
        }, async client => await Assert.ThrowsAsync<InvalidDataException>(() => client.ExecuteAsync("example")));
    }

    [Fact]
    public async Task BoundsAnUnresponsiveServerWithTimeout()
    {
        await WithServer(async stream =>
        {
            await ReadAsync(stream);
            await Task.Delay(400);
        }, async client => await Assert.ThrowsAsync<TimeoutException>(() => client.ExecuteAsync("example")),
            TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void CommandEncoderPreventsLuaDelimiterInjection()
    {
        GameRequest request = GameRequest.Create("submit", new { recipe = "]=])); game.print('INJECTED'); --" });
        string encoded = FactorioGameClient.BuildCommand(request);
        Assert.Contains("[==[", encoded);
        Assert.EndsWith("]==]))", encoded);
    }

    [Theory]
    [InlineData("eval")]
    [InlineData("/silent-command")]
    public void RejectsActionsOutsideTheProtocol(string action) =>
        Assert.Throws<ArgumentException>(() => FactorioGameClient.BuildCommand(GameRequest.Create(action)));

    private static async Task WithServer(Func<NetworkStream, Task> server, Func<RconClient, Task> action,
        TimeSpan? timeout = null)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task serve = Task.Run(async () =>
        {
            using TcpClient socket = await listener.AcceptTcpClientAsync();
            await using NetworkStream stream = socket.GetStream();
            await server(stream);
        });
        try
        {
            await action(new RconClient(new RconOptions
            {
                Port = port, Password = "test-only", Timeout = timeout ?? TimeSpan.FromSeconds(5)
            }));
        }
        finally
        {
            await serve.WaitAsync(TimeSpan.FromSeconds(6));
        }
    }

    private static async Task<(int Id, int Type, byte[] Body)> ReadAsync(NetworkStream stream)
    {
        byte[] length = new byte[4];
        await stream.ReadExactlyAsync(length);
        byte[] packet = new byte[BinaryPrimitives.ReadInt32LittleEndian(length)];
        await stream.ReadExactlyAsync(packet);
        return (BinaryPrimitives.ReadInt32LittleEndian(packet), BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4)), packet[8..^2]);
    }

    private static async Task WriteAsync(NetworkStream stream, int id, int type, byte[] body)
    {
        byte[] packet = new byte[body.Length + 14];
        BinaryPrimitives.WriteInt32LittleEndian(packet, body.Length + 10);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), type);
        body.CopyTo(packet, 12);
        for (int index = 0; index < packet.Length; index += 31)
            await stream.WriteAsync(packet.AsMemory(index, Math.Min(31, packet.Length - index)));
    }
}
