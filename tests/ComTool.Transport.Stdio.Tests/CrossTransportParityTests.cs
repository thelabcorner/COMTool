using System.Text;
using System.Text.Json;
using ComTool.Protocol;
using ComTool.Runtime;
using ComTool.Runtime.Ipc;
using ComTool.Transport.Stdio;

namespace ComTool.Transport.Stdio.Tests;

/// <summary>
/// Proves the architectural invariant that NDJSON stdio and the internal
/// length-prefixed runtime pipe are two framings over ONE operation protocol:
/// the same request dispatched by the same handler yields byte-identical
/// canonical <see cref="OperationResult"/> JSON across both transports.
/// </summary>
public sealed class CrossTransportParityTests
{
    [Theory]
    [InlineData("core.runtime.health", "null")]
    [InlineData("core.targets.list", "null")]
    [InlineData("com.get", "{\"path\":\"Version\"}")]
    [InlineData("script.eval", "{\"kind\":\"expression\",\"source\":\"1+1\",\"effects\":\"unknown\"}")]
    public async Task SameRequestYieldsIdenticalEnvelopeAcrossTransports(
        string operation,
        string inputJson)
    {
        var handler = new CanonicalDispatcher();

        var stdioJson = await ExecuteViaStdioAsync(handler, operation, inputJson);
        var pipeJson = await ExecuteViaPipeAsync(handler, operation, inputJson);

        Assert.Equal(stdioJson, pipeJson);
    }

    [Fact]
    public async Task MalformedStdioFrameProducesValidationEnvelopeAndKeepsServing()
    {
        var handler = new CanonicalDispatcher();
        var input = "{not-json}\n" + RequestLine("after", "core.runtime.health", "null") + "\n";

        var responses = await RunStdioAsync(input, handler);

        Assert.Equal(2, responses.Count);
        Assert.False(responses[0].GetProperty("ok").GetBoolean());
        Assert.Equal(
            "invalid_json",
            responses[0].GetProperty("error").GetProperty("kind").GetString());
        Assert.True(responses[1].GetProperty("ok").GetBoolean());
    }

    private static async Task<string> ExecuteViaStdioAsync(
        CanonicalDispatcher handler,
        string operation,
        string inputJson)
    {
        var input = RequestLine("parity-stdio", operation, inputJson) + "\n";
        var responses = await RunStdioAsync(input, handler);
        return responses.Single().GetRawText();
    }

    private static async Task<string> ExecuteViaPipeAsync(
        CanonicalDispatcher handler,
        string operation,
        string inputJson)
    {
        var pipeName = $"comtool-v2-parity-{Guid.NewGuid():N}";
        using var serverCts = new CancellationTokenSource();

        var server = new RuntimePipeServer(
            (request, cancellationToken) =>
                handler.ExecuteAsync(request, cancellationToken).AsTask(),
            pipeName);
        var serverTask = server.RunAsync(serverCts.Token);

        await using var client = await RuntimePipeClient.ConnectAsync(pipeName);

        using var document = JsonDocument.Parse(inputJson);

        var result = await client.ExecuteAsync(new OperationRequest
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = "parity-stdio",
            Operation = operation,
            Input = document.RootElement.Clone()
        });

        serverCts.Cancel();
        await serverTask;

        return ProtocolJson.Serialize(result);
    }

    private static async Task<List<JsonElement>> RunStdioAsync(
        string input,
        CanonicalDispatcher handler)
    {
        await using var rawInput = new MemoryStream(
            Encoding.UTF8.GetBytes(input),
            writable: false);
        await using var output = new MemoryStream();

        var server = new NdjsonServer(handler);
        await server.RunAsync(rawInput, output);

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

    private static string RequestLine(
        string id,
        string operation,
        string inputJson) =>
        $"{{\"protocolVersion\":1,\"id\":\"{id}\",\"operation\":\"{operation}\",\"input\":{inputJson}}}";

    /// <summary>
    /// Deterministic, transport-independent handler that emits a canonical
    /// envelope so parity is a property of the protocol, not the host adapter.
    /// </summary>
    private sealed class CanonicalDispatcher : IOperationDispatcher
    {
        public ValueTask<OperationResult> ExecuteAsync(
            OperationRequest request,
            CancellationToken cancellationToken = default)
        {
            var known = request.Operation is
                "core.runtime.health" or
                "core.targets.list" or
                "com.get" or
                "script.eval";

            var result = new OperationResult
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = request.Id,
                Operation = request.Operation,
                Ok = known,
                Status = known
                    ? OperationStatus.Completed
                    : OperationStatus.UnsupportedOperation,
                TargetState = TargetState.Known,
                Result = known
                    ? ProtocolValue.From(request.Input)
                    : null,
                Error = known
                    ? null
                    : new ProtocolError
                    {
                        Kind = "unsupported_operation",
                        Message = request.Operation,
                        Retryable = false,
                        Execution = ExecutionState.NotStarted
                    }
            };

            return ValueTask.FromResult(result);
        }
    }
}
