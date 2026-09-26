using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ComTool.Protocol;
using ComTool.Runtime.Ipc;
using ComTool.Transport.Pipe;

namespace ComTool.Runtime.Ipc.Tests;

public sealed class RuntimePipeTests
{
    [Fact]
    public void RuntimeEndpointUsesDefaultMutexForDefaultPipe()
    {
        Assert.Equal(
            RuntimeEndpoint.DefaultMutexName,
            RuntimeEndpoint.MutexNameForPipe(
                RuntimeEndpoint.DefaultPipeName));
    }

    [Fact]
    public void RuntimeEndpointUsesDeterministicDistinctMutexForNamedPipe()
    {
        var first = RuntimeEndpoint.MutexNameForPipe(
            "comtool-v2-isolated-a");
        var repeated = RuntimeEndpoint.MutexNameForPipe(
            "comtool-v2-isolated-a");
        var second = RuntimeEndpoint.MutexNameForPipe(
            "comtool-v2-isolated-b");

        Assert.Equal(first, repeated);
        Assert.NotEqual(first, second);
        Assert.NotEqual(
            RuntimeEndpoint.DefaultMutexName,
            first);
        Assert.StartsWith(
            "Local\\ComToolV2Runtime_",
            first,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RoundTripPreservesCorrelationAndTaggedStringType()
    {
        var pipeName = UniquePipe();
        using var serverCts = new CancellationTokenSource();

        var server = new RuntimePipeServer(
            (request, _) => Task.FromResult(Success(
                request,
                ProtocolValue.FromString("true"))),
            pipeName);

        var serverTask = server.RunAsync(serverCts.Token);

        await using var client = await RuntimePipeClient.ConnectAsync(pipeName);
        var request = Request("roundtrip-1", "core.test");

        var result = await client.ExecuteAsync(request);

        Assert.True(result.Ok);
        Assert.Equal(request.Id, result.Id);
        Assert.Equal(request.Operation, result.Operation);
        Assert.Equal("string", result.Result?.Kind);
        Assert.Equal(
            "true",
            result.Result?.Value?.GetString());

        serverCts.Cancel();
        await serverTask;
    }

    [Fact]
    public async Task ConcurrentClientsRemainIndependentlyCorrelated()
    {
        var pipeName = UniquePipe();
        using var serverCts = new CancellationTokenSource();

        var server = new RuntimePipeServer(
            async (request, cancellationToken) =>
            {
                await Task.Delay(10, cancellationToken);
                return Success(
                    request,
                    ProtocolValue.FromString(request.Id));
            },
            pipeName);

        var serverTask = server.RunAsync(serverCts.Token);

        var tasks = Enumerable.Range(0, 12)
            .Select(async index =>
            {
                await using var client =
                    await RuntimePipeClient.ConnectAsync(pipeName);

                var request = Request(
                    $"parallel-{index:D2}",
                    "core.test");

                var result = await client.ExecuteAsync(request);
                Assert.True(result.Ok);
                Assert.Equal(request.Id, result.Id);
                Assert.Equal(
                    request.Id,
                    result.Result?.Value?.GetString());
            })
            .ToArray();

        await Task.WhenAll(tasks);

        serverCts.Cancel();
        await serverTask;
    }

    [Fact]
    public async Task MalformedClientIsDroppedWithoutPoisoningServer()
    {
        var pipeName = UniquePipe();
        using var serverCts = new CancellationTokenSource();

        var server = new RuntimePipeServer(
            (request, _) => Task.FromResult(Success(
                request,
                ProtocolValue.FromString("ok"))),
            pipeName);

        var serverTask = server.RunAsync(serverCts.Token);

        await using (var badPipe = new NamedPipeClientStream(
                         ".",
                         pipeName,
                         PipeDirection.InOut,
                         PipeOptions.Asynchronous))
        {
            await badPipe.ConnectAsync();
            await using var badFramed = new LengthPrefixedFramedStream(
                badPipe,
                leaveOpen: true);

            await badFramed.WriteAsync(
                Encoding.UTF8.GetBytes("{not-json"));
        }

        await using var goodClient =
            await RuntimePipeClient.ConnectAsync(pipeName);

        var result = await goodClient.ExecuteAsync(
            Request("after-malformed", "core.test"));

        Assert.True(result.Ok);
        Assert.Equal("after-malformed", result.Id);

        serverCts.Cancel();
        await serverTask;
    }

    [Fact]
    public async Task ClientRejectsMismatchedCorrelation()
    {
        var pipeName = UniquePipe();
        using var serverCts = new CancellationTokenSource();

        var server = new RuntimePipeServer(
            (request, _) => Task.FromResult(new OperationResult
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = "wrong-id",
                Operation = request.Operation,
                Ok = true,
                Status = OperationStatus.Completed,
                TargetState = TargetState.Known,
                Result = ProtocolValue.FromString("ok")
            }),
            pipeName);

        var serverTask = server.RunAsync(serverCts.Token);

        await using var client = await RuntimePipeClient.ConnectAsync(pipeName);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.ExecuteAsync(
                Request("expected-id", "core.test")));

        serverCts.Cancel();
        await serverTask;
    }

    private static OperationRequest Request(string id, string operation)
    {
        using var document = JsonDocument.Parse("null");
        return new OperationRequest
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = id,
            Operation = operation,
            Input = document.RootElement.Clone()
        };
    }

    private static OperationResult Success(
        OperationRequest request,
        ProtocolValue value) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = true,
            Status = OperationStatus.Completed,
            TargetState = TargetState.Known,
            Result = value
        };

    private static string UniquePipe() =>
        $"comtool-v2-test-{Guid.NewGuid():N}";
}
