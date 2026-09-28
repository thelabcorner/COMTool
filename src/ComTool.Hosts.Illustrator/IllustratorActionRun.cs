using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator;

/// <summary>
/// V2 Action-palette runtime: run one named action from one named action set
/// under an explicit dialog policy, and report truthfully whether it finished.
///
/// This is the single host execution path for <c>illustrator.action.run</c>.
/// The legacy tool is used here only as a behavioral oracle, not as an
/// architecture template: the legacy runner executed directly against a
/// process-wide COM session with a bare polling loop and no dispatch
/// identity, no budget derivation, and no ambiguity contract.
///
/// V2 semantics that are strictly stronger than the legacy <c>action
/// &lt;name&gt;</c> runner:
///
/// <list type="bullet">
/// <item><description>Explicit input contract. <c>name</c>, <c>actionSet</c>
/// and <c>dialogs</c> are all mandatory and bounded. There is no default
/// action set (the legacy CLI defaulted to <c>""</c>, which silently lets the
/// host search every set) and no implicit <c>--dialogs</c> default (the legacy
/// CLI defaulted to suppressed, so a caller could not tell which UI behavior
/// it had asked for). An empty <c>actionSet</c> is still expressible, but
/// only when the caller states it.</description></item>
/// <item><description>No hidden UI default. The caller's <c>dialogs</c>
/// boolean is forwarded verbatim as <c>DoScript</c>'s third parameter and is
/// never omitted, so the host's own optional-argument default can never be
/// inherited implicitly.</description></item>
/// <item><description>Truthful pre-dispatch baseline. <c>ActionIsRunning</c>
/// is read before dispatch. It is a process-global flag, so dispatching while
/// it is already true would make a later <c>false</c> observation
/// unattributable to this run; the operation refuses instead of guessing.</description></item>
/// <item><description>Conservative ambiguity. Everything from the first
/// possible <c>DoScript</c> submission onward is ambiguous and never
/// retried, replayed, or downgraded to a completed mutation. Only a proven
/// reject-before-execution HRESULT is reported as retryable, and that verdict
/// is inherited from the shared COM interop classifier rather than re-decided
/// here.</description></item>
/// <item><description>Bounded wait on the caller's clock. The post-dispatch
/// poll budget is derived from <c>policy.workerWatchdogMs</c> when the caller
/// supplies one and otherwise falls back to a documented default ceiling. It
/// is never unbounded, and this class adds no retry or watchdog system of its
/// own: DoScript's own blocking is bounded by the runtime worker watchdog,
/// and this budget bounds only the post-dispatch observability window.</description></item>
/// <item><description>Bounded diagnostics. A fixed field set of scalars:
/// no unbounded sample log, and every error string is length-capped before
/// it reaches the protocol frame.</description></item>
/// </list>
///
/// <para><b>Intentionally dropped from the legacy runner.</b> Legacy set
/// <c>Application.UserInteractionLevel = -1</c> (COM enum
/// <c>aiDontDisplayAlerts</c>) around every suppressed-dialog run and
/// restored it afterwards. That is deliberately not reproduced. The two flags
/// are different surfaces — <c>DoScript(..., Dialogs:=False)</c> suppresses
/// the action's own modal dialogs, while <c>aiDontDisplayAlerts</c> suppresses
/// host alerts raised by the commands inside the action — but the legacy
/// behavior mutates process-global host state that outlives the call, needs
/// capture/restore to stay truthful, and turns a single action dispatch into
/// two mutations with two ambiguity states. No V2 spec requires it and no
/// controlled test in this tree demonstrates that <c>DoScript</c>'s dialog flag
/// is insufficient, so it is recorded here as dropped legacy safety baggage.
/// The residual exposure is explicit and bounded: a host alert raised inside a
/// dialog-suppressed action is not suppressed, so the action stalls, the
/// bounded wait expires, and the result is reported as ambiguous
/// (<c>action_watchdog_expired</c>) instead of silently succeeding.</para>
///
/// <para>The current interaction level is still read once, non-mutating, as
/// best-effort diagnostic evidence of the UI state the action ran under. A
/// failure to read it is never allowed to fabricate a not-started outcome.</para>
/// </summary>
internal static class IllustratorActionRun
{
    public const string Operation = "illustrator.action.run";

    /// <summary>Diagnostics evidence kind for every terminal path.</summary>
    internal const string DiagnosticsEvidenceKind =
        "illustrator.action.run.diagnostics";

