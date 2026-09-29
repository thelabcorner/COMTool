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
    public async Task ClientPoolLetsControlRequestBypassBlockedConnection()
    {
        var pipeName = UniquePipe();
        using var serverCts =
            new CancellationTokenSource();

        var slowEntered =
            new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow =
            new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

        var server = new RuntimePipeServer(
            async (request, cancellationToken) =>
            {
                if (string.Equals(
                        request.Id,
                        "slow",
                        StringComparison.Ordinal))
                {
                    slowEntered.TrySetResult();
                    await releaseSlow.Task
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                return Success(
                    request,
                    ProtocolValue.FromString(request.Id));
            },
            pipeName);

        var serverTask =
            server.RunAsync(serverCts.Token);

        await using var pool =
            await RuntimePipeClientPool.ConnectAsync(
                pipeName,
                maxIdleClients: 2);

        var slowTask =
            pool.ExecuteAsync(
                Request(
                    "slow",
                    "script.runFile"));

        try
        {
            await slowEntered.Task
                .WaitAsync(TimeSpan.FromSeconds(2));

            var controlTask =
                pool.ExecuteAsync(
                    Request(
                        "control",
                        "core.target.host.terminate"));

            var control =
                await controlTask
                    .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(control.Ok);
            Assert.Equal(
                "control",
                control.Id);
            Assert.False(
                slowTask.IsCompleted);

            releaseSlow.TrySetResult();

            var slow =
                await slowTask
                    .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(slow.Ok);
            Assert.Equal(
                "slow",
                slow.Id);
        }
        finally
        {
            releaseSlow.TrySetResult();
            serverCts.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task ClientPoolSelfHealPurgesStaleIdleGenerationWithoutReplayingInterruptedRequest()
    {
        var pipeName = UniquePipe();
        using var firstServerCts = new CancellationTokenSource();
        var enteredCount = 0;
        var bothEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSeed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var firstServer = new RuntimePipeServer(
            async (request, cancellationToken) =>
            {
                if (request.Id.StartsWith("seed-", StringComparison.Ordinal))
                {
                    if (Interlocked.Increment(ref enteredCount) == 2)
                        bothEntered.TrySetResult();

                    await releaseSeed.Task
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                return Success(
                    request,
                    ProtocolValue.FromString(request.Id));
            },
            pipeName);

        var firstServerTask =
            firstServer.RunAsync(firstServerCts.Token);

        await using var pool =
            await RuntimePipeClientPool.ConnectAsync(
                pipeName,
                maxIdleClients: 2,
                selfHeal: true);

        var seedA = pool.ExecuteAsync(
            Request("seed-a", "core.test"));
        var seedB = pool.ExecuteAsync(
            Request("seed-b", "core.test"));

        await bothEntered.Task
            .WaitAsync(TimeSpan.FromSeconds(2));
        releaseSeed.TrySetResult();
        await Task.WhenAll(seedA, seedB);

        firstServerCts.Cancel();
        await firstServerTask;

        using var replacementCts = new CancellationTokenSource();
        var replacement = new RuntimePipeServer(
            (request, _) => Task.FromResult(Success(
                request,
                ProtocolValue.FromString("replacement:" + request.Id))),
            pipeName);
        var replacementTask = replacement.RunAsync(replacementCts.Token);

        try
        {
            var interrupted =
                await Assert.ThrowsAsync<RuntimeRequestInterruptedException>(
                    () => pool.ExecuteAsync(
                        Request(
                            "first-after-restart",
                            "core.test")));

            Assert.Equal(
                "first-after-restart",
                interrupted.RequestId);
            Assert.Equal(
                ExecutionState.Ambiguous,
                interrupted.Execution);

            var recovered = await pool.ExecuteAsync(
                Request(
                    "second-after-restart",
                    "core.test"));

            Assert.True(recovered.Ok);
            Assert.Equal(
                "replacement:second-after-restart",
                recovered.Result?.Value?.GetString());
        }
        finally
        {
            replacementCts.Cancel();
            await replacementTask;
        }
    }

    [Fact]
    public async Task ClientPoolSelfHealOptOutLeavesStaleIdleConnectionsFailFast()
    {
        var pipeName = UniquePipe();
        using var firstServerCts = new CancellationTokenSource();
        var enteredCount = 0;
        var bothEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSeed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var firstServer = new RuntimePipeServer(
            async (request, cancellationToken) =>
            {
                if (request.Id.StartsWith("seed-", StringComparison.Ordinal))
                {
                    if (Interlocked.Increment(ref enteredCount) == 2)
                        bothEntered.TrySetResult();

                    await releaseSeed.Task
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                return Success(
                    request,
                    ProtocolValue.FromString(request.Id));
            },
            pipeName);

        var firstServerTask =
            firstServer.RunAsync(firstServerCts.Token);

        await using var pool =
            await RuntimePipeClientPool.ConnectAsync(
                pipeName,
                maxIdleClients: 2,
                selfHeal: false);

        var seedA = pool.ExecuteAsync(
            Request("seed-a", "core.test"));
        var seedB = pool.ExecuteAsync(
            Request("seed-b", "core.test"));

        await bothEntered.Task
            .WaitAsync(TimeSpan.FromSeconds(2));
        releaseSeed.TrySetResult();
        await Task.WhenAll(seedA, seedB);

        firstServerCts.Cancel();
        await firstServerTask;

        using var replacementCts = new CancellationTokenSource();
        var replacement = new RuntimePipeServer(
            (request, _) => Task.FromResult(Success(
                request,
                ProtocolValue.FromString("replacement:" + request.Id))),
            pipeName);
        var replacementTask = replacement.RunAsync(replacementCts.Token);

        try
        {
            await Assert.ThrowsAsync<RuntimeRequestInterruptedException>(
                () => pool.ExecuteAsync(
                    Request("stale-one", "core.test")));
            await Assert.ThrowsAsync<RuntimeRequestInterruptedException>(
                () => pool.ExecuteAsync(
                    Request("stale-two", "core.test")));

            var fresh = await pool.ExecuteAsync(
                Request("fresh-third", "core.test"));

            Assert.True(fresh.Ok);
            Assert.Equal(
                "replacement:fresh-third",
                fresh.Result?.Value?.GetString());
        }
        finally
        {
            replacementCts.Cancel();
            await replacementTask;
        }
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

        var ex = await Assert.ThrowsAsync<RuntimeRequestInterruptedException>(
            () => client.ExecuteAsync(
                Request("expected-id", "core.test")));

        Assert.Equal(
            ExecutionState.Ambiguous,
            ex.Execution);
        Assert.Equal(
            "expected-id",
            ex.RequestId);

        serverCts.Cancel();
        await serverTask;
    }

    [Fact]
    public async Task ExplicitClientTimeoutFailsClosedAsAmbiguous()
    {
        var pipeName = UniquePipe();
        using var serverCts = new CancellationTokenSource();

        var server = new RuntimePipeServer(
            async (request, _) =>
            {
                await Task.Delay(200);
                return Success(
                    request,
                    ProtocolValue.FromString("late"));
            },
            pipeName);

        var serverTask = server.RunAsync(serverCts.Token);
        await using var client = await RuntimePipeClient.ConnectAsync(pipeName);
        var request = Request("timeout-ambiguous", "core.test");

        var ex = await Assert.ThrowsAsync<RuntimeRequestInterruptedException>(
            () => client.ExecuteAsync(
                request,
                timeout: TimeSpan.FromMilliseconds(20)));

        Assert.Equal(
            ExecutionState.Ambiguous,
            ex.Execution);
        Assert.Equal(
            request.Id,
            ex.RequestId);

        await Task.Delay(250);
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
