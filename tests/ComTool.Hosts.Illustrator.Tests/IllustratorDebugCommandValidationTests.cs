using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator.Tests;

/// <summary>
/// Proves the debugger command contract's core safety invariant without an
/// Adobe host or a spawned bridge child: every deterministic pre-submit
/// validation failure is reported as not-started, must not reach the transport,
/// must not tear the session down, and must leave the same session able to
/// execute a subsequent valid command.
///
/// The fake bridge is shared with the lifecycle suite and scripts failures
/// explicitly as <c>before_send</c> rejection, lost child, and
/// possibly-submitted loss, so no assertion can pass by inferring submission
/// from elapsed time.
/// </summary>
public sealed class IllustratorDebugCommandValidationTests
{
    private const string OtherLeaseId =
        "lease-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void InvalidTimeoutIsNotStartedAndLeavesTheSessionUsable()
    {
        using var harness = DebugSessionHarness.Create();
        harness.Open();

        var result = harness.Command(
            "get-breakpoints",
            "\"timeoutMs\":10");

        AssertRejectedBeforeSubmit(result, "invalid_debug_request");
        Assert.Equal(0, harness.Bridge.CommandExchanges);
        Assert.False(harness.Bridge.Disposed);

        var after = harness.Command("get-breakpoints");

        Assert.True(after.Ok, after.Error?.Message);
        Assert.Equal(1, harness.Bridge.CommandExchanges);
        Assert.False(harness.Bridge.Disposed);
    }

    [Fact]
    public void MalformedCommandSpecificFieldIsNotStartedAndLeavesTheSessionUsable()
    {
        using var harness = DebugSessionHarness.Create();
        harness.Open();

        // 'frame' belongs to get-frame; supplying it to get-breakpoints is an
        // unknown field for that command's contract.
        var unknownField = harness.Command(
            "get-breakpoints",
            "\"frame\":3");
        AssertRejectedBeforeSubmit(
            unknownField,
            "invalid_debug_request");
        Assert.Contains(
            "Unknown debugger field",
            unknownField.Error?.Message,
            StringComparison.Ordinal);

        // A malformed breakpoint entry is also a pure contract failure.
        var malformedBreakpoint = harness.Command(
            "set-breakpoints",
            "\"breakpoints\":[{\"file\":\"file:///probe.jsx\"," +
            "\"line\":-4}]");
        AssertRejectedBeforeSubmit(
            malformedBreakpoint,
            "invalid_debug_request");

        // eval without its required source.
        var missingSource = harness.Command(
            "eval",
            "\"debugLevel\":1");
        AssertRejectedBeforeSubmit(
            missingSource,
            "invalid_debug_request");

        // A wrong field type on a shared field.
        var wrongType = harness.Command(
            "eval",
            "\"source\":\"1\",\"debugLevel\":\"high\"");
        AssertRejectedBeforeSubmit(
            wrongType,
            "invalid_debug_request");

        Assert.Equal(0, harness.Bridge.CommandExchanges);
        Assert.False(harness.Bridge.Disposed);

        var after = harness.Command(
            "set-breakpoints",
            "\"breakpoints\":[{\"file\":\"file:///probe.jsx\"," +
            "\"line\":4}]");

        Assert.True(after.Ok, after.Error?.Message);
        Assert.Equal(1, harness.Bridge.CommandExchanges);
        Assert.False(harness.Bridge.Disposed);
    }

    [Fact]
    public void UnsupportedCommandIsNotStartedAndReportedAsUnsupported()
    {
        using var harness = DebugSessionHarness.Create();
        harness.Open();

        var result = harness.Command("detach-everything");

        AssertRejectedBeforeSubmit(result, "invalid_debug_request");
        Assert.Contains(
            "Unsupported debugger command",
            result.Error?.Message,
            StringComparison.Ordinal);
        Assert.Equal(0, harness.Bridge.CommandExchanges);
        Assert.False(harness.Bridge.Disposed);

        // An unsupported command must not be masked by an unrelated timeout
        // error about a request that would never be sent.
        var withBadTimeout = harness.Command(
            "detach-everything",
            "\"timeoutMs\":5");
        AssertRejectedBeforeSubmit(
            withBadTimeout,
            "invalid_debug_request");
        Assert.Contains(
            "Unsupported debugger command",
            withBadTimeout.Error?.Message,
            StringComparison.Ordinal);

        var after = harness.Command("get-break");

        Assert.True(after.Ok, after.Error?.Message);
        Assert.Equal(1, harness.Bridge.CommandExchanges);
    }