    internal const int MaxActionNameChars = 1_024;
    internal const int MaxActionSetChars = 1_024;
    internal const int MaxDiagnosticTextChars = 512;

    /// <summary>
    /// Floor for the ActionIsRunning poll interval. The interval is never
    /// caller-tunable: a zero or sub-floor interval would turn the
    /// observability wait into a COM busy-loop.
    /// </summary>
    internal const int MinPollIntervalMs = 100;

    internal const int DefaultPollIntervalMs = 150;
    internal const int MaxPollIntervalMs = 1_000;

    /// <summary>
    /// Ceiling applied when the caller supplies no worker watchdog. Matches
    /// the legacy runner's default operation timeout.
    /// </summary>
    internal const int DefaultBudgetMs = 60_000;

    /// <summary>
    /// Floor for the derived post-dispatch budget, so a caller-supplied
    /// watchdog below the reserve headroom still yields a usable (and still
    /// bounded) window.
    /// </summary>
    internal const int MinBudgetMs = 1;

    /// <summary>
    /// Reserve kept out of the in-operation budget so a truthful ambiguous
    /// result can be returned before the runtime worker watchdog fires.
    /// </summary>
    internal const int CallerWatchdogHeadroomMs = 2_000;

    internal const string CallerPolicyBudgetSource = "caller_policy";
    internal const string OperationDefaultBudgetSource = "operation_default";

    private static readonly string[] ReconcileActions =
    [
        "inspect_mutation_ledger",
        "apply_operation_specific_postconditions",
        "resolve_incident_explicitly"
    ];

    // ---------------------------------------------------------------------
    // Input contract
    // ---------------------------------------------------------------------

    /// <summary>
    /// Parses the strict, fully explicit <c>illustrator.action.run</c>
    /// request. Throws <see cref="ArgumentException"/> for every contract
    /// violation; nothing here reads or mutates the host.
    /// </summary>
    public static ActionRunRequest ParseRequest(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
            throw new ArgumentException(
                $"{Operation} input must be a JSON object.");

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "name",
            "actionSet",
            "dialogs"
        };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in input.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw new ArgumentException(
                    $"Duplicate {Operation} field '{property.Name}'.");

