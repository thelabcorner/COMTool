using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator.Tests;

/// <summary>
/// Focused tests for the <c>illustrator.action.run</c> host runtime.
///
/// Every case runs against <see cref="FakeActionRunHost"/> and virtual time:
/// no live Illustrator instance, no user document, and no wall-clock
/// dependency. The classification assertions (not-started vs ambiguous, and
/// therefore replayable vs not) are the point of the lane.
/// </summary>
public sealed class IllustratorActionRunTests
{
    private const string ExplicitRequest =
        """{"name":"Export for Web","actionSet":"My Actions","dialogs":false}""";

    // -----------------------------------------------------------------
    // Input contract: nothing implicit
    // -----------------------------------------------------------------

    [Fact]
    public void ParseRequestAcceptsFullyExplicitActionIdentityAndDialogPolicy()
    {
        using var input = JsonDocument.Parse(ExplicitRequest);

        var request = IllustratorActionRun.ParseRequest(input.RootElement);

        Assert.Equal("Export for Web", request.Name);
        Assert.Equal("My Actions", request.ActionSet);
        Assert.False(request.Dialogs);
    }

    [Fact]
    public void ParseRequestAcceptsExplicitEmptyActionSet()
    {
        using var input = JsonDocument.Parse(
            """{"name":"Solo","actionSet":"","dialogs":true}""");

        var request = IllustratorActionRun.ParseRequest(input.RootElement);

        Assert.Equal("Solo", request.Name);
        Assert.Equal(string.Empty, request.ActionSet);
        Assert.True(request.Dialogs);
    }

    [Theory]
    // name is mandatory
    [InlineData("""{"actionSet":"My Actions","dialogs":false}""")]
    [InlineData("""{"name":"","actionSet":"My Actions","dialogs":false}""")]
    [InlineData("""{"name":"   ","actionSet":"My Actions","dialogs":false}""")]
    // actionSet must be stated; the legacy "" default is not inherited
    [InlineData("""{"name":"Solo","dialogs":false}""")]
    // dialogs must be stated; there is no implicit suppressed-dialog default
    [InlineData("""{"name":"Solo","actionSet":"My Actions"}""")]
    // dialogs must be a real boolean, never a coerced 1/"true"
    [InlineData("""{"name":"Solo","actionSet":"S","dialogs":1}""")]
    [InlineData("""{"name":"Solo","actionSet":"S","dialogs":"true"}""")]
    [InlineData("""{"name":"Solo","actionSet":"S","dialogs":null}""")]
    // no open-ended or duplicated surface
    [InlineData("""{"name":"Solo","actionSet":"S","dialogs":false,"extra":1}""")]
    [InlineData("""{"name":"A","name":"B","actionSet":"S","dialogs":false}""")]
    [InlineData("""["Solo"]""")]
    public void ParseRequestRejectsImplicitOrOpenEndedActionRequests(string json)
    {
        using var input = JsonDocument.Parse(json);

        Assert.Throws<ArgumentException>(
            () => IllustratorActionRun.ParseRequest(input.RootElement));
    }

