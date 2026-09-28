using System.Text;
using System.Text.Json;
using ComTool.Protocol;
using ComTool.Runtime;
using ComTool.Transport.Stdio;

namespace ComTool.Transport.Stdio.Tests;

public sealed class NdjsonServerTests
{
    [Fact]
    public async Task MultipleFramesInSingleReadProduceOneResponseEach()
    {
        var input = Join(
            Request("a", "\"one\""),
            Request("b", "2"),
            Request("c", "true"));

        var responses = await RunAsync(input);

        Assert.Equal(3, responses.Count);
        Assert.Equal("a", responses[0].GetProperty("id").GetString());
        Assert.Equal("b", responses[1].GetProperty("id").GetString());
        Assert.Equal("c", responses[2].GetProperty("id").GetString());
    }

    [Fact]
    public async Task FrameSplitAcrossTinyReadsIsReassembledExactly()
    {
        var input = Request("split", "{\"x\":[1,2,3]}") + "\n";
        var responses = await RunAsync(input, chunkSize: 3);

        var response = Assert.Single(responses);
        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal("object", response.GetProperty("result").GetProperty("kind").GetString());
        Assert.Equal(
            3,
            response.GetProperty("result")
                .GetProperty("value")
                .GetProperty("x")
                .GetArrayLength());
    }

    [Fact]
    public async Task MalformedFrameDoesNotPoisonFollowingRequest()
    {
        var input = "{not-json}\n" + Request("after", "\"ok\"") + "\n";

        var responses = await RunAsync(input);

        Assert.Equal(2, responses.Count);
        Assert.False(responses[0].GetProperty("ok").GetBoolean());
        Assert.Equal(
            "invalid_json",
            responses[0].GetProperty("error").GetProperty("kind").GetString());

        Assert.True(responses[1].GetProperty("ok").GetBoolean());
        Assert.Equal("after", responses[1].GetProperty("id").GetString());
    }