            if (!allowed.Contains(property.Name))
                throw new ArgumentException(
                    $"Unknown {Operation} field '{property.Name}'.");
        }

        var name = ReadBoundedString(
            input,
            "name",
            MaxActionNameChars,
            allowEmpty: false);
        var actionSet = ReadBoundedString(
            input,
            "actionSet",
            MaxActionSetChars,
            allowEmpty: true);
        var dialogs = ReadRequiredBoolean(input, "dialogs");

        return new ActionRunRequest(name, actionSet, dialogs);
    }

    private static string ReadBoundedString(
        JsonElement input,
        string field,
        int max,
        bool allowEmpty)
    {
        if (!input.TryGetProperty(field, out var element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            throw new ArgumentException(
                $"'{field}' is required" +
                (allowEmpty
                    ? " (an empty string is allowed and is sent verbatim)."
                    : "."));
        }

        if (element.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"'{field}' must be a string.");

        var text = element.GetString() ?? string.Empty;
        if (!allowEmpty && string.IsNullOrWhiteSpace(text))
            throw new ArgumentException($"'{field}' must be non-empty.");

        if (text.Length > max)
        {
            throw new ArgumentException(
                $"'{field}' exceeds the {max} character limit.");
        }

        return text;
    }

    private static bool ReadRequiredBoolean(JsonElement input, string field)
    {
        if (!input.TryGetProperty(field, out var element) ||
            element.ValueKind == JsonValueKind.Null)
            throw new ArgumentException($"'{field}' is required.");

        // Deliberately strict: no 0/1/"true" coercion, so a caller's dialog
        // policy is always an unambiguous declared intent.
        if (element.ValueKind is not (JsonValueKind.True or
            JsonValueKind.False))
        {
            throw new ArgumentException(
                $"'{field}' must be a JSON boolean.");
        }

        return element.GetBoolean();
    }

    // ---------------------------------------------------------------------
    // Watchdog budget
    // ---------------------------------------------------------------------

    /// <summary>
    /// Derives the post-dispatch wait budget from the caller's own runtime
    /// watchdog. This introduces no independent timeout universe: when the
    /// caller sets a watchdog the in-operation budget is strictly smaller
    /// than it (minus a fixed reserve) so the truthful ambiguous result
    /// returns before the runtime watchdog kills the worker; when the caller
    /// sets none, a fixed documented ceiling applies. The result is always
    /// inside [<see cref="MinBudgetMs"/>,
    /// <see cref="OperationPolicy.MaxWorkerWatchdogMs"/>].
    /// </summary>
    public static ActionRunBudget ResolveBudget(int? callerWatchdogMs)
    {
        var pollIntervalMs = Math.Clamp(
            DefaultPollIntervalMs,
            MinPollIntervalMs,
            MaxPollIntervalMs);

        if (callerWatchdogMs is not { } watchdogMs)
        {
            return new ActionRunBudget(
                Math.Min(DefaultBudgetMs, OperationPolicy.MaxWorkerWatchdogMs),
                OperationDefaultBudgetSource,
                pollIntervalMs);
        }

        var derived = Math.Max(
            MinBudgetMs,
            watchdogMs - CallerWatchdogHeadroomMs);

        return new ActionRunBudget(
            Math.Min(derived, OperationPolicy.MaxWorkerWatchdogMs),
            CallerPolicyBudgetSource,
            pollIntervalMs);
    }

    // ---------------------------------------------------------------------
    // Execution
    // ---------------------------------------------------------------------

    /// <summary>
    /// Runs the action through the real COM host surface.
    /// </summary>
    public static OperationResult Execute(
        object appObject,
        OperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(appObject);
        ArgumentNullException.ThrowIfNull(request);

        return Execute(
            request,
            new ComActionRunHost(appObject),
            ResolveBudget(request.Policy?.WorkerWatchdogMs),
            SystemActionRunClock.Instance,
            cancellationToken);
    }

    /// <summary>
    /// Runs the action against an injected host surface and clock. The
    /// injected pair is the only reason the state machine below is testable
    /// without a live Illustrator document.
    /// </summary>
    public static OperationResult Execute(
        OperationRequest request,
        IActionRunHost host,
        ActionRunBudget budget,
        IActionRunClock clock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(clock);

        ActionRunRequest parsed;
        try
        {
            parsed = ParseRequest(request.Input);
        }
        catch (ArgumentException ex)
        {
            return Build(request, budget, new ActionRunCore
            {
                Outcome = ActionRunOutcome.Refused,
                Execution = ExecutionState.NotStarted,
                ErrorKind = "invalid_action_run_request",
                ErrorMessage = ex.Message,
                Retryable = false,
                BusyStatus = false,
                Dispatched = false,
                PreDispatchActionIsRunning = null,
                PollSamples = 0,
                CompletionObserved = null,
                UserInteractionLevel = null
            });
        }

        return Build(
            request,
            budget,
            RunCore(parsed, host, budget, clock, cancellationToken) with
            {
                Name = parsed.Name,
                ActionSet = parsed.ActionSet,
                Dialogs = parsed.Dialogs
            });
    }

    private static ActionRunCore RunCore(
        ActionRunRequest request,
        IActionRunHost host,
        ActionRunBudget budget,
        IActionRunClock clock,
        CancellationToken cancellationToken)
    {
        // ---- Phase 1: non-mutating pre-dispatch observation. ------------
        // Nothing has been dispatched yet, so every failure in this phase is
        // an honest NotStarted.
        bool preRunning;
        try
        {
            preRunning = host.ReadActionIsRunning();
        }
        catch (HostAdapterException ex)
        {
            return Refused(
                ex.Kind,
                $"ActionIsRunning could not be read before dispatch: {ex.Message}",
                retryable: ex.Retryable,
                busyStatus: ex.Retryable,
                dispatched: false,
                preRunning: null,
                userInteractionLevel: ReadInteractionLevelQuietly(host));
        }

        if (preRunning)
        {
            return Refused(
                "action_already_running",
                "ActionIsRunning was already true before dispatch. The flag is " +
                "process-global, so a later false observation could not be " +
                "attributed to this run; dispatch was refused rather than " +
                "concurrently running an action whose completion could not be " +
                "proven.",
                retryable: true,
                busyStatus: true,
                dispatched: false,
                preRunning: true,
                userInteractionLevel: ReadInteractionLevelQuietly(host));
        }

        var interactionLevel = ReadInteractionLevelQuietly(host);

        // ---- Phase 2: the commit point. ---------------------------------
        // From the first possible submission onward nothing is ever retried
        // or replayed inside this method.
        var dispatchStarted = clock.Elapsed;
        try
        {
            host.DoScript(request.Name, request.ActionSet, request.Dialogs);
        }
        catch (HostAdapterException ex)
        {
            // ExecutionState.NotStarted here is the shared interop's proven
            // reject-before-execution verdict (the call never entered the
            // host), which is the only replayable failure this operation can
            // ever report.
            return new ActionRunCore
            {
                Outcome = ex.Execution == ExecutionState.Ambiguous
                    ? ActionRunOutcome.Ambiguous
                    : ActionRunOutcome.Refused,
                Execution = ex.Execution,
                ErrorKind = ex.Execution == ExecutionState.Ambiguous
                    ? "action_dispatch_outcome_ambiguous"
                    : ex.Retryable
                        ? "host_busy"
                        : ex.Kind,
                ErrorMessage = ex.Message,
                Retryable =
                    ex.Execution != ExecutionState.Ambiguous && ex.Retryable,
                BusyStatus =
                    ex.Execution != ExecutionState.Ambiguous && ex.Retryable,
                Dispatched = ex.Execution == ExecutionState.Ambiguous,
                PreDispatchActionIsRunning = preRunning,
                PollSamples = 0,
                CompletionObserved = null,
                UserInteractionLevel = interactionLevel,
                DispatchMs = (clock.Elapsed - dispatchStarted).TotalMilliseconds
            };
        }

        var dispatchMs = (clock.Elapsed - dispatchStarted).TotalMilliseconds;

        // ---- Phase 3: bounded, attributable completion wait. ------------
        // The first observation is immediate so an action that finished
        // synchronously costs no poll interval.
        var pollInterval = TimeSpan.FromMilliseconds(
            budget.PollIntervalMs);
        var samples = 0;

        while (true)
        {
            bool running;
            try
            {
                running = host.ReadActionIsRunning();
            }
            catch (HostAdapterException ex)
            {
                return new ActionRunCore
                {
                    Outcome = ActionRunOutcome.Ambiguous,
                    Execution = ExecutionState.Ambiguous,
                    ErrorKind = "action_completion_unobservable",
                    ErrorMessage =
                        "The action was dispatched, but ActionIsRunning became " +
                        $"unreadable while waiting for completion: {ex.Message}",
                    Retryable = false,
                    BusyStatus = false,
                    Dispatched = true,
                    PreDispatchActionIsRunning = preRunning,
                    PollSamples = samples,
                    CompletionObserved = null,
                    UserInteractionLevel = interactionLevel,
                    DispatchMs = dispatchMs
                };
            }

            samples++;

            if (!running)
            {
                return new ActionRunCore
                {
                    Outcome = ActionRunOutcome.Completed,
                    Execution = ExecutionState.Completed,
                    ErrorKind = null,
                    ErrorMessage = null,
                    Retryable = false,
                    BusyStatus = false,
                    Dispatched = true,
                    PreDispatchActionIsRunning = preRunning,
                    PollSamples = samples,
                    CompletionObserved = true,
                    UserInteractionLevel = interactionLevel,
                    DispatchMs = dispatchMs
                };
            }

            if (clock.Elapsed - dispatchStarted >=
                TimeSpan.FromMilliseconds(budget.BudgetMs))
            {
                return new ActionRunCore
                {
                    Outcome = ActionRunOutcome.Ambiguous,
                    Execution = ExecutionState.Ambiguous,
                    ErrorKind = "action_watchdog_expired",
                    ErrorMessage =
                        $"ActionIsRunning was still true after the bounded " +
                        $"{budget.BudgetMs} ms wait ({budget.BudgetSource}). The " +
                        "action may still be running, may be blocked on a dialog " +
                        "this operation does not suppress, or may have failed. " +
                        "Completion is not proven and the action is not replayed.",
                    Retryable = false,
                    BusyStatus = false,
                    Dispatched = true,
                    PreDispatchActionIsRunning = preRunning,
                    PollSamples = samples,
                    CompletionObserved = false,
                    UserInteractionLevel = interactionLevel,
                    DispatchMs = dispatchMs
                };
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new ActionRunCore
                {
                    Outcome = ActionRunOutcome.Ambiguous,
                    Execution = ExecutionState.Ambiguous,
                    ErrorKind = "action_wait_cancelled",
                    ErrorMessage =
                        "The wait for action completion was cancelled after " +
                        "dispatch. The action was submitted and its outcome is " +
                        "not proven.",
                    Retryable = false,
                    BusyStatus = false,
                    Dispatched = true,
                    PreDispatchActionIsRunning = preRunning,
                    PollSamples = samples,
                    CompletionObserved = false,
                    UserInteractionLevel = interactionLevel,
                    DispatchMs = dispatchMs
                };
            }

            var elapsed = clock.Elapsed - dispatchStarted;
            var remaining =
                TimeSpan.FromMilliseconds(budget.BudgetMs) - elapsed;
            clock.Wait(remaining < pollInterval ? remaining : pollInterval);
        }
    }

    /// <summary>
    /// Best-effort, non-mutating diagnostic read. A failure here is recorded
    /// as "no observation" and never turns a dispatchable request into a
    /// fabricated not-started outcome: this read changes nothing, so it
    /// cannot prove anything about readiness.
    /// </summary>
    private static int? ReadInteractionLevelQuietly(IActionRunHost host)
    {
        try
        {
            return host.ReadUserInteractionLevel();
        }
        catch (HostAdapterException)
        {
            return null;
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
    }

    private static ActionRunCore Refused(
        string kind,
        string message,
        bool retryable,
        bool busyStatus,
        bool dispatched,
        bool? preRunning,
        int? userInteractionLevel) =>
        new()
        {
            Outcome = ActionRunOutcome.Refused,
            Execution = ExecutionState.NotStarted,
            ErrorKind = kind,
            ErrorMessage = message,
            Retryable = retryable,
            BusyStatus = busyStatus,
            Dispatched = dispatched,
            PreDispatchActionIsRunning = preRunning,
            PollSamples = 0,
            CompletionObserved = null,
            UserInteractionLevel = userInteractionLevel
        };

    // ---------------------------------------------------------------------
    // Result projection
    // ---------------------------------------------------------------------

    private static OperationResult Build(
        OperationRequest request,
        ActionRunBudget budget,
        ActionRunCore core)
    {
        var diagnostics = JsonSerializer.SerializeToElement(new
        {
            outcome = OutcomeToken(core.Outcome),
            errorKind = core.ErrorKind,
            errorDetail = Truncate(core.ErrorMessage),
            action = core.Name,
            actionSet = core.ActionSet,
            dialogs = core.Dialogs,
            dialogPolicy = core.Dialogs
                ? "allow_action_dialogs"
                : "suppress_action_dialogs",
            userInteractionLevelObserved = core.UserInteractionLevel,
            userInteractionLevelMutated = false,
            preDispatchActionIsRunning = core.PreDispatchActionIsRunning,
            dispatched = core.Dispatched,
            completionObserved = core.CompletionObserved,
            budgetMs = budget.BudgetMs,
            budgetSource = budget.BudgetSource,
            pollIntervalMs = budget.PollIntervalMs,
            pollSamples = core.PollSamples,
            dispatchMs = core.DispatchMs,
            // An action is a non-idempotent external side effect: this
            // operation never certifies that running it again is safe.
            replayPermitted = false
        });

        var evidence = new List<EvidenceItem>
        {
            new(DiagnosticsEvidenceKind, diagnostics)
        };

        if (core.Outcome == ActionRunOutcome.Completed)
        {
            return new OperationResult
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = request.Id,
                Operation = request.Operation,
                Ok = true,
                Status = OperationStatus.Completed,
                TargetState = TargetState.KnownChanged,
                Result = ProtocolValue.From(diagnostics),
                Evidence = evidence
            };
        }

        var ambiguous = core.Outcome == ActionRunOutcome.Ambiguous;

        return new OperationResult
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = false,
            Status = ambiguous
                ? OperationStatus.ReconciliationRequired
                : core.BusyStatus
                    ? OperationStatus.HostBusy
                    : OperationStatus.Failed,
            TargetState = ambiguous
                ? TargetState.ReconciliationRequired
                : core.BusyStatus
                    ? TargetState.Busy
                    : TargetState.Known,
            Error = new ProtocolError
            {
                Kind = core.ErrorKind ?? "action_run_failed",
                Message = Truncate(core.ErrorMessage),
                Retryable = core.Retryable,
                Execution = core.Execution,
                SuggestedActions = SuggestedActions(core)
            },
            Evidence = evidence
        };
    }

    private static IReadOnlyList<string> SuggestedActions(ActionRunCore core)
    {
        if (core.Outcome == ActionRunOutcome.Ambiguous)
        {
            // Never "retry_within_budget" here: the action may already have
            // run, and replaying it is not a safe recovery.
            return ReconcileActions;
        }

        if (core.BusyStatus)
            return ["retry_within_budget", "inspect_action_is_running"];

        return core.Dispatched
            ? ["inspect_host_state", "apply_operation_specific_postconditions"]
            : ["inspect_request_contract", "inspect_host_state"];
    }

    private static string OutcomeToken(ActionRunOutcome outcome) =>
        outcome switch
        {
            ActionRunOutcome.Completed => "completed",
            ActionRunOutcome.Ambiguous => "ambiguous",
            _ => "refused"
        };

    internal static string Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.Length <= MaxDiagnosticTextChars
            ? value
            : value[..MaxDiagnosticTextChars] + " [truncated]";
    }
}