    [Fact]
    public void MissingLeaseIsRejectedBeforeAnySessionLookup()
    {
        using var harness = DebugSessionHarness.Create();
        var sessionId = harness.Open();

        var result = harness.Execute(
            IllustratorDebugSessionManager.CommandOperation,
            "{\"sessionId\":\"" + sessionId +
            "\",\"command\":\"get-breakpoints\"}",
            leaseId: null);

        AssertRejectedBeforeSubmit(result, "invalid_debug_request");
        Assert.Contains(
            "policy.leaseId",
            result.Error?.Message,
            StringComparison.Ordinal);
        Assert.Equal(0, harness.Bridge.CommandExchanges);
        Assert.False(harness.Bridge.Disposed);
    }

    [Fact]
    public void WrongLeaseOrSessionIdentityIsNotStartedAndPreservesTheSession()
    {
        using var harness = DebugSessionHarness.Create();
        harness.Open();

        // Same length, different value: exercises the fixed-time comparison.
        var wrongLease = harness.Command(
            "get-breakpoints",
            leaseId: OtherLeaseId);
        AssertAuthorityRefusal(
            wrongLease,
            "debug_session_lease_mismatch");

        // Different length: must be classified, not thrown out of
        // FixedTimeEquals and reported as malformed input.
        var shortLease = harness.Command(
            "get-breakpoints",
            leaseId: "short");
        AssertAuthorityRefusal(
            shortLease,
            "debug_session_lease_mismatch");

        var wrongSession = harness.Execute(
            IllustratorDebugSessionManager.CommandOperation,
            "{\"sessionId\":\"dbg-somebody-else\"," +
            "\"command\":\"get-breakpoints\"}",
            DebugSessionHarness.LeaseId);
        AssertAuthorityRefusal(
            wrongSession,
            "debug_session_identity_mismatch");

        Assert.Equal(0, harness.Bridge.CommandExchanges);
        Assert.False(harness.Bridge.Disposed);

        var after = harness.Command("get-breakpoints");
        Assert.True(after.Ok, after.Error?.Message);
        Assert.Equal(1, harness.Bridge.CommandExchanges);
        Assert.False(harness.Bridge.Disposed);
    }

    [Fact]
    public void ClosingAForeignSessionIsNotStartedAndPreservesTheOwnedSession()
    {
        using var harness = DebugSessionHarness.Create();
        harness.Open();

        var wrongSession = harness.Close("dbg-somebody-else");
        AssertAuthorityRefusal(
            wrongSession,
            "debug_session_identity_mismatch");
        Assert.False(harness.Bridge.Closed);
        Assert.False(harness.Bridge.Disposed);

        var after = harness.Command("get-frame");
        Assert.True(after.Ok, after.Error?.Message);
        Assert.Equal(1, harness.Bridge.CommandExchanges);
    }

    [Fact]
    public void ProvenBeforeSendRejectionKeepsTheSessionAlive()
    {
        using var harness = DebugSessionHarness.Create();
        harness.Open();

        // The child refused the request locally and provably sent nothing.
        harness.Bridge.Enqueue(
            FakeBridgeBehavior.RejectedBeforeSend());

        var rejection = Assert.Throws<HostAdapterException>(
            () => harness.Command("get-properties"));

        Assert.Equal(
            "debugger_request_rejected",
            rejection.Kind);
        Assert.Equal(
            ExecutionState.NotStarted,
            rejection.Execution);
        Assert.False(rejection.Retryable);

        // The rejected request reached the transport, and the session survived
        // it, so the very next valid command still works.
        Assert.Equal(1, harness.Bridge.CommandExchanges);
        Assert.False(harness.Bridge.Disposed);

        var after = harness.Command("get-properties");
        Assert.True(after.Ok, after.Error?.Message);
        Assert.Equal(2, harness.Bridge.CommandExchanges);
        Assert.False(harness.Bridge.Disposed);
    }

