using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator.Tests;

/// <summary>
/// Deterministic proof of the debugger session lifecycle and recovery truth
/// exposed by <c>debug.session.status</c>. No Adobe process, no spawned child,
/// and no real idle wait: the child generation is the current process's strong
/// PID + start-time identity, and idle expiry runs through the injected clock
/// and the shared <c>ExpireIdleIfDue</c> decision point.
///
/// Every scenario asserts the same safety rule from a different angle: a session
/// that stopped being command-capable is reported truthfully, and its identifier
/// is never silently rebound to a new session, child, or Illustrator generation.
/// </summary>
public sealed class IllustratorDebugSessionLifecycleTests
{
    [Fact]
    public void StatusBeforeAnySessionReportsNoneWithNoAuthority()
    {
        using var harness = DebugSessionHarness.Create();

        var status = Status(harness);

        Assert.True(status.Ok, status.Error?.Message);
        Assert.Equal("none", Str(status, "sessionState"));
        Assert.Null(Str(status, "sessionId"));
        Assert.Null(Str(status, "engine"));
        Assert.Null(Str(status, "appSpec"));
        Assert.Null(Opt(status, "child"));
        Assert.Null(Opt(status, "provenance"));
        Assert.Null(Str(status, "terminalReason"));
        Assert.Equal(0, Int(status, "commandCount"));
        Assert.True(
            V(status).GetProperty("workerOwned").GetBoolean());

        // Status is observation only: it succeeds with no lease at all and
        // never demands one merely to see whether a session exists.
        Assert.Null(status.Error);
    }

    [Fact]
    public void StatusRejectsAnyInputField()
    {
        using var harness = DebugSessionHarness.Create();
        harness.Open();

        var result = harness.Execute(
            IllustratorDebugSessionManager.StatusOperation,
            "{\"sessionId\":\"x\"}",
            leaseId: null);

        Assert.False(result.Ok);
        Assert.Equal(
            OperationStatus.InvalidRequest,
            result.Status);
        Assert.Equal(
            ExecutionState.NotStarted,
            result.Error?.Execution);
    }

