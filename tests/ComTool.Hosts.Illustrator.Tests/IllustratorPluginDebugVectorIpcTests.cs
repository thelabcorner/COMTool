using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator.Tests;

public sealed class IllustratorPluginDebugVectorIpcTests
{
    [Fact]
    public void DirectDiagnosticsUseOnlyRuntimeDiscoveredGenerationAndPropagateCancellation()
    {
        var identity = Identity();
        var ctl = new RecordingCtl
        {
            Outcome = new AipDebugCtlOutcome(
                AipDebugCtlClient.ExitSuccess,
                """
                {
                  "ok":true,
                  "protocol":"AIPDebug/1",
                  "plugin":"AIPDebug",
                  "pid":4242
                }
                """,
                string.Empty,
                AipDebugCtlCompletion.Completed)
        };
        var comCalls = 0;
        var manager = new IllustratorPluginDebugManager(
            identity,
            ctl,
            (_, method, args) =>
            {
                comCalls++;
                Assert.Equal("SendScriptMessage", method);
                Assert.Equal("AIPDebug", args[0]);
                Assert.Equal("AIPDebug/1/discover", args[1]);
                Assert.Equal(string.Empty, args[2]);
                return """
                {
                  "ok":true,
                  "protocol":"AIPDebug/1",
                  "plugin":"AIPDebug",
                  "pid":4242,
                  "endpoint":"aipdbg-test-generation-4242",
                  "version":"0.1.0",
                  "buildId":"test-build"
                }
                """;
            });

        var discover = manager.Execute(
            Request(
                IllustratorPluginDebugManager.DiagnosticsOperation,
                """
                {
                  "plugin":"AIPDebug",
                  "action":"discover",
                  "transport":"com"
                }
                """),
            new object());

        Assert.True(discover.Ok, discover.Error?.Message);
        Assert.Equal(1, comCalls);
        Assert.Null(ctl.Invocation);

        using var cts = new CancellationTokenSource();
        var direct = manager.Execute(
            Request(
                IllustratorPluginDebugManager.DiagnosticsOperation,
                """
                {
                  "plugin":"AIPDebug",
                  "action":"info",
                  "transport":"ipc",
                  "endpoint":"aipdbg-test-generation-4242",
                  "timeoutMs":2345
                }
                """),
            new object(),
            cts.Token);

        Assert.True(direct.Ok, direct.Error?.Message);
        Assert.Equal(1, comCalls);

        var invocation = Assert.IsType<AipDebugCtlInvocation>(ctl.Invocation);
        Assert.Equal(
            "aipdbg-test-generation-4242",
            invocation.Endpoint);
        Assert.Equal("info", invocation.Action);
        Assert.Equal(identity.ProcessId, invocation.ExpectedProcessId);
        Assert.Equal(
            identity.ProcessStartedAt.UtcDateTime.ToFileTimeUtc(),
            invocation.ExpectedProcessStartFileTimeUtc);
        Assert.Equal(2345, invocation.TimeoutMs);
        Assert.Equal(
            IllustratorPluginDebugManager.MaxResponseUtf8Bytes,
            invocation.MaxOutputBytes);
        Assert.Equal(cts.Token, ctl.Token);

        var result = Value(direct);
        Assert.Equal("vectoripc", result.GetProperty("transport").GetString());
        Assert.Equal(
            "runtime_discovered",
            result.GetProperty("endpointProvenance").GetString());
        Assert.Equal(
            identity.ProcessId,
            result.GetProperty("serverProcessId").GetInt32());
    }

    [Fact]
    public void CallerCannotReplaceDiscoveredEndpoint()
    {
        var identity = Identity();
        var ctl = new RecordingCtl();
        var manager = new IllustratorPluginDebugManager(
            identity,
            ctl,
            (_, _, _) =>
                """
                {
                  "ok":true,
                  "protocol":"AIPDebug/1",
                  "plugin":"AIPDebug",
                  "pid":4242,
                  "endpoint":"aipdbg-authoritative-4242"
                }
                """);

        var discover = manager.Execute(
            Request(
                IllustratorPluginDebugManager.DiagnosticsOperation,
                """
                {
                  "plugin":"AIPDebug",
                  "action":"discover",
                  "transport":"com"
                }
                """),
            new object());
        Assert.True(discover.Ok, discover.Error?.Message);

        var result = manager.Execute(
            Request(
                IllustratorPluginDebugManager.DiagnosticsOperation,
                """
                {
                  "plugin":"AIPDebug",
                  "action":"stats",
                  "transport":"ipc",
                  "endpoint":"aipdbg-attacker-4242"
                }
                """),
            new object());

        Assert.False(result.Ok);
        Assert.Equal(
            "plugin_debug_endpoint_mismatch",
            result.Error?.Kind);
        Assert.Equal(
            ExecutionState.NotStarted,
            result.Error?.Execution);
        Assert.Null(ctl.Invocation);
    }