/// <summary>
/// The validated, fully explicit action-run request.
/// </summary>
internal sealed record ActionRunRequest(
    string Name,
    string ActionSet,
    bool Dialogs);

/// <summary>
/// The post-dispatch wait budget and the poll interval, both already bounded.
/// </summary>
internal sealed record ActionRunBudget(
    int BudgetMs,
    string BudgetSource,
    int PollIntervalMs);

internal enum ActionRunOutcome
{
    Completed,
    Refused,
    Ambiguous
}

/// <summary>
/// The internal state-machine result. Kept distinct from the protocol result
/// so the projection stays in one place and every terminal path carries the
/// same bounded evidence.
/// </summary>
internal sealed record ActionRunCore
{
    public required ActionRunOutcome Outcome { get; init; }

    public required ExecutionState Execution { get; init; }

    public string? ErrorKind { get; init; }

    public string? ErrorMessage { get; init; }

    public required bool Retryable { get; init; }

    public required bool BusyStatus { get; init; }

    public required bool Dispatched { get; init; }

    public required bool? PreDispatchActionIsRunning { get; init; }

    public required int PollSamples { get; init; }

    public required bool? CompletionObserved { get; init; }

    public required int? UserInteractionLevel { get; init; }

    public double? DispatchMs { get; init; }