    [Fact]
    public void OpenSessionStatusIsOpenWithBoundedSafeMetadata()
    {
        using var harness = DebugSessionHarness.Create();
        var sessionId = harness.Open(idleTimeoutMs: 120_000);

        var status = Status(harness);

        Assert.True(status.Ok, status.Error?.Message);
        Assert.Equal("open", Str(status, "sessionState"));
        Assert.Equal(sessionId, Str(status, "sessionId"));
        Assert.Equal("main", Str(status, "engine"));
        Assert.Equal("illustrator-30.064", Str(status, "appSpec"));
        Assert.Equal(120_000, Int(status, "idleTimeoutMs"));
        Assert.Equal(0, Int(status, "commandCount"));
        Assert.Equal("estk3", Str(status, "transport"));
        Assert.Null(Opt(status, "lastCommandAt"));
        Assert.Null(Str(status, "terminalReason"));

        // Timestamps come from the injected clock, not the wall clock.
        Assert.Equal(
            harness.Clock.UtcNow,
            Req(status, "openedAt").GetDateTimeOffset());
        Assert.Equal(
            harness.Clock.UtcNow,
            Req(status, "lastUsedAt").GetDateTimeOffset());

        // Strong target generation: PID plus the captured start instant.
        var target = Req(status, "target");
        Assert.Equal(
            harness.Identity.ProcessId,
            target.GetProperty("processId").GetInt32());
        Assert.Equal(
            harness.Identity.ProcessStartedAt,
            target.GetProperty("processStartedAt")
                .GetDateTimeOffset());

        // Strong child generation, alive only on an exact identity match.
        var child = Req(status, "child");
        Assert.Equal(
            harness.Bridge.ProcessId,
            child.GetProperty("processId").GetInt32());
        Assert.Equal(
            harness.Bridge.ProcessStartedAt,
            child.GetProperty("processStartedAt")
                .GetDateTimeOffset());
        Assert.True(
            child.GetProperty("alive").GetBoolean());

        // Hashes and a safe version only.
        var provenance = Req(status, "provenance");
        Assert.False(string.IsNullOrWhiteSpace(
            provenance.GetProperty("addonSha256").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(
            provenance.GetProperty("bridgeSha256").GetString()));
        Assert.Equal(
            "fake-node",
            provenance.GetProperty("nodeVersion").GetString());
    }

    [Fact]
    public void StatusNeverLeaksPathsLeaseSourceOrDocumentContent()
    {
        using var harness = DebugSessionHarness.Create();
        harness.Open();

        // Send exactly the payloads status must never echo back.
        Assert.True(harness.Command(
            "set-breakpoints",
            "\"breakpoints\":[{\"file\":\"file:///C:/secret/probe.jsx\"," +
            "\"line\":7,\"condition\":\"secretCondition > 3\"}]").Ok);
        Assert.True(harness.Command(
            "eval",
            "\"source\":\"var secretSource = 42; secretSource;\"").Ok);

        var status = Status(harness);
        Assert.True(status.Ok, status.Error?.Message);

        var json = V(status).GetRawText();
        foreach (var forbidden in new[]
                 {
                     "node.exe",
                     "C:",
                     "\\",
                     "esdcorelibinterface",
                     "esd-debugger-bridge",
                     "COMTOOL_ESD_ADDON_PATH",
                     ".jsx",
                     "secret",
                     "lease-",
                     DebugSessionHarness.LeaseId
                 })
        {
            Assert.DoesNotContain(
                forbidden,
                json,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SuccessfulCommandIncrementsCommandCountAndLastCommandAt()
    {
        using var harness = DebugSessionHarness.Create();
        harness.Open();

        Assert.Null(Opt(Status(harness), "lastCommandAt"));

        harness.Advance(TimeSpan.FromSeconds(30));
        Assert.True(harness.Command("get-breakpoints").Ok);

        var afterFirst = Status(harness);
        Assert.Equal(1, Int(afterFirst, "commandCount"));
        Assert.Equal(
            harness.Clock.UtcNow,
            Req(afterFirst, "lastCommandAt").GetDateTimeOffset());
        Assert.Equal(
            harness.Clock.UtcNow,
            Req(afterFirst, "lastUsedAt").GetDateTimeOffset());

        harness.Advance(TimeSpan.FromSeconds(5));
        Assert.True(harness.Command("get-frame").Ok);

        var afterSecond = Status(harness);
        Assert.Equal(2, Int(afterSecond, "commandCount"));
        Assert.Equal(
            harness.Clock.UtcNow,
            Req(afterSecond, "lastCommandAt").GetDateTimeOffset());
    }

    [Fact]
    public void ProvenBeforeSendRejectionLeavesSessionOpenAndUncounted()
    {
        using var harness = DebugSessionHarness.Create();
        harness.Open();

        harness.Bridge.Enqueue(
            FakeBridgeBehavior.RejectedBeforeSend());

        Assert.Throws<HostAdapterException>(
            () => harness.Command("get-properties"));

        var status = Status(harness);

        // A provably unsent request neither retires the session nor counts as a
        // completed debugger command, because execution never happened.
        Assert.Equal("open", Str(status, "sessionState"));
        Assert.Equal(0, Int(status, "commandCount"));
        Assert.Null(Opt(status, "lastCommandAt"));
        Assert.False(harness.Bridge.Disposed);
    }

    [Fact]
    public void ExplicitCloseReportsClosedByCallerAndDropsLiveAuthority()
    {
        using var harness = DebugSessionHarness.Create();
        var sessionId = harness.Open();

        var closed = harness.Close();
        Assert.True(closed.Ok, closed.Error?.Message);
        Assert.True(
            V(closed).GetProperty("closed").GetBoolean());
        Assert.True(harness.Bridge.Closed);

        var status = Status(harness);
        Assert.Equal("closed", Str(status, "sessionState"));
        Assert.Equal(
            "closed_by_caller",
            Str(status, "terminalReason"));
        Assert.Equal(sessionId, Str(status, "sessionId"));
        // Bounded history is retained, but it can no longer be used.
        Assert.Equal("main", Str(status, "engine"));

        var later = harness.Command("get-breakpoints");
        Assert.Equal("debug_session_not_open", Kind(later));
        Assert.Equal(
            ExecutionState.NotStarted,
            later.Error?.Execution);
    }

    [Fact]
    public void CloseTeardownFailureReportsCloseFailedAndIsNeverReactivated()
    {
        using var harness = DebugSessionHarness.Create();
        var sessionId = harness.Open();
        harness.Bridge.ThrowOnClose = true;

        var closed = harness.Close();

        // A failed teardown is not a successful close, and it is never
        // ambiguous: nothing was submitted to Adobe.
        Assert.False(closed.Ok);
        Assert.Equal(OperationStatus.Failed, closed.Status);
        Assert.Equal(TargetState.Known, closed.TargetState);
        Assert.Equal(
            ExecutionState.NotStarted,
            closed.Error?.Execution);
        Assert.False(closed.Error?.Retryable);
        Assert.Equal(
            "debug_session_close_failed",
            closed.Error?.Kind);

        var status = Status(harness);
        Assert.Equal("faulted", Str(status, "sessionState"));
        Assert.Equal(
            "close_failed",
            Str(status, "terminalReason"));
        Assert.Equal(sessionId, Str(status, "sessionId"));

        // Never reactivated: the retired identifier stays dead.
        var later = harness.Command("get-breakpoints");
        Assert.Equal("debug_session_not_open", Kind(later));
        Assert.Equal(
            ExecutionState.NotStarted,
            later.Error?.Execution);
    }

    [Fact]
    public void IdleExpiryClosesTheSessionWithoutAnyRealWait()
    {
        using var harness = DebugSessionHarness.Create();
        var sessionId = harness.Open(
            idleTimeoutMs:
                IllustratorDebugSessionManager.MinIdleTimeoutMs);

        // Not yet due: the same decision point must leave the session alone.
        harness.Advance(TimeSpan.FromMilliseconds(1_000));
        harness.Manager.ExpireIdleIfDue();
        Assert.Equal(
            "open",
            Str(Status(harness), "sessionState"));
        Assert.False(harness.Bridge.Disposed);

        harness.Advance(
            TimeSpan.FromMilliseconds(
                IllustratorDebugSessionManager.MinIdleTimeoutMs));
        harness.Manager.ExpireIdleIfDue();

        var status = Status(harness);
        Assert.Equal("closed", Str(status, "sessionState"));
        Assert.Equal(
            "idle_expired",
            Str(status, "terminalReason"));
        Assert.Equal(sessionId, Str(status, "sessionId"));
        Assert.True(harness.Bridge.Disposed);

        var later = harness.Command("get-breakpoints");
        Assert.Equal("debug_session_not_open", Kind(later));
    }

    [Fact]
    public void CommandUseRefreshesTheIdleDeadline()
    {
        using var harness = DebugSessionHarness.Create();
        harness.Open(
            idleTimeoutMs:
                IllustratorDebugSessionManager.MinIdleTimeoutMs);

        // Repeated use inside the window must never expire the session.
        for (var i = 0; i < 3; i++)
        {
            harness.Advance(TimeSpan.FromSeconds(3));
            Assert.True(harness.Command("get-breakpoints").Ok);
            harness.Manager.ExpireIdleIfDue();
        }

        Assert.Equal(
            "open",
            Str(Status(harness), "sessionState"));
        Assert.Equal(3, Int(Status(harness), "commandCount"));
    }

    [Fact]
    public void RecycledChildPidIsNeverReportedAlive()
    {
        // A live PID whose captured start instant does not match is a different
        // process, so liveness must be decided on identity, not on PID
        // existence.
        Assert.True(
            IllustratorDebugSessionManager.IsGenerationAlive(
                Environment.ProcessId,
                FakeDebuggerBridge.CurrentProcessStart));

        Assert.False(
            IllustratorDebugSessionManager.IsGenerationAlive(
                Environment.ProcessId,
                FakeDebuggerBridge.CurrentProcessStart
                    .AddSeconds(1)));

        Assert.False(
            IllustratorDebugSessionManager.IsGenerationAlive(
                0x7FFFFFF0,
                DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void StatusRetiresSessionWhenStrongChildGenerationIsGone()
    {
        // The child generation was captured from a different process
        // generation, so strong evidence says it can never be used again.
        var bridge = new FakeDebuggerBridge(
            processStartedAt:
                FakeDebuggerBridge.CurrentProcessStart
                    .AddSeconds(1));
        using var harness = DebugSessionHarness.Create(bridge);
        var sessionId = harness.Open();

        // The same status observation that reports liveness also performs the
        // internal bookkeeping, so it can never keep claiming an open session.
        var status = Status(harness);
        Assert.Equal("faulted", Str(status, "sessionState"));
        Assert.Equal(
            "bridge_child_unavailable",
            Str(status, "terminalReason"));
        Assert.Equal(sessionId, Str(status, "sessionId"));
        Assert.False(
            Req(status, "child").GetProperty("alive")
                .GetBoolean());
        Assert.True(bridge.Disposed);

        var later = harness.Command("get-breakpoints");
        Assert.Equal("debug_session_not_open", Kind(later));
    }

    [Fact]
    public void TargetGenerationLossFaultsTheSessionBeforeNotStarted()
    {
        using var harness = DebugSessionHarness.Create();
        var sessionId = harness.Open();

        // The bound Illustrator generation is lost mid-session.
        harness.TargetGenerationLost = true;

        var failure = Assert.Throws<HostAdapterException>(
            () => harness.Command("get-breakpoints"));

        // The pre-existing not-started target-generation failure is preserved
        // exactly; observability did not reclassify it.
        Assert.Equal(
            "target_generation_changed",
            failure.Kind);
        Assert.Equal(
            ExecutionState.NotStarted,
            failure.Execution);
        Assert.False(failure.Retryable);

        var status = Status(harness);
        Assert.Equal("faulted", Str(status, "sessionState"));
        Assert.Equal(
            "target_generation_lost",
            Str(status, "terminalReason"));
        Assert.Equal(sessionId, Str(status, "sessionId"));
        Assert.True(harness.Bridge.Disposed);

        // Nothing was submitted and no substitute target was discovered: the
        // request never reached the transport.
        Assert.Equal(0, harness.Bridge.CommandExchanges);
        var later = harness.Command("get-breakpoints");
        Assert.Equal("debug_session_not_open", Kind(later));
    }

    [Fact]
    public void StaleSessionIdAfterReopenIsRefusedAndNeverRebound()
    {
        using var harness = DebugSessionHarness.Create();
        var first = harness.Open();
        Assert.True(harness.Close().Ok);

        var second = harness.Open();
        Assert.NotEqual(first, second);

        // The replacement session is genuinely usable.
        Assert.True(harness.Command("get-breakpoints").Ok);
        Assert.Equal(
            second,
            Str(Status(harness), "sessionId"));

        // The retired identifier stays dead. It is never rebound to the new
        // session and never silently reopens anything. A replacement session is
        // live, so the truthful report is an identity mismatch rather than
        // "no session" — and either way nothing started.
        var stale = harness.Execute(
            IllustratorDebugSessionManager.CommandOperation,
            "{\"sessionId\":\"" + first +
            "\",\"command\":\"get-breakpoints\"}",
            DebugSessionHarness.LeaseId);
        Assert.Equal(
            "debug_session_identity_mismatch",
            Kind(stale));
        Assert.Equal(
            ExecutionState.NotStarted,
            stale.Error?.Execution);

        // Closing the retired identifier is refused too.
        Assert.Equal(
            "debug_session_identity_mismatch",
            Kind(harness.Close(first)));
    }

    [Fact]
    public void FreshManagerHasNoSessionAndCannotResurrectAnOldOne()
    {
        using var first = DebugSessionHarness.Create();
        var oldSessionId = first.Open();
        Assert.True(first.Close().Ok);

        // A replacement worker/runtime starts with no memory of the old
        // session: there is no reconnect token to present and no terminal
        // record to inherit.
        using var second = DebugSessionHarness.Create();
        var status = Status(second);
        Assert.Equal("none", Str(status, "sessionState"));
        Assert.Null(Str(status, "sessionId"));
        Assert.Null(Str(status, "terminalReason"));

        var reused = second.Execute(
            IllustratorDebugSessionManager.CommandOperation,
            "{\"sessionId\":\"" + oldSessionId +
            "\",\"command\":\"get-breakpoints\"}",
            DebugSessionHarness.LeaseId);
        Assert.Equal("debug_session_not_open", Kind(reused));
        Assert.Equal(
            ExecutionState.NotStarted,
            reused.Error?.Execution);
        Assert.Equal(0, second.Bridge.CommandExchanges);
    }

    [Fact]
    public void TerminalHistoryKeepsOnlyTheMostRecentRecord()
    {
        using var harness = DebugSessionHarness.Create();

        var first = harness.Open();
        Assert.True(harness.Close(first).Ok);
        Assert.Equal(
            first,
            Str(Status(harness), "sessionId"));

        var second = harness.Open();
        Assert.True(harness.Close(second).Ok);

        // Exactly one record is retained, and it is the most recent one.
        var status = Status(harness);
        Assert.Equal("closed", Str(status, "sessionState"));
        Assert.Equal(
            "closed_by_caller",
            Str(status, "terminalReason"));
        Assert.Equal(second, Str(status, "sessionId"));

        // The superseded session is gone from the snapshot entirely.
        Assert.DoesNotContain(
            first,
            V(status).GetRawText(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyOneSessionMayBeOpenPerWorker()
    {
        using var harness = DebugSessionHarness.Create();
        var sessionId = harness.Open();

        var second = harness.Execute(
            IllustratorDebugSessionManager.OpenOperation,
            "{\"engine\":\"main\"}",
            DebugSessionHarness.LeaseId);

        Assert.False(second.Ok);
        Assert.Equal(
            "debug_session_already_open",
            second.Error?.Kind);
        Assert.Equal(
            ExecutionState.Completed,
            second.Error?.Execution);
        Assert.Equal(
            sessionId,
            Str(Status(harness), "sessionId"));
    }

    private static OperationResult Status(
        DebugSessionHarness harness) =>
        harness.Status();

    /// <summary>
    /// The unwrapped result object. <see cref="ProtocolValue.Value"/> is a
    /// nullable struct, so it needs a real unwrap rather than a null-forgiving
    /// operator.
    /// </summary>
    private static JsonElement V(OperationResult result) =>
        result.Result!.Value!.Value;

    /// <summary>
    /// A property that must be present, used for the strongly typed
    /// nested-object assertions.
    /// </summary>
    private static JsonElement Req(
        OperationResult result,
        string name) =>
        V(result).GetProperty(name);

    /// <summary>
    /// A property that is expected to be absent or JSON null, so a genuinely
    /// empty authority field is distinguishable from a missing one.
    /// </summary>
    private static JsonElement? Opt(
        OperationResult result,
        string name)
    {
        if (result.Result?.Value is not { } root ||
            root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(name, out var found))
        {
            return null;
        }

        return found.ValueKind == JsonValueKind.Null
            ? null
            : found;
    }

    private static string? Str(
        OperationResult result,
        string name) =>
        Opt(result, name)?.ToString();

    private static int Int(OperationResult result, string name) =>
        V(result).GetProperty(name).GetInt32();

    private static string? Kind(OperationResult result) =>
        result.Error?.Kind;
}