    [Fact]
    public void EffectfulControlNeverSilentlyFallsBackWhenDirectProvenanceIsMissing()
    {
        var identity = Identity();
        var ctl = new RecordingCtl();
        var comCalls = 0;
        var manager = new IllustratorPluginDebugManager(
            identity,
            ctl,
            (_, _, _) =>
            {
                comCalls++;
                return """{"ok":true}""";
            });

        var result = manager.Execute(
            Request(
                IllustratorPluginDebugManager.ControlOperation,
                """
                {
                  "plugin":"AIPDebug",
                  "action":"clear",
                  "transport":"auto"
                }
                """,
                leaseId: "lease-vectoripc-test-000000000000"),
            new object());

        Assert.False(result.Ok);
        Assert.Equal(
            "plugin_debug_endpoint_unknown",
            result.Error?.Kind);
        Assert.Equal(
            ExecutionState.NotStarted,
            result.Error?.Execution);
        Assert.Equal(0, comCalls);
        Assert.Null(ctl.Invocation);
    }

    [Fact]
    public void ReadOnlyAutoFallbackReportsThatItUsedCom()
    {
        var identity = Identity();
        var ctl = new RecordingCtl();
        var comCalls = 0;
        var manager = new IllustratorPluginDebugManager(
            identity,
            ctl,
            (_, _, _) =>
            {
                comCalls++;
                return
                    """
                    {
                      "ok":true,
                      "protocol":"AIPDebug/1",
                      "plugin":"AIPDebug",
                      "pid":4242
                    }
                    """;
            });

        var result = manager.Execute(
            Request(
                IllustratorPluginDebugManager.DiagnosticsOperation,
                """
                {
                  "plugin":"AIPDebug",
                  "action":"stats",
                  "transport":"auto"
                }
                """),
            new object());

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(1, comCalls);
        Assert.Null(ctl.Invocation);
        var value = Value(result);
        Assert.Equal("com", value.GetProperty("transport").GetString());
        Assert.Equal(
            "auto",
            value.GetProperty("requestedTransport").GetString());
        Assert.Contains(
            "No runtime-discovered AIPDebug endpoint",
            value.GetProperty("transportFallback").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void EffectfulControlTreatsPostLaunchHelperFailureAsAmbiguous()
    {
        var identity = Identity();
        var ctl = new RecordingCtl();
        var manager = new IllustratorPluginDebugManager(
            identity,
            ctl,
            (_, _, _) =>
                """
                {
                  "ok":true,
                  "protocol":"AIPDebug/1",
                  "plugin":"AIPDebug",
                  "pid":4242,
                  "endpoint":"aipdbg-authoritative-4242"
                }
                """);

        var discover = manager.Execute(
            Request(
                IllustratorPluginDebugManager.DiagnosticsOperation,
                """
                {
                  "plugin":"AIPDebug",
                  "action":"discover",
                  "transport":"com"
                }
                """),
            new object());
        Assert.True(discover.Ok, discover.Error?.Message);

        ctl.Outcome = new AipDebugCtlOutcome(
            -1,
            string.Empty,
            "helper state became unavailable after launch",
            AipDebugCtlCompletion.ProcessFailure);

        var error = Assert.Throws<HostAdapterException>(
            () => manager.Execute(
                Request(
                    IllustratorPluginDebugManager.ControlOperation,
                    """
                    {
                      "plugin":"AIPDebug",
                      "action":"clear",
                      "transport":"ipc"
                    }
                    """,
                    leaseId: "lease-vectoripc-test-000000000000"),
                new object()));

        Assert.Equal(
            "plugin_debug_ipc_process_failed",
            error.Kind);
        Assert.Equal(
            ExecutionState.Ambiguous,
            error.Execution);
        Assert.False(error.Retryable);
    }

    [Fact]
    public void EffectfulControlTreatsMalformedVectorResponseAsAmbiguous()
    {
        var identity = Identity();
        var ctl = new RecordingCtl();
        var manager = new IllustratorPluginDebugManager(
            identity,
            ctl,
            (_, _, _) =>
                """
                {
                  "ok":true,
                  "protocol":"AIPDebug/1",
                  "plugin":"AIPDebug",
                  "pid":4242,
                  "endpoint":"aipdbg-authoritative-4242"
                }
                """);

        var discover = manager.Execute(
            Request(
                IllustratorPluginDebugManager.DiagnosticsOperation,
                """
                {
                  "plugin":"AIPDebug",
                  "action":"discover",
                  "transport":"com"
                }
                """),
            new object());
        Assert.True(discover.Ok, discover.Error?.Message);

        ctl.Outcome = new AipDebugCtlOutcome(
            AipDebugCtlClient.ExitSuccess,
            "not-json",
            string.Empty,
            AipDebugCtlCompletion.Completed);

        var error = Assert.Throws<HostAdapterException>(
            () => manager.Execute(
                Request(
                    IllustratorPluginDebugManager.ControlOperation,
                    """
                    {
                      "plugin":"AIPDebug",
                      "action":"clear",
                      "transport":"ipc"
                    }
                    """,
                    leaseId: "lease-vectoripc-test-000000000000"),
                new object()));

        Assert.Equal("plugin_debug_invalid_response", error.Kind);
        Assert.Equal(ExecutionState.Ambiguous, error.Execution);
        Assert.False(error.Retryable);
    }

    [Fact]
    public void EffectfulControlTreatsMalformedComResponseAsAmbiguous()
    {
        var manager = new IllustratorPluginDebugManager(
            Identity(),
            new RecordingCtl(),
            (_, _, _) => "not-json");

        var error = Assert.Throws<HostAdapterException>(
            () => manager.Execute(
                Request(
                    IllustratorPluginDebugManager.ControlOperation,
                    """
                    {
                      "plugin":"AIPDebug",
                      "action":"clear",
                      "transport":"com"
                    }
                    """,
                    leaseId: "lease-vectoripc-test-000000000000"),
                new object()));

        Assert.Equal("plugin_debug_invalid_response", error.Kind);
        Assert.Equal(ExecutionState.Ambiguous, error.Execution);
        Assert.False(error.Retryable);
    }

    private static OperationRequest Request(
        string operation,
        string inputJson,
        string? leaseId = null)
    {
        using var input = JsonDocument.Parse(inputJson);
        return new OperationRequest
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = "plugin-debug-vectoripc-test",
            Operation = operation,
            Input = input.RootElement.Clone(),
            Policy = leaseId is null
                ? null
                : new OperationPolicy { LeaseId = leaseId }
        };
    }

    private static JsonElement Value(OperationResult result)
    {
        Assert.NotNull(result.Result);
        Assert.True(result.Result!.Value.HasValue);
        return result.Result.Value.Value;
    }

    private static HostTargetIdentity Identity() =>
        new()
        {
            Host = IllustratorAdapter.HostName,
            ProcessId = 4242,
            ProcessStartedAt =
                DateTimeOffset.Parse("2026-09-26T10:00:00Z"),
            ExecutablePath = @"C:\Adobe\Illustrator.exe",
            HostVersion = "30.6.0",
            AdapterVersion = IllustratorAdapter.CurrentAdapterVersion,
            EndpointIdentity = IllustratorComInterop.ProgId
        };

    private sealed class RecordingCtl : IAipDebugCtlInvoker
    {
        public AipDebugCtlInvocation? Invocation { get; private set; }
        public CancellationToken Token { get; private set; }
        public AipDebugCtlOutcome Outcome { get; set; } =
            new(
                AipDebugCtlClient.ExitSuccess,
                """{"ok":true}""",
                string.Empty,
                AipDebugCtlCompletion.Completed);

        public AipDebugCtlOutcome Invoke(
            AipDebugCtlInvocation invocation,
            CancellationToken cancellationToken)
        {
            Invocation = invocation;
            Token = cancellationToken;
            return Outcome;
        }
    }
}
