using System.Diagnostics;
using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;
using ComTool.Runtime;

namespace ComTool.Supervisor;

/// <summary>
/// Bounded watch/condition runtime. A watch is an orchestration record, not a
/// second host dispatcher: it resolves one strong target identity, then issues
/// independent child read requests that re-enter ExecuteAsync so the
/// operation catalog, capability advertisement, leases, and mutation ledger
/// remain the authority.
/// </summary>
public sealed partial class RuntimeSupervisor
{
    /// <summary>
    /// Deterministic seam so the poller can be driven without real time.
    /// </summary>
    internal IWatchClock WatchClock { get; set; } = SystemWatchClock.Instance;

    private async Task<OperationResult> ExecuteWatchConditionAsync(
        OperationRequest request,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        // A watch composes lease-free read-only operations, so it neither takes
        // nor forwards a lease, and it owns its own child deadlines.
        if (request.Policy?.LeaseId is not null)
        {
            return Failure(
                request,
                OperationStatus.InvalidRequest,
                TargetState.Known,
                "watch_lease_not_supported",
                "watch.condition composes lease-free read-only operations and does not accept policy.leaseId.",
                ExecutionState.NotStarted,
                clock.Elapsed.TotalMilliseconds,
                suggestedActions:
                [
                    "release_target_lease",
                    "retry_without_policy_leaseId"
                ]);
        }

        if (request.Preconditions is { Count: > 0 } ||
            request.Postconditions is { Count: > 0 })
        {
            return Failure(
                request,
                OperationStatus.InvalidRequest,
                TargetState.Known,
                "invalid_watch_request",
                "watch.condition evaluates its own condition; attach preconditions or postconditions to the watched source operation instead.",
                ExecutionState.NotStarted,
                clock.Elapsed.TotalMilliseconds,
                suggestedActions: ["inspect_watch_condition"]);
        }

        if (!WatchConditionInput.TryRead(
                request.Input,
                request.Policy?.WorkerWatchdogMs,
                out var plan,
                out var inputError))
        {
            return Failure(
                request,
                OperationStatus.InvalidRequest,
                TargetState.Known,
                "invalid_watch_condition",
                inputError!,
                ExecutionState.NotStarted,
                clock.Elapsed.TotalMilliseconds,
                suggestedActions: ["inspect_watch_condition"]);
        }

        try
        {
            // Recursion guard first, so a self-watch is refused on its own
            // terms rather than through the generic host-scope rejection.
            WatchConditionPoller.EnsureWatchableSource(
                plan!.Condition.Source.Operation);
        }
        catch (OperationConditionPolicyException ex)
        {
            return Failure(
                request,
                OperationStatus.InvalidRequest,
                TargetState.Known,
                ex.Kind,
                ex.Message,
                ExecutionState.NotStarted,
                clock.Elapsed.TotalMilliseconds,
                suggestedActions: ["core.operations.list"]);
        }

        ManagedTarget managed;
        try
        {
            managed = await ResolveLiveTargetAsync(
                    request.Target!,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return Failure(
                request,
                OperationStatus.TargetUnavailable,
                TargetState.Unavailable,
                "watch_target_unavailable",
                ex.Message,
                ExecutionState.NotStarted,
                clock.Elapsed.TotalMilliseconds,
                suggestedActions: ["core.targets.list"]);
        }

        // Pin the strong identity (PID plus process start time plus endpoint)
        // once, then reuse it for every child poll. A watch never re-resolves
        // onto a different generation of the host process.
        var pinned = managed.Descriptor;

        try
        {
            OperationConditionEvaluator.ValidateSources(
                [plan!.Condition],
                pinned,
                "watch");
        }
        catch (OperationConditionPolicyException ex)
        {
            return Failure(
                request,
                OperationStatus.InvalidRequest,
                TargetState.Known,
                ex.Kind,
                ex.Message,
                ExecutionState.NotStarted,
                clock.Elapsed.TotalMilliseconds,
                suggestedActions: ["core.target.capabilities"]);
        }

        var outcome = await WatchConditionPoller.RunAsync(
                plan!,
                WatchClock,
                (pollIndex, budget, ct) => PollWatchSourceAsync(
                    request,
                    plan,
                    pinned,
                    pollIndex,
                    budget,
                    ct),
                () => IsPinnedTargetCurrent(pinned),
                cancellationToken)
            .ConfigureAwait(false);

        return BuildWatchResult(request, clock, plan, pinned, outcome);
    }

    /// <summary>
    /// Issue one independent child read request through the canonical
    /// dispatcher. Each child carries the pinned target ref and a worker
    /// watchdog clamped to the poll's own budget, so a blocked worker cannot
    /// outlive the watch deadline.
    /// </summary>
    private async Task<WatchSourceSample> PollWatchSourceAsync(
        OperationRequest parent,
        WatchPlan plan,
        HostTargetDescriptor pinned,
        int pollIndex,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        var child = new OperationRequest
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = WatchConditionPoller.CreateChildRequestId(
                parent.Id,
                pollIndex),
            Target = pinned.Target,
            Operation = plan.Condition.Source.Operation,
            Input = plan.Condition.Source.Input.Clone(),
            Policy = new OperationPolicy(
                WorkerWatchdogMs: (int)Math.Clamp(
                    Math.Ceiling(budget.TotalMilliseconds),
                    OperationPolicy.MinWorkerWatchdogMs,
                    OperationPolicy.MaxWorkerWatchdogMs),
                RetryBudgetMs: parent.Policy?.RetryBudgetMs)
        };

        ProtocolJson.ValidateRequest(child);

        var result = await ExecuteAsync(child, cancellationToken)
            .ConfigureAwait(false);

        return new WatchSourceSample(
            result.Ok && result.Result is not null,
            result.Result,
            result.Error?.Kind,
            result.Error?.Message,
            result.Status);
    }

