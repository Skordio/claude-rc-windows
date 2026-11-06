using System.IO.Pipes;
using ClaudeRc.Host.Ipc;

namespace ClaudeRc.Host.Tests;

/// <summary>
/// The per-client outgoing queue is capped in bytes, so a viewer that stops reading cannot make
/// the host buffer hundreds of MB of output events.
/// </summary>
public sealed class ClientConnectionTests
{
    private const int Cap = 1 << 20;
    private static readonly string Chunk = new('x', 64 * 1024);

    private static (NamedPipeServerStream Server, NamedPipeClientStream Client) Pair()
    {
        var name = "claude-rc-windows-test-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accept = server.WaitForConnectionAsync();
        client.Connect(5000);
        accept.Wait(5000);
        return (server, client);
    }

    [Fact]
    public async Task A_client_that_stops_reading_is_disconnected_once_its_queue_passes_the_byte_cap()
    {
        var (server, client) = Pair();
        using var _ = client;
        var connection = new ClientConnection(server, 1, maxQueuedBytes: Cap);
        var run = connection.RunAsync((_, _) => true);

        // 64 messages of 64 KiB: 4 MiB, far under the message-count limit but over the byte cap.
        for (var i = 0; i < 64; i++)
            connection.SendLine(Chunk);

        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_client_that_keeps_reading_gets_more_than_the_cap_in_total()
    {
        var (server, client) = Pair();
        using var _ = client;
        var connection = new ClientConnection(server, 1, maxQueuedBytes: Cap);
        var run = connection.RunAsync((_, _) => true);
        var reader = new StreamReader(client);

        const int Count = 64;
        var received = 0;
        for (var i = 0; i < Count; i++)
        {
            connection.SendLine(Chunk);
            var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(Chunk, line);
            received++;
        }
        Assert.Equal(Count, received);
        Assert.False(run.IsCompleted, "connection closed although the client kept reading");

        connection.Abort();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
