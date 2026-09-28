using System.Runtime.InteropServices;
using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator.Tests;

public sealed class IllustratorRetryBudgetTests
{
    private const int RpcECallRejected = unchecked((int)0x80010001);

    [Fact]
    public void ZeroBudgetAttemptsRejectedComCallExactlyOnceAndDoesNotLeak()
    {
        RunSta(() =>
        {
            var identity = Identity();
            var remainingRejects = 1;
            var attempts = 0;
            var session = new IllustratorSession(
                identity,
                new object(),
                (_, _, _) => IllustratorComInterop.RetryRead(
                    () =>
                    {
                        attempts++;
                        if (remainingRejects > 0)
                        {
                            remainingRejects--;
                            throw new COMException(
                                "Illustrator is busy.",
                                RpcECallRejected);
                        }

                        return """{"ok":true,"result":42}""";
                    }));

            try
            {
                var zeroBudget = session.ExecuteAsync(
                        ScriptRequest(
                            identity,
                            "retry-zero",
                            retryBudgetMs: 0))
                    .GetAwaiter()
                    .GetResult();

                Assert.False(zeroBudget.Ok);
                Assert.Equal(OperationStatus.HostBusy, zeroBudget.Status);
                Assert.Equal("host_busy", zeroBudget.Error?.Kind);
                Assert.Equal(
                    ExecutionState.NotStarted,
                    zeroBudget.Error?.Execution);
                Assert.Equal(1, attempts);

                remainingRejects = 1;
                var defaultBudget = session.ExecuteAsync(
                        ScriptRequest(identity, "retry-default"))
                    .GetAwaiter()
                    .GetResult();

                Assert.True(defaultBudget.Ok, defaultBudget.Error?.Message);
                Assert.Equal(OperationStatus.Completed, defaultBudget.Status);
                Assert.Equal(3, attempts);
                Assert.Equal(
                    42,
                    defaultBudget.Result!.Value!.Value.GetInt32());
            }
            finally
            {
                session.DisposeAsync()
                    .GetAwaiter()
                    .GetResult();
            }

            return true;
        });
    }

    [Fact]
    public void DirectSessionRejectsOutOfRangeRetryBudgetBeforeCom()
    {
        RunSta(() =>
        {
            var identity = Identity();
            var calls = 0;
            var session = new IllustratorSession(
                identity,
                new object(),
                (_, _, _) =>
                {
                    calls++;
                    return """{"ok":true,"result":42}""";
                });

            try
            {
                var result = session.ExecuteAsync(
                        ScriptRequest(
                            identity,
                            "retry-invalid",
                            retryBudgetMs:
                                OperationPolicy.MaxRetryBudgetMs + 1))
                    .GetAwaiter()
                    .GetResult();

                Assert.False(result.Ok);
                Assert.Equal(
                    OperationStatus.InvalidRequest,
                    result.Status);
                Assert.Equal(
                    "invalid_retry_budget",
                    result.Error?.Kind);
                Assert.Equal(0, calls);
            }
            finally
            {
                session.DisposeAsync()
                    .GetAwaiter()
                    .GetResult();
            }

            return true;
        });
    }

    private static OperationRequest ScriptRequest(
        HostTargetIdentity identity,
        string id,
        int? retryBudgetMs = null)
    {
        using var input = JsonDocument.Parse(
            """
            {
              "kind":"expression",
              "source":"6*7",
              "effects":"unknown"
            }
            """);

        return new OperationRequest
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = id,
            Target = new TargetRef(
                identity.Host,
                identity.TargetId,
                Generation: 0),
            Operation = "script.eval",
            Input = input.RootElement.Clone(),
            Policy = retryBudgetMs is null
                ? null
                : new OperationPolicy(
                    RetryBudgetMs: retryBudgetMs)
        };
    }

    private static HostTargetIdentity Identity() =>
        new()
        {
            Host = IllustratorAdapter.HostName,
            ProcessId = 4242,
            ProcessStartedAt =
                DateTimeOffset.Parse("2026-09-27T12:00:00Z"),
            ExecutablePath = @"C:\Adobe\Illustrator.exe",
            HostVersion = "30.6.0",
            AdapterVersion = IllustratorAdapter.CurrentAdapterVersion,
            EndpointIdentity = IllustratorComInterop.ProgId
        };

    private static T RunSta<T>(Func<T> action)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure =
            null;
        T? result = default;
        var completed = false;

        var thread = new Thread(() =>
        {
            try
            {
                result = action();
                completed = true;
            }
            catch (Exception ex)
            {
                failure =
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo
                        .Capture(ex);
            }
        })
        {
            IsBackground = true
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("STA test thread did not finish.");

        failure?.Throw();
        if (!completed)
        {
            throw new InvalidOperationException(
                "STA test function did not complete.");
        }

        return result!;
    }

}