    public string Name { get; init; } = string.Empty;

    public string ActionSet { get; init; } = string.Empty;

    public bool Dialogs { get; init; }
}

/// <summary>
/// The narrow host surface this operation needs. Every member is either a
/// read or the single action dispatch; there is deliberately no host-state
/// write (see the class remarks on dropped legacy
/// <c>UserInteractionLevel</c> manipulation).
/// </summary>
internal interface IActionRunHost
{
    /// <summary>Reads the process-global ActionIsRunning flag.</summary>
    bool ReadActionIsRunning();

    /// <summary>
    /// Reads the current interaction level for diagnostics only. Never
    /// mutated by this operation.
    /// </summary>
    int ReadUserInteractionLevel();

    /// <summary>
    /// Dispatches one action. This is the operation's only mutating call and
    /// its only commit point.
    /// </summary>
    void DoScript(string name, string actionSet, bool dialogs);
}

/// <summary>
/// Monotonic clock for the bounded completion wait, so tests can advance time
/// deterministically instead of sleeping.
/// </summary>
internal interface IActionRunClock
{
    TimeSpan Elapsed { get; }

    void Wait(TimeSpan duration);
}

internal sealed class SystemActionRunClock : IActionRunClock
{
    public static readonly SystemActionRunClock Instance = new();

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public void Wait(TimeSpan duration)
    {
        if (duration > TimeSpan.Zero)
            Thread.Sleep(duration);
    }
}

