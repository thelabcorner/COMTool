using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using ComTool.Transport.Pipe;

namespace ComTool.Transport.Pipe.Tests;

public sealed class LengthPrefixedFramedStreamTests
{
    [Fact]
    public async Task RoundTripPreservesPayload()
    {
        await using var pair = await PipePair.CreateAsync();
        await using var writer = new LengthPrefixedFramedStream(pair.Client, leaveOpen: true);
        await using var reader = new LengthPrefixedFramedStream(pair.Server, leaveOpen: true);

        var payload = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}");

        var pendingRead = reader.ReadAsync().AsTask();
        await writer.WriteAsync(payload);
        using var frame = await pendingRead;

        Assert.Equal(payload, frame.Memory.ToArray());
    }

    [Fact]
    public async Task ConcurrentWritesRemainWholeFrames()
    {
        await using var pair = await PipePair.CreateAsync();
        await using var writer = new LengthPrefixedFramedStream(pair.Client, leaveOpen: true);
        await using var reader = new LengthPrefixedFramedStream(pair.Server, leaveOpen: true);

        const int count = 64;
        var writes = Enumerable
            .Range(0, count)
            .Select(index =>
                writer.WriteAsync(
                        Encoding.UTF8.GetBytes($"frame:{index:D3}"))
                    .AsTask())
            .ToArray();

        var received = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            using var frame = await reader.ReadAsync();
            received.Add(Encoding.UTF8.GetString(frame.Span));
        }

        await Task.WhenAll(writes);

        Assert.Equal(count, received.Count);
        for (var i = 0; i < count; i++)
            Assert.Contains($"frame:{i:D3}", received);
    }

    [Fact]
    public async Task OversizedWriteIsRejectedBeforeAnyBytesAreWritten()
    {
        await using var pair = await PipePair.CreateAsync();
        await using var writer = new LengthPrefixedFramedStream(
            pair.Client,
            maxFrameBytes: 256,
            leaveOpen: true);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await writer.WriteAsync(new byte[257]));

        Assert.Contains("257", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidDeclaredLengthIsRejectedBeforeAllocation()
    {
        await using var pair = await PipePair.CreateAsync();
        await using var reader = new LengthPrefixedFramedStream(
            pair.Server,
            maxFrameBytes: 256,
            leaveOpen: true);

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 1024);
        var pendingRead = reader.ReadAsync().AsTask();
        await pair.Client.WriteAsync(header);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await pendingRead);

        Assert.Contains("1024", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentReadersAreRejected()
    {
        await using var pair = await PipePair.CreateAsync();
        await using var writer = new LengthPrefixedFramedStream(pair.Client, leaveOpen: true);
        await using var reader = new LengthPrefixedFramedStream(pair.Server, leaveOpen: true);

        var firstRead = reader.ReadAsync().AsTask();

        // Give the first call a chance to enter the blocked header read.
        await Task.Delay(25);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await reader.ReadAsync());

        await writer.WriteAsync("release"u8.ToArray());
        using var first = await firstRead;

        Assert.Equal("release", Encoding.UTF8.GetString(first.Span));
    }

    [Fact]
    public async Task DisposedFrameLeaseCannotExposePooledBuffer()
    {
        await using var pair = await PipePair.CreateAsync();
        await using var writer = new LengthPrefixedFramedStream(pair.Client, leaveOpen: true);
        await using var reader = new LengthPrefixedFramedStream(pair.Server, leaveOpen: true);

        var pendingRead = reader.ReadAsync().AsTask();
        await writer.WriteAsync("secret"u8.ToArray());
        var frame = await pendingRead;
        frame.Dispose();

        Assert.Throws<ObjectDisposedException>(() => frame.Memory.ToArray());
    }

    [Fact]
    public async Task EmptyFrameIsRejected()
    {
        await using var pair = await PipePair.CreateAsync();
        await using var writer = new LengthPrefixedFramedStream(pair.Client, leaveOpen: true);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await writer.WriteAsync(ReadOnlyMemory<byte>.Empty));
    }

    private sealed class PipePair : IAsyncDisposable
    {
        private PipePair(
            NamedPipeServerStream server,
            NamedPipeClientStream client)
        {
            Server = server;
            Client = client;
        }

        public NamedPipeServerStream Server { get; }
        public NamedPipeClientStream Client { get; }

        public static async Task<PipePair> CreateAsync()
        {
            var name = $"comtool-v2-test-{Guid.NewGuid():N}";

            var server = new NamedPipeServerStream(
                name,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            var client = new NamedPipeClientStream(
                ".",
                name,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            try
            {
                var accepting = server.WaitForConnectionAsync();
                await client.ConnectAsync(5000);
                await accepting;
                return new PipePair(server, client);
            }
            catch
            {
                await client.DisposeAsync();
                await server.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}