    /// <summary>
    /// Generation-safe re-check. Deliberately reads the runtime's cached
    /// descriptor instead of re-resolving: a re-resolve would perform fresh
    /// discovery and could substitute a restarted host process.
    /// </summary>
    private bool IsPinnedTargetCurrent(HostTargetDescriptor pinned)
    {
        if (!string.IsNullOrEmpty(pinned.Identity.TargetId) &&
            _targets.TryGetValue(
                pinned.Identity.TargetId,
                out var current))
        {
            return current.Running &&
                   string.Equals(
                       current.Identity.Host,
                       pinned.Identity.Host,
                       StringComparison.Ordinal) &&
                   current.Identity.ProcessId ==
                       pinned.Identity.ProcessId &&
                   current.Identity.ProcessStartedAt ==
                       pinned.Identity.ProcessStartedAt &&
                   string.Equals(
                       current.Identity.ExecutablePath,
                       pinned.Identity.ExecutablePath,
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       current.Identity.EndpointIdentity,
                       pinned.Identity.EndpointIdentity,
                       StringComparison.Ordinal);
        }

        return false;
    }

    private static OperationResult BuildWatchResult(
        OperationRequest request,
        Stopwatch clock,
        WatchPlan plan,
        HostTargetDescriptor pinned,
        WatchOutcome outcome)
    {
        var state = outcome.Termination switch
        {
            WatchTermination.Satisfied => TargetState.Known,
            WatchTermination.TargetGenerationChanged =>
                TargetState.Unavailable,
            _ => TargetState.Known
        };

        var evidence = new EvidenceItem(
            "watch.summary",
            JsonSerializer.SerializeToElement(
                new
                {
                    source = new
                    {
                        operation = plan.Condition.Source.Operation,
                        kind = outcome.LastValue?.Kind
                    },
                    predicate = new
                    {
                        kind = plan.PredicateKind,
                        expected = plan.Expected?.Kind
                    },
                    termination = outcome.DescribeTermination(),
                    polls = outcome.Polls,
                    waitedMs = Math.Round(
                        outcome.Waited.TotalMilliseconds,
                        3),
                    pollIntervalMs = (int)plan.PollInterval.TotalMilliseconds,
                    timeoutMs = (int)plan.Timeout.TotalMilliseconds,
                    pollTimeoutMs = (int)plan.PollTimeout.TotalMilliseconds,
                    maxPolls = plan.MaxPolls,
                    lastObserved = WatchValuePayload(outcome.LastValue),
                    lastVerified = outcome.LastVerified,
                    lastStatus = outcome.LastStatus.ToString(),
                    lastFailureKind = outcome.LastFailureKind,
                    target = new
                    {
                        id = pinned.Identity.TargetId,
                        host = pinned.Identity.Host,
                        processId = pinned.Identity.ProcessId,
                        processStartedAt =
                            pinned.Identity.ProcessStartedAt
                                .ToUniversalTime(),
                        endpointIdentity =
                            pinned.Identity.EndpointIdentity
                    }
                },
                RuntimePayloadJson));

        if (outcome.Satisfied)
        {
            return Success(
                request,
                ProtocolValue.From(
                    JsonSerializer.SerializeToElement(
                        new
                        {
                            matched = true,
                            predicate = plan.PredicateKind,
                            polls = outcome.Polls,
                            waitedMs = Math.Round(
                                outcome.Waited.TotalMilliseconds,
                                3),
                            value = WatchValuePayload(outcome.LastValue)
                        },
                        RuntimePayloadJson)),
                state,
                clock.Elapsed.TotalMilliseconds,
                [evidence]);
        }

        return Failure(
            request,
            outcome.Termination switch
            {
                WatchTermination.TargetGenerationChanged =>
                    OperationStatus.TargetUnavailable,
                WatchTermination.SourceUnavailable =>
                    OperationStatus.Failed,
                _ => OperationStatus.Failed
            },
            state,
            outcome.Termination switch
            {
                WatchTermination.Timeout => "watch_timeout",
                WatchTermination.SourceUnavailable => "watch_source_failed",
                WatchTermination.PollBudgetExhausted =>
                    "watch_poll_budget_exhausted",
                WatchTermination.TargetGenerationChanged =>
                    "watch_target_generation_changed",
                _ => "watch_unsatisfied"
            },
            DescribeWatchFailure(plan, outcome),
            // A watch only ever dispatches registered read-only operations, so
            // no mutation can have been dispatched and nothing needs replay.
            ExecutionState.NotStarted,
            clock.Elapsed.TotalMilliseconds,
            retryable: true,
            suggestedActions:
                outcome.Termination ==
                WatchTermination.TargetGenerationChanged
                    ?
                    [
                        "list_targets",
                        "resubmit_watch_against_the_new_generation"
                    ]
                    : ["resubmit_watch_with_a_longer_timeout"]);
    }