/// <summary>
/// Real COM host surface. Every call goes through the shared
/// <see cref="IllustratorComInterop"/> helpers, so the reject-before-execution
/// retry budget and the ambiguous classification stay owned by one component
/// and this lane adds no second COM policy.
/// </summary>
internal sealed class ComActionRunHost : IActionRunHost
{
    private readonly object _app;

    public ComActionRunHost(object app) => _app = app;

    public bool ReadActionIsRunning()
    {
        dynamic app = _app;
        return IllustratorComInterop.RetryRead(
            () => Convert.ToBoolean(
                app.ActionIsRunning,
                CultureInfo.InvariantCulture));
    }

    public int ReadUserInteractionLevel()
    {
        dynamic app = _app;
        return IllustratorComInterop.RetryRead(
            () => Convert.ToInt32(
                app.UserInteractionLevel,
                CultureInfo.InvariantCulture));
    }

    public void DoScript(string name, string actionSet, bool dialogs)
    {
        // The live COM inventory declares
        // DoScript(Action: VT_BSTR, From: VT_BSTR, Dialogs: VT_VARIANT[opt]).
        // The dialogs argument is always passed explicitly (never
        // Type.Missing) so the host's optional-argument default can never be
        // inherited implicitly.
        IllustratorComInterop.InvokeMutationMethod(
            _app,
            "DoScript",
            [name, actionSet, dialogs]);
    }
}