    [Fact]
    public void PossiblySubmittedFailureRetiresTheSessionAndIsReportedAmbiguous()
    {
        using var harness = DebugSessionHarness.Create();
        harness.Open();

        harness.Bridge.Enqueue(
            FakeBridgeBehavior.SubmittedThenLost());

        var ambiguous = Assert.Throws<HostAdapterException>(
            () => harness.Command("continue"));

        Assert.Equal(
            "debugger_transport_lost_after_submit",
            ambiguous.Kind);
        Assert.Equal(
            ExecutionState.Ambiguous,
            ambiguous.Execution);
        Assert.False(ambiguous.Retryable);

        // A possibly-submitted debugger request is never retried, and the
        // bridge it was submitted through is never reused.
        Assert.True(harness.Bridge.Disposed);
        Assert.Equal(1, harness.Bridge.CommandExchanges);

        var later = harness.Command("get-breakpoints");
        AssertAuthorityRefusal(
            later,
            "debug_session_not_open");
        Assert.Equal(1, harness.Bridge.CommandExchanges);
    }

    [Fact]
    public void LostBridgeChildRetiresTheSessionWithoutAmbiguity()
    {
        using var harness = DebugSessionHarness.Create();
        harness.Open();

        harness.Bridge.Enqueue(FakeBridgeBehavior.ChildDied());

        var failure = Assert.Throws<HostAdapterException>(
            () => harness.Command("halt"));

        // Nothing was submitted, so this is not ambiguous — but the session is
        // still permanently unusable.
        Assert.Equal(
            "debugger_child_unavailable",
            failure.Kind);
        Assert.Equal(
            ExecutionState.NotStarted,
            failure.Execution);
        Assert.True(harness.Bridge.Disposed);

        var later = harness.Command("get-breakpoints");
        AssertAuthorityRefusal(
            later,
            "debug_session_not_open");
    }

    [Fact]
    public void NormalizeCommandIsSideEffectFreeAndCoversEverySupportedCommand()
    {
        var commands = new (string Command, string ExtraJson)[]
        {
            ("eval", "\"source\":\"6*7\""),
            ("set-breakpoints",
                "\"breakpoints\":[{\"file\":\"f\",\"line\":1}]"),
            ("get-breakpoints", ""),
            ("get-break", ""),
            ("get-frame", ""),
            ("set-frame", ""),
            ("get-properties", ""),
            ("continue", ""),
            ("break", ""),
            ("halt", ""),
            ("stepover", ""),
            ("stepinto", ""),
            ("stepout", "")
        };

        foreach (var (command, extraJson) in commands)
        {
            // Built by concatenation rather than an interpolated raw literal so
            // the JSON is unambiguous regardless of quote counting.
            var json =
                "{\"sessionId\":\"dbg-x\",\"command\":\"" +
                command + "\"" +
                (extraJson.Length == 0 ? "" : "," + extraJson) +
                "}";
            using var document = JsonDocument.Parse(json);

            var normalized =
                IllustratorDebugSessionManager.NormalizeCommand(
                    document.RootElement,
                    command,
                    "main",
                    5000);

            Assert.Equal("dbg-x", normalized.SessionId);
            Assert.Equal(command, normalized.Command);
            Assert.Equal("main", normalized.Engine);
            Assert.Equal(5000, normalized.TimeoutMs);
            Assert.Contains(
                "engine=\"main\"",
                normalized.Xml,
                StringComparison.Ordinal);
        }
    }

    private static void AssertRejectedBeforeSubmit(
        OperationResult result,
        string expectedKind)
    {
        Assert.False(result.Ok);
        Assert.Equal(
            OperationStatus.InvalidRequest,
            result.Status);
        Assert.Equal(
            ExecutionState.NotStarted,
            result.Error?.Execution);
        Assert.Equal(expectedKind, result.Error?.Kind);
        Assert.False(result.Error?.Retryable);
        Assert.Equal(TargetState.Known, result.TargetState);
        Assert.Null(result.Result);
    }

    private static void AssertAuthorityRefusal(
        OperationResult result,
        string expectedKind)
    {
        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(
            ExecutionState.NotStarted,
            result.Error?.Execution);
        Assert.Equal(expectedKind, result.Error?.Kind);
        Assert.False(result.Error?.Retryable);
        Assert.Equal(TargetState.Known, result.TargetState);
    }
}