    [Fact]
    public async Task StrictFieldFailurePreservesRecoverableRequestIdentity()
    {
        const string input =
            "{\"protocolVersion\":1,\"id\":\"bad-fields\",\"operation\":\"core.echo\",\"input\":null,\"extra\":1}\n";

        var response = Assert.Single(await RunAsync(input));

        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Equal("bad-fields", response.GetProperty("id").GetString());
        Assert.Equal("core.echo", response.GetProperty("operation").GetString());
        Assert.Equal(
            "invalid_json",
            response.GetProperty("error").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task OversizedFrameIsRejectedAndFollowingFrameStillRuns()
    {
        var oversized = new string('x', 400);
        var first =
            "{\"protocolVersion\":1,\"id\":\"too-big\",\"operation\":\"core.echo\",\"input\":\"" +
            oversized +
            "\"}\n";

        var input = first + Request("after", "\"alive\"") + "\n";

        var responses = await RunAsync(input, maxFrameBytes: 256, chunkSize: 19);

        Assert.Equal(2, responses.Count);
        Assert.False(responses[0].GetProperty("ok").GetBoolean());
        Assert.Equal(
            "frame_too_large",
            responses[0].GetProperty("error").GetProperty("kind").GetString());

        Assert.True(responses[1].GetProperty("ok").GetBoolean());
        Assert.Equal("after", responses[1].GetProperty("id").GetString());
    }

    [Fact]
    public async Task FinalFrameWithoutNewlineIsAccepted()
    {
        var response = Assert.Single(await RunAsync(Request("eof", "\"done\"")));

        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal("eof", response.GetProperty("id").GetString());
    }

    [Fact]
    public async Task CrLfFramesAreAccepted()
    {
        var response = Assert.Single(
            await RunAsync(Request("crlf", "\"yes\"") + "\r\n"));

        Assert.True(response.GetProperty("ok").GetBoolean());
        Assert.Equal("crlf", response.GetProperty("id").GetString());
    }

    [Fact]
    public async Task EmptyLineIsIsolatedAsInvalidRequest()
    {
        var input = "\n" + Request("valid", "null") + "\n";

        var responses = await RunAsync(input);

        Assert.Equal(2, responses.Count);
        Assert.False(responses[0].GetProperty("ok").GetBoolean());
        Assert.True(responses[1].GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task UnsupportedProtocolVersionDoesNotReachDispatcher()
    {
        const string input =
            "{\"protocolVersion\":2,\"id\":\"v2\",\"operation\":\"core.echo\",\"input\":null}\n";

        var dispatcher = new EchoDispatcher();
        var responses = await RunAsync(input, dispatcher: dispatcher);

        Assert.Single(responses);
        Assert.Equal(0, dispatcher.CallCount);
        Assert.Equal(
            "unsupported_protocol_version",
            responses[0].GetProperty("error").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task JsonLookingStringRemainsStringAcrossStdio()
    {
        var response = Assert.Single(
            await RunAsync(Request("string", "\"{\\\"x\\\":1}\"") + "\n"));

        var result = response.GetProperty("result");
        Assert.Equal("string", result.GetProperty("kind").GetString());
        Assert.Equal(
            "{\"x\":1}",
            result.GetProperty("value").GetString());
    }

    [Fact]
    public async Task BlockedRequestDoesNotPreventLaterFrameFromDispatching()
    {
        var input =
            Join(
                Request("slow", "1"),
                Request("fast", "2"));

        await using var rawInput =
            new MemoryStream(
                Encoding.UTF8.GetBytes(input),
                writable: false);
        await using var output =
            new MemoryStream();

        var dispatcher =
            new BlockingDispatcher();
        var server =
            new NdjsonServer(
                dispatcher,
                maxConcurrentRequests: 2);

        var runTask =
            server.RunAsync(
                rawInput,
                output);

        await dispatcher.SlowEntered.Task
            .WaitAsync(TimeSpan.FromSeconds(2));

        // This is the regression assertion: the legacy stdio server awaited
        // the slow frame inline and therefore could never dispatch "fast".
        await dispatcher.FastEntered.Task
            .WaitAsync(TimeSpan.FromSeconds(2));

        dispatcher.ReleaseSlow();

        await runTask
            .WaitAsync(TimeSpan.FromSeconds(2));

        output.Position = 0;
        using var reader =
            new StreamReader(
                output,
                Encoding.UTF8,
                leaveOpen: true);

        var ids = new HashSet<string>(
            StringComparer.Ordinal);

        while (await reader.ReadLineAsync() is { } line)
        {
            using var document =
                JsonDocument.Parse(line);
            ids.Add(
                document.RootElement
                    .GetProperty("id")
                    .GetString()!);
        }

        Assert.Equal(
            new[] { "fast", "slow" }.Order(),
            ids.Order());
    }

    private static string Request(string id, string inputJson) =>
        $"{{\"protocolVersion\":1,\"id\":\"{id}\",\"operation\":\"core.echo\",\"input\":{inputJson}}}";

    private static string Join(params string[] requests) =>
        string.Join("\n", requests) + "\n";

    private static async Task<List<JsonElement>> RunAsync(
        string input,
        int? maxFrameBytes = null,
        int? chunkSize = null,
        IOperationDispatcher? dispatcher = null)
    {
        dispatcher ??= new EchoDispatcher();
        await using var rawInput = new MemoryStream(Encoding.UTF8.GetBytes(input), writable: false);
        await using Stream effectiveInput = chunkSize is null
            ? rawInput
            : new ChunkedReadStream(rawInput, chunkSize.Value);
        await using var output = new MemoryStream();

        var server = new NdjsonServer(
            dispatcher,
            maxFrameBytes ?? NdjsonServer.DefaultMaxFrameBytes);

        await server.RunAsync(effectiveInput, output);

        output.Position = 0;
        using var reader = new StreamReader(output, Encoding.UTF8, leaveOpen: true);
        var results = new List<JsonElement>();

        while (await reader.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            results.Add(document.RootElement.Clone());
        }

        return results;
    }

    private sealed class BlockingDispatcher : IOperationDispatcher
    {
        private readonly TaskCompletionSource _slowEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _fastEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSlow =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SlowEntered => _slowEntered;
        public TaskCompletionSource FastEntered => _fastEntered;

        public void ReleaseSlow() =>
            _releaseSlow.TrySetResult();

        public async ValueTask<OperationResult> ExecuteAsync(
            OperationRequest request,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(
                    request.Id,
                    "slow",
                    StringComparison.Ordinal))
            {
                _slowEntered.TrySetResult();
                await _releaseSlow.Task
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (string.Equals(
                         request.Id,
                         "fast",
                         StringComparison.Ordinal))
            {
                _fastEntered.TrySetResult();
            }

            return new OperationResult
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = request.Id,
                Operation = request.Operation,
                Ok = true,
                Status = OperationStatus.Completed,
                TargetState = TargetState.Known,
                Result = ProtocolValue.From(request.Input)
            };
        }
    }

    private sealed class EchoDispatcher : IOperationDispatcher
    {
        public int CallCount { get; private set; }

        public ValueTask<OperationResult> ExecuteAsync(
            OperationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;

            if (!string.Equals(request.Operation, "core.echo", StringComparison.Ordinal))
            {
                return ValueTask.FromResult(new OperationResult
                {
                    ProtocolVersion = ProtocolVersion.Current,
                    Id = request.Id,
                    Operation = request.Operation,
                    Ok = false,
                    Status = OperationStatus.UnsupportedOperation,
                    TargetState = TargetState.Known,
                    Error = new ProtocolError
                    {
                        Kind = "unsupported_operation",
                        Message = request.Operation,
                        Retryable = false,
                        Execution = ExecutionState.NotStarted
                    }
                });
            }

            return ValueTask.FromResult(new OperationResult
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = request.Id,
                Operation = request.Operation,
                Ok = true,
                Status = OperationStatus.Completed,
                TargetState = TargetState.Known,
                Result = ProtocolValue.From(request.Input)
            });
        }
    }

    private sealed class ChunkedReadStream(Stream inner, int maxChunk) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var count = Math.Min(buffer.Length, maxChunk);
            return await inner
                .ReadAsync(buffer[..count], cancellationToken)
                .ConfigureAwait(false);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, Math.Min(count, maxChunk));

        public override void Flush() => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
