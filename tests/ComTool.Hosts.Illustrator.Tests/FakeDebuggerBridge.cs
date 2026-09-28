using System.Diagnostics;
using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator.Tests;

/// <summary>
/// Test-only debugger bridge. It never contacts Adobe, records every exchange,
/// and can fail only in the ways the real bridge reports failures, so no
/// assertion can pass by inferring submission from elapsed time.
///
/// The default child generation is the *current process's* strong PID +
/// start-time identity. That keeps the production generation-safe liveness
/// check fully exercised — a real, exactly matching generation — so a
/// deterministic test never needs a spawned child and production liveness is
/// never weakened to accommodate a test.
/// </summary>
internal sealed class FakeDebuggerBridge : IDebuggerBridge
{
    private readonly Queue<FakeBridgeBehavior> _scripted = new();

    public FakeDebuggerBridge(
        string? appSpecifier = null,
        int? processId = null,
        DateTimeOffset? processStartedAt = null)
    {
        AppSpecifier = appSpecifier ?? "illustrator-30.064";
        ProcessId = processId ?? Environment.ProcessId;
        ProcessStartedAt = processStartedAt ?? CurrentProcessStart;
    }

    /// <summary>
    /// The current process's real start instant, read once. Used as the
    /// default child generation so liveness is a genuine strong-identity match.
    /// </summary>
    public static DateTimeOffset CurrentProcessStart { get; } =
        ReadCurrentProcessStart();

    public string AppSpecifier { get; }
    public int ProcessId { get; }
    public DateTimeOffset ProcessStartedAt { get; }
    public string? NodeVersion => "fake-node";

    /// <summary>Exchanges issued after the implicit connect probe.</summary>
    public int CommandExchanges { get; private set; }

    public bool Closed { get; private set; }
    public bool Disposed { get; private set; }
    public List<string> Bodies { get; } = [];

    /// <summary>When set, <see cref="Close"/> throws to model failed teardown.</summary>
    public bool ThrowOnClose { get; set; }

    public void Enqueue(FakeBridgeBehavior behavior) =>
        _scripted.Enqueue(behavior);

    public JsonElement Exchange(
        string body,
        int timeoutMs)
    {
        Bodies.Add(body);

        if (string.Equals(
                body,
                "<connect/>",
                StringComparison.Ordinal))
        {
            return Parse(
                """
                {
                  "ok": true,
                  "appSpec": "illustrator-30.064",
                  "events": [
                    {
                      "reason": 1,
                      "serialNumber": 1,
                      "bodyTag": "engines",
                      "body": "<engines><engine name=\"main\"/><engine name=\"transient\"/></engines>"
                    }
                  ]
                }
                """);
        }

        CommandExchanges++;

        if (_scripted.Count > 0)
        {
            var behavior = _scripted.Dequeue();
            throw new DebuggerBridgeException(
                behavior.Message,
                behavior.Fault);
        }

        return Parse(
            """
            {
              "ok": true,
              "events": [
                {
                  "reason": 3,
                  "serialNumber": 4,
                  "resultSerial": 3,
                  "bodyTag": "evalresult",
                  "body": "<evalresult engine=\"main\"><value type=\"number\"><![CDATA[42]]></value></evalresult>"
                }
              ]
            }
            """);
    }

    public DebuggerBridgeCloseResult Close()
    {
        if (ThrowOnClose)
            throw new InvalidOperationException(
                "synthetic teardown failure");

        Closed = true;
        return new DebuggerBridgeCloseResult(
            Graceful: true,
            ExitObserved: true);
    }

    public void Dispose() => Disposed = true;

