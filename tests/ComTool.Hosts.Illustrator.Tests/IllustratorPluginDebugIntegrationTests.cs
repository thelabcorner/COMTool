using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator.Tests;

public sealed class IllustratorPluginDebugIntegrationTests
{
    [Fact]
    public void CapabilitiesSplitDiagnosticsAndControlByEffect()
    {
        var diagnostics = Assert.Single(
            IllustratorOperations.Capabilities,
            capability =>
                capability.Name ==
                IllustratorPluginDebugManager.DiagnosticsOperation);
        Assert.Equal(MutationClass.ReadOnly, diagnostics.MutationClass);
        Assert.True(diagnostics.Supported);
        Assert.Equal("illustrator", diagnostics.Host);

        var control = Assert.Single(
            IllustratorOperations.Capabilities,
            capability =>
                capability.Name ==
                IllustratorPluginDebugManager.ControlOperation);
        Assert.Equal(
            MutationClass.ExternalSideEffect,
            control.MutationClass);
        Assert.True(control.Supported);
        Assert.Equal("illustrator", control.Host);
    }

    [Fact]
    public void ActionSetsCannotCrossTheCatalogEffectBoundary()
    {
        var diagnostics = Request(
            IllustratorPluginDebugManager.DiagnosticsOperation,
            """
            {"plugin":"AIPDebug","action":"clear"}
            """);

        var diagnosticError = Assert.Throws<ArgumentException>(
            () => PluginDebugRequest.Parse(diagnostics));
        Assert.Contains(
            "not a plugin.debug.diagnostics action",
            diagnosticError.Message,
            StringComparison.Ordinal);

        var control = Request(
            IllustratorPluginDebugManager.ControlOperation,
            """
            {"plugin":"AIPDebug","action":"snapshot"}
            """,
            leaseId: "lease-plugin-debug-test-000000000000");

        var controlError = Assert.Throws<ArgumentException>(
            () => PluginDebugRequest.Parse(control));
        Assert.Contains(
            "not a plugin.debug.control action",
            controlError.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionRoutesDiagnosticsThroughWorkerOwnedManager()
    {
        var identity = Identity();
        var app = new FakePluginDebugApplication();
        var scriptExecutorCalled = false;

        var result = RunSta(() =>
        {
            var session = new IllustratorSession(
                identity,
                app,
                (_, _, _) =>
                {
                    scriptExecutorCalled = true;
                    return "{}";
                });

            try
            {
                return session.ExecuteAsync(
                        Request(
                            IllustratorPluginDebugManager.DiagnosticsOperation,
                            """
                            {
                              "plugin":"AIPDebug",
                              "action":"discover",
                              "transport":"com"
                            }
                            """) with
                        {
                            Target = new TargetRef(
                                identity.Host,
                                identity.TargetId,
                                Generation: 0)
                        })
                    .GetAwaiter()
                    .GetResult();
            }
            finally
            {
                session.DisposeAsync()
                    .GetAwaiter()
                    .GetResult();
            }
        });

        Assert.True(result.Ok);
        Assert.Equal(OperationStatus.Completed, result.Status);
        Assert.False(scriptExecutorCalled);
        Assert.Equal(1, app.Calls);
        Assert.Equal("AIPDebug", app.Plugin);
        Assert.Equal("AIPDebug/1/discover", app.Selector);
        Assert.Equal(string.Empty, app.Payload);
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
            Id = "plugin-debug-integration-test",
            Operation = operation,
            Input = input.RootElement.Clone(),
            Policy = leaseId is null
                ? null
                : new OperationPolicy { LeaseId = leaseId }
        };
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

    private sealed class FakePluginDebugApplication
    {
        public int Calls { get; private set; }
        public string? Plugin { get; private set; }
        public string? Selector { get; private set; }
        public string? Payload { get; private set; }

        public string SendScriptMessage(
            string plugin,
            string selector,
            string payload)
        {
            Calls++;
            Plugin = plugin;
            Selector = selector;
            Payload = payload;
            return "{}";
        }
    }
}