    private static string DescribeWatchFailure(
        WatchPlan plan,
        WatchOutcome outcome)
    {
        var waited = Math.Round(
            outcome.Waited.TotalMilliseconds,
            3);

        if (outcome.Termination ==
            WatchTermination.TargetGenerationChanged)
        {
            return "The pinned target generation is no longer current; the watch stopped rather than follow a restarted host process.";
        }

        if (outcome.Termination ==
            WatchTermination.PollBudgetExhausted)
        {
            return $"Watch exhausted its poll budget of {plan.MaxPolls} attempts after {waited} ms.";
        }

        if (outcome.Termination ==
            WatchTermination.SourceUnavailable)
        {
            return
                $"Watch could not observe source operation '{plan.Condition.Source.Operation}' in {outcome.Polls} attempt(s) over {waited} ms" +
                (outcome.LastFailureKind is null
                    ? "."
                    : $": {outcome.LastFailureKind} {outcome.LastFailureMessage}".TrimEnd() + ".");
        }

        return
            $"Condition '{plan.PredicateKind}' did not hold within {plan.Timeout.TotalMilliseconds} ms ({outcome.Polls} polls, {waited} ms waited); last value: " +
            WatchValueJson(outcome.LastValue);
    }

    private static object? WatchValuePayload(ProtocolValue? value) =>
        value is null
            ? null
            : new
            {
                kind = value.Kind,
                value = value.Value
            };

    private static string WatchValueJson(ProtocolValue? value)
    {
        if (value is null)
            return "<none>";

        return value.Value is { } element
            ? element.GetRawText()
            : $"<{value.Kind}>";
    }
}