    private static DateTimeOffset ReadCurrentProcessStart()
    {
        using var self = Process.GetProcessById(
            Environment.ProcessId);
        return new DateTimeOffset(self.StartTime);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

/// <summary>One scripted transport outcome for the fake bridge.</summary>
internal sealed record FakeBridgeBehavior(
    DebuggerBridgeFault Fault,
    string Message)
{
    public static FakeBridgeBehavior RejectedBeforeSend() =>
        new(
            DebuggerBridgeFault.RejectedBeforeSend,
            "Debugger bridge rejected the request before sending it: body exceeds the 1 MiB UTF-8 transport limit.");

    public static FakeBridgeBehavior SubmittedThenLost() =>
        new(
            DebuggerBridgeFault.TransportLostAfterSubmit,
            "Timed out waiting for debugger bridge response after 10000 ms.");

    public static FakeBridgeBehavior ChildDied() =>
        new(
            DebuggerBridgeFault.ChildUnavailable,
            "Debugger bridge is not running.");
}

/// <summary>
/// Owns one <see cref="IllustratorDebugSessionManager"/> wired to test seams.
/// The target generation is the current process's strong PID + start-time
/// identity, so the production generation check passes without any Adobe
/// process, and a controllable clock makes idle expiry deterministic with no
/// real wait.
/// </summary>
internal sealed class DebugSessionHarness : IDisposable
{
    public const string LeaseId =
        "lease-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly TemporaryAddon _addon;
    private readonly TestClock _clock;
    private int _requestId;

    private DebugSessionHarness(
        FakeDebuggerBridge bridge,
        IllustratorDebugSessionManager manager,
        TemporaryAddon addon,
        TestClock clock)
    {
        Bridge = bridge;
        Manager = manager;
        _addon = addon;
        _clock = clock;
    }

    public FakeDebuggerBridge Bridge { get; }
    public IllustratorDebugSessionManager Manager { get; }
    public TestClock Clock => _clock;
    public HostTargetIdentity Identity { get; private set; } = null!;

    /// <summary>The session ID reported by the most recent successful open.</summary>
    public string? SessionId { get; private set; }

    /// <summary>
    /// When true, the injected target-generation verifier reports the host as
    /// lost, modelling Illustrator exiting or being recycled mid-session.
    /// </summary>
    public bool TargetGenerationLost { get; set; }

    public static DebugSessionHarness Create(
        FakeDebuggerBridge? bridge = null)
    {
        var identity = new HostTargetIdentity
        {
            Host = "illustrator",
            ProcessId = Environment.ProcessId,
            ProcessStartedAt =
                FakeDebuggerBridge.CurrentProcessStart,
            ExecutablePath =
                Environment.ProcessPath ?? "testhost.exe",
            HostVersion = "test",
            AdapterVersion = "test",
            EndpointIdentity = "debug-session-lifecycle"
        };

        var addon = new TemporaryAddon();
        var clock = new TestClock(
            new DateTimeOffset(2026, 9, 26, 8, 0, 0, TimeSpan.Zero));
        var fake = bridge ?? new FakeDebuggerBridge();
        var harnessRef = new StrongBox<DebugSessionHarness>();

        var manager = new IllustratorDebugSessionManager(
            identity,
            () => new DebuggerDependencyStatus(
                true,
                @"C:\fake\node.exe",
                addon.Path,
                null),
            _ => fake,
            current =>
            {
                if (harnessRef.Value?.TargetGenerationLost == true)
                {
                    throw new HostAdapterException(
                        "target_generation_changed",
                        "Illustrator process generation changed while a debugger request was being prepared.",
                        retryable: false,
                        ExecutionState.NotStarted);
                }

                IllustratorDebugSessionManager
                    .VerifyTargetGeneration(current);
            },
            () => clock.UtcNow);

        var harness = new DebugSessionHarness(
            fake,
            manager,
            addon,
            clock);
        harness.Identity = identity;
        harnessRef.Value = harness;
        return harness;
    }

    public string Open(
        int idleTimeoutMs = 600_000,
        string engine = "main")
    {
        var result = Execute(
            IllustratorDebugSessionManager.OpenOperation,
            "{\"engine\":\"" + engine +
            "\",\"idleTimeoutMs\":" + idleTimeoutMs +
            ",\"connectTimeoutMs\":5000}",
            LeaseId);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(0, Bridge.CommandExchanges);
        SessionId = result.Result?.Value?
            .GetProperty("sessionId")
            .GetString();
        Assert.False(string.IsNullOrWhiteSpace(SessionId));
        return SessionId!;
    }

    public OperationResult Command(
        string command,
        string extraJson = "",
        string? leaseId = null) =>
        Execute(
            IllustratorDebugSessionManager.CommandOperation,
            "{\"sessionId\":\"$SESSION\",\"command\":\"" +
            command + "\"" +
            (extraJson.Length == 0 ? "" : "," + extraJson) +
            "}",
            leaseId ?? LeaseId);

    public OperationResult Close(
        string? sessionId = null,
        string? leaseId = null) =>
        Execute(
            IllustratorDebugSessionManager.CloseOperation,
            "{\"sessionId\":\"" + (sessionId ?? SessionId) + "\"}",
            leaseId ?? LeaseId);

    /// <summary>
    /// Status is observation only, so it is sent with no policy at all: a
    /// caller must not need a lease merely to see whether a session exists.
    /// </summary>
    public OperationResult Status() =>
        Execute(
            IllustratorDebugSessionManager.StatusOperation,
            "{}",
            leaseId: null);

    public OperationResult Execute(
        string operation,
        string inputTemplate,
        string? leaseId)
    {
        using var input = JsonDocument.Parse(
            inputTemplate.Replace(
                "$SESSION",
                SessionId ?? "dbg-unknown",
                StringComparison.Ordinal));

        return Manager.Execute(
            new OperationRequest
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = $"req-{operation}-{++_requestId}",
                Operation = operation,
                Input = input.RootElement,
                Policy = leaseId is null
                    ? null
                    : new OperationPolicy(LeaseId: leaseId)
            });
    }

    public void Advance(TimeSpan delta) =>
        _clock.Advance(delta);

    public void Dispose()
    {
        Manager.Dispose();
        _addon.Dispose();
    }

    internal sealed class TestClock(DateTimeOffset start)
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan delta) => UtcNow += delta;
    }

    private sealed class StrongBox<T>
    {
        public T? Value { get; set; }
    }

    private sealed class TemporaryAddon : IDisposable
    {
        public TemporaryAddon()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "comtool-v2-fake-addon-" +
                Guid.NewGuid().ToString("N") + ".node");
            File.WriteAllBytes(Path, [0x4D, 0x5A, 0x00, 0x01]);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch
            {
                // Scratch cleanup is best effort.
            }
        }
    }
}