    [Fact]
    public void ParseRequestBoundsActionNameAndSetLength()
    {
        using var tooLong = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            name = new string('n', IllustratorActionRun.MaxActionNameChars + 1),
            actionSet = "S",
            dialogs = false
        }));
        using var setTooLong = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            name = "Solo",
            actionSet = new string('s', IllustratorActionRun.MaxActionSetChars + 1),
            dialogs = false
        }));

        Assert.Throws<ArgumentException>(
            () => IllustratorActionRun.ParseRequest(tooLong.RootElement));
        Assert.Throws<ArgumentException>(
            () => IllustratorActionRun.ParseRequest(setTooLong.RootElement));
    }

    // -----------------------------------------------------------------
    // Watchdog budget: derived from the caller, always bounded
    // -----------------------------------------------------------------

    [Fact]
    public void ResolveBudgetStaysInsideTheCallerWatchdogWhenSupplied()
    {
        var budget = IllustratorActionRun.ResolveBudget(30_000);

        Assert.Equal(
            IllustratorActionRun.CallerPolicyBudgetSource,
            budget.BudgetSource);
        Assert.Equal(
            30_000 - IllustratorActionRun.CallerWatchdogHeadroomMs,
            budget.BudgetMs);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(2_100)]
    [InlineData(int.MaxValue)]
    public void ResolveBudgetNeverLeavesItsBoundedWindow(int callerWatchdogMs)
    {
        var budget = IllustratorActionRun.ResolveBudget(callerWatchdogMs);

        Assert.InRange(
            budget.BudgetMs,
            IllustratorActionRun.MinBudgetMs,
            OperationPolicy.MaxWorkerWatchdogMs);
        Assert.True(
            callerWatchdogMs < OperationPolicy.MaxWorkerWatchdogMs
                ? budget.BudgetMs < callerWatchdogMs
                : budget.BudgetMs <= OperationPolicy.MaxWorkerWatchdogMs);
    }

    [Fact]
    public void ResolveBudgetFallsBackToADocumentedCeilingWithNoCallerWatchdog()
    {
        var budget = IllustratorActionRun.ResolveBudget(null);

        Assert.Equal(
            IllustratorActionRun.OperationDefaultBudgetSource,
            budget.BudgetSource);
        Assert.Equal(IllustratorActionRun.DefaultBudgetMs, budget.BudgetMs);
    }

    [Fact]
    public void ResolveBudgetKeepsThePollIntervalAtOrAboveTheFloor()
    {
        var budget = IllustratorActionRun.ResolveBudget(30_000);

        Assert.InRange(
            budget.PollIntervalMs,
            IllustratorActionRun.MinPollIntervalMs,
            IllustratorActionRun.MaxPollIntervalMs);
    }

    // -----------------------------------------------------------------
    // Pre-dispatch refusals: nothing was submitted
    // -----------------------------------------------------------------

    [Fact]
    public void ExecuteRefusesToDispatchWhenActionIsAlreadyRunning()
    {
        var host = new FakeActionRunHost { DefaultActionIsRunning = true };
        var clock = new FakeActionRunClock();

        var result = Run(host, clock, ExplicitRequest);

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.HostBusy, result.Status);
        Assert.Equal(TargetState.Busy, result.TargetState);
        Assert.Equal("action_already_running", result.Error?.Kind);
        Assert.Equal(ExecutionState.NotStarted, result.Error?.Execution);
        Assert.True(result.Error?.Retryable);
        Assert.Empty(host.Dispatches);
    }

    [Fact]
    public void ExecuteRefusesToDispatchWhenTheBaselineCannotBeObserved()
    {
        var host = new FakeActionRunHost
        {
            ReadActionIsRunningFailure =
                FakeActionRunHost.RejectedBeforeExecution()
        };
        var clock = new FakeActionRunClock();

        var result = Run(host, clock, ExplicitRequest);

        Assert.False(result.Ok);
        Assert.Equal(ExecutionState.NotStarted, result.Error?.Execution);
        Assert.True(result.Error?.Retryable);
        Assert.Empty(host.Dispatches);
    }

    [Fact]
    public void ExecuteRefusesBeforeDispatchWhenTheRequestContractIsInvalid()
    {
        var host = new FakeActionRunHost();
        var clock = new FakeActionRunClock();

        var result = Run(host, clock, """{"name":"Solo","dialogs":false}""");

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal("invalid_action_run_request", result.Error?.Kind);
        Assert.Equal(ExecutionState.NotStarted, result.Error?.Execution);
        Assert.False(result.Error?.Retryable);
        Assert.Empty(host.Dispatches);
    }

    // -----------------------------------------------------------------
    // Dispatch success
    // -----------------------------------------------------------------

    [Fact]
    public void ExecuteDispatchesTheExactActionAndWaitsForCompletion()
    {
        var host = new FakeActionRunHost();
        host.Script(false, true, true, false);
        var clock = new FakeActionRunClock();

        var result = Run(host, clock, ExplicitRequest);

        Assert.True(result.Ok);
        Assert.Equal(OperationStatus.Completed, result.Status);
        Assert.Equal(TargetState.KnownChanged, result.TargetState);
        Assert.Null(result.Error);

        var dispatch = Assert.Single(host.Dispatches);
        Assert.Equal("Export for Web", dispatch.Name);
        Assert.Equal("My Actions", dispatch.ActionSet);
        Assert.False(dispatch.Dialogs);

        var diagnostics = Diagnostics(result);
        Assert.Equal("completed", String(diagnostics, "outcome"));
        Assert.Equal("Export for Web", String(diagnostics, "action"));
        Assert.Equal("My Actions", String(diagnostics, "actionSet"));
        Assert.Equal("suppress_action_dialogs", String(diagnostics, "dialogPolicy"));
        Assert.Equal(3, Int(diagnostics, "pollSamples"));
        Assert.True(Bool(diagnostics, "completionObserved"));
        Assert.True(Bool(diagnostics, "dispatched"));
        Assert.False(Bool(diagnostics, "preDispatchActionIsRunning"));
    }

    [Fact]
    public void ExecuteForwardsAnExplicitDialogPolicyWithoutHostDefaults()
    {
        var host = new FakeActionRunHost { DefaultActionIsRunning = false };
        var clock = new FakeActionRunClock();

        var result = Run(
            host,
            clock,
            """{"name":"Solo","actionSet":"","dialogs":true}""");

        Assert.True(result.Ok);

        var dispatch = Assert.Single(host.Dispatches);
        Assert.Equal(string.Empty, dispatch.ActionSet);
        Assert.True(dispatch.Dialogs);
        Assert.Equal(
            "allow_action_dialogs",
            String(Diagnostics(result), "dialogPolicy"));
    }

    [Fact]
    public void ExecuteNeverMutatesGlobalUserInteractionLevel()
    {
        var host = new FakeActionRunHost
        {
            UserInteractionLevel = 2,
            DefaultActionIsRunning = false
        };
        var clock = new FakeActionRunClock();

        Run(host, clock, ExplicitRequest);

        // The interaction level is read once as diagnostic evidence and is
        // never written. The legacy runner's capture/change/restore of this
        // process-global host flag is intentionally not reproduced.
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("set:", StringComparison.Ordinal));
        Assert.Equal(1, host.UserInteractionLevelReads);

        var diagnostics = Diagnostics(Run(
            new FakeActionRunHost { DefaultActionIsRunning = false },
            new FakeActionRunClock(),
            ExplicitRequest));
        Assert.Equal(2, Int(diagnostics, "userInteractionLevelObserved"));
        Assert.False(Bool(diagnostics, "userInteractionLevelMutated"));
    }

    [Fact]
    public void ExecuteSurvivesAnUnreadableInteractionLevelWithoutBlockingDispatch()
    {
        var host = new FakeActionRunHost
        {
            ReadUserInteractionLevelFailure = new HostAdapterException(
                "host_busy",
                "interaction level busy",
                retryable: true,
                ExecutionState.NotStarted),
            DefaultActionIsRunning = false
        };
        var clock = new FakeActionRunClock();

        var result = Run(host, clock, ExplicitRequest);

        // A diagnostic read that changes nothing must never fabricate a
        // not-started outcome.
        Assert.True(result.Ok);
        Assert.Single(host.Dispatches);
    }

    [Fact]
    public void ExecuteNeverCertifiesThatReplayingTheActionIsSafe()
    {
        var host = new FakeActionRunHost { DefaultActionIsRunning = false };

        var result = Run(host, new FakeActionRunClock(), ExplicitRequest);

        Assert.False(Bool(Diagnostics(result), "replayPermitted"));
    }

    // -----------------------------------------------------------------
    // No-replay classification after a possible dispatch
    // -----------------------------------------------------------------

    [Fact]
    public void ExecuteNeverReplaysWhenTheDispatchOutcomeIsAmbiguous()
    {
        var host = new FakeActionRunHost
        {
            DispatchFailure = FakeActionRunHost.AmbiguousAfterDispatch()
        };
        var clock = new FakeActionRunClock();

        var result = Run(host, clock, ExplicitRequest);

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.ReconciliationRequired, result.Status);
        Assert.Equal(TargetState.ReconciliationRequired, result.TargetState);
        Assert.Equal("action_dispatch_outcome_ambiguous", result.Error?.Kind);
        Assert.Equal(ExecutionState.Ambiguous, result.Error?.Execution);
        Assert.False(result.Error?.Retryable);
        Assert.DoesNotContain(
            "retry_within_budget",
            result.Error?.SuggestedActions ?? []);

        // Exactly one dispatch attempt: the action is never re-sent.
        Assert.Single(host.Dispatches);
    }

    [Fact]
    public void ExecuteReportsProvenPreDispatchRejectionAsRetryableHostBusy()
    {
        var host = new FakeActionRunHost
        {
            DispatchFailure = FakeActionRunHost.RejectedBeforeExecution()
        };
        var clock = new FakeActionRunClock();

        var result = Run(host, clock, ExplicitRequest);

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.HostBusy, result.Status);
        Assert.Equal(TargetState.Busy, result.TargetState);
        Assert.Equal("host_busy", result.Error?.Kind);
        Assert.Equal(ExecutionState.NotStarted, result.Error?.Execution);
        Assert.True(result.Error?.Retryable);
        Assert.Contains(
            "retry_within_budget",
            result.Error?.SuggestedActions ?? []);
    }

    [Fact]
    public void ExecuteNeverReplaysWhenCompletionBecomesUnobservable()
    {
        var host = new FakeActionRunHost();
        host.Script(false, true);
        var clock = new FakeActionRunClock();

        // The first post-dispatch poll succeeds, then the flag goes unreadable.
        var flaky = new FlakyActionRunHost(host, failAfterReads: 2);
        var result = IllustratorActionRun.Execute(
            ActionRunRequests.Create(ExplicitRequest, 30_000),
            flaky,
            IllustratorActionRun.ResolveBudget(30_000),
            clock);

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.ReconciliationRequired, result.Status);
        Assert.Equal("action_completion_unobservable", result.Error?.Kind);
        Assert.Equal(ExecutionState.Ambiguous, result.Error?.Execution);
        Assert.False(result.Error?.Retryable);
        Assert.Single(host.Dispatches);
    }

    // -----------------------------------------------------------------
    // Bounded watchdog
    // -----------------------------------------------------------------

    [Fact]
    public void ExecuteReportsAmbiguityWhenTheBoundedWaitExpires()
    {
        var host = new FakeActionRunHost { DefaultActionIsRunning = true };
        host.Script(false);
        var clock = new FakeActionRunClock();
        var budget = new ActionRunBudget(
            1_000,
            IllustratorActionRun.CallerPolicyBudgetSource,
            IllustratorActionRun.MinPollIntervalMs);

        var result = IllustratorActionRun.Execute(
            ActionRunRequests.Create(ExplicitRequest, 3_000),
            host,
            budget,
            clock);

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.ReconciliationRequired, result.Status);
        Assert.Equal(TargetState.ReconciliationRequired, result.TargetState);
        Assert.Equal("action_watchdog_expired", result.Error?.Kind);
        Assert.Equal(ExecutionState.Ambiguous, result.Error?.Execution);
        Assert.False(result.Error?.Retryable);
        Assert.DoesNotContain(
            "retry_within_budget",
            result.Error?.SuggestedActions ?? []);
        Assert.Single(host.Dispatches);

        // The wait is bounded and never busy-loops the COM apartment.
        Assert.NotEmpty(clock.Waits);
        Assert.All(
            clock.Waits,
            wait => Assert.True(
                wait >= TimeSpan.FromMilliseconds(
                    IllustratorActionRun.MinPollIntervalMs)));
        Assert.True(clock.Elapsed <= TimeSpan.FromMilliseconds(1_000));

        var diagnostics = Diagnostics(result);
        Assert.False(Bool(diagnostics, "completionObserved"));
        Assert.Equal(
            1_000,
            Int(diagnostics, "budgetMs"));
        Assert.Equal(
            IllustratorActionRun.CallerPolicyBudgetSource,
            String(diagnostics, "budgetSource"));
    }

    // -----------------------------------------------------------------
    // Cancellation
    // -----------------------------------------------------------------

    [Fact]
    public void ExecuteReportsAmbiguityWhenCancelledAfterDispatch()
    {
        var host = new FakeActionRunHost { DefaultActionIsRunning = true };
        host.Script(false);
        var clock = new FakeActionRunClock();
        using var cancellation = new CancellationTokenSource();

        var request = ActionRunRequests.Create(ExplicitRequest, 30_000);
        var budget = IllustratorActionRun.ResolveBudget(30_000);

        // Cancel as soon as the first post-dispatch poll observes the action
        // still running.
        var result = IllustratorActionRun.Execute(
            request,
            new CancellingActionRunHost(host, cancellation),
            budget,
            clock,
            cancellation.Token);

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.ReconciliationRequired, result.Status);
        Assert.Equal("action_wait_cancelled", result.Error?.Kind);
        Assert.Equal(ExecutionState.Ambiguous, result.Error?.Execution);
        Assert.False(result.Error?.Retryable);
        Assert.Single(host.Dispatches);
    }

    // -----------------------------------------------------------------
    // Capability descriptor (lane-local registration surface)
    // -----------------------------------------------------------------

    [Fact]
    public void CapabilityDeclaresAnExternalSideEffectIllustratorOperation()
    {
        var capability = Assert.Single(
            IllustratorOperations.Capabilities,
            c => c.Name == "illustrator.action.run");

        Assert.Equal("illustrator", capability.Host);
        Assert.True(capability.Supported);
        Assert.Equal(MutationClass.ExternalSideEffect, capability.MutationClass);
        Assert.Contains("DoScript", capability.Description, StringComparison.Ordinal);
        Assert.Contains(
            "lease",
            capability.Description,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CapabilityNamesAreUnique()
    {
        var names = IllustratorOperations.Capabilities
            .Select(c => c.Name)
            .ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static OperationResult Run(
        IActionRunHost host,
        IActionRunClock clock,
        string inputJson,
        int? workerWatchdogMs = 30_000) =>
        IllustratorActionRun.Execute(
            ActionRunRequests.Create(inputJson, workerWatchdogMs),
            host,
            IllustratorActionRun.ResolveBudget(workerWatchdogMs),
            clock);

    private static JsonElement Diagnostics(OperationResult result)
    {
        var evidence = Assert.Single(result.Evidence ?? []);
        Assert.Equal(IllustratorActionRun.DiagnosticsEvidenceKind, evidence.Kind);
        return evidence.Value;
    }

    private static string String(JsonElement element, string property) =>
        element.GetProperty(property).GetString() ?? string.Empty;

    private static int Int(JsonElement element, string property) =>
        element.GetProperty(property).GetInt32();

    private static bool Bool(JsonElement element, string property) =>
        element.GetProperty(property).GetBoolean();

    /// <summary>
    /// Fails the ActionIsRunning read only after the scripted pre-dispatch
    /// read has already been consumed, so the dispatch is genuinely reached
    /// before observability is lost.
    /// </summary>
    private sealed class FlakyActionRunHost : IActionRunHost
    {
        private readonly FakeActionRunHost _inner;
        private readonly int _failAfterReads;
        private int _reads;

        public FlakyActionRunHost(FakeActionRunHost inner, int failAfterReads)
        {
            _inner = inner;
            _failAfterReads = failAfterReads;
        }

        public bool ReadActionIsRunning()
        {
            _reads++;
            if (_reads > _failAfterReads)
            {
                throw FakeActionRunHost.AmbiguousAfterDispatch();
            }

            return _inner.ReadActionIsRunning();
        }

        public int ReadUserInteractionLevel() =>
            _inner.ReadUserInteractionLevel();

        public void DoScript(string name, string actionSet, bool dialogs) =>
            _inner.DoScript(name, actionSet, dialogs);
    }

    /// <summary>
    /// Cancels the wait the first time the dispatched action is observed
    /// still running, modelling a caller-side cancellation arriving after
    /// dispatch.
    /// </summary>
    private sealed class CancellingActionRunHost : IActionRunHost
    {
        private readonly FakeActionRunHost _inner;
        private readonly CancellationTokenSource _cancellation;
        private bool _dispatched;

        public CancellingActionRunHost(
            FakeActionRunHost inner,
            CancellationTokenSource cancellation)
        {
            _inner = inner;
            _cancellation = cancellation;
        }

        public bool ReadActionIsRunning()
        {
            var running = _inner.ReadActionIsRunning();
            if (_dispatched && running)
                _cancellation.Cancel();

            return running;
        }

        public int ReadUserInteractionLevel() =>
            _inner.ReadUserInteractionLevel();

        public void DoScript(string name, string actionSet, bool dialogs)
        {
            _inner.DoScript(name, actionSet, dialogs);
            _dispatched = true;
        }
    }
}
