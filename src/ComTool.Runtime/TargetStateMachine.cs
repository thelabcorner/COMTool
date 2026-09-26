using ComTool.Protocol;

namespace ComTool.Runtime;

public sealed class TargetStateMachine
{
    private readonly object _gate = new();
    private TargetState _state;
    private long _revision;
    private string? _incidentKind;

    public TargetStateMachine(TargetState initialState = TargetState.Known)
    {
        _state = initialState;
    }

    public TargetStateSnapshot Snapshot()
    {
        lock (_gate)
            return new TargetStateSnapshot(_state, _revision, _incidentKind);
    }

    public void EnsureOperationAllowed(MutationClass mutationClass)
    {
        lock (_gate)
        {
            if (_state == TargetState.Unavailable)
                throw new TargetStateException(
                    "target_unavailable",
                    "The target is unavailable.",
                    _state,
                    retryable: true);

            if (_state == TargetState.Busy)
                throw new TargetStateException(
                    "target_busy",
                    "The target is busy.",
                    _state,
                    retryable: true);

            if (_state == TargetState.ReconciliationRequired &&
                mutationClass != MutationClass.ReadOnly)
            {
                throw new TargetStateException(
                    "reconciliation_required",
                    "A prior mutation has ambiguous outcome; reconcile before another mutation.",
                    _state,
                    retryable: false);
            }
        }
    }

    public void MarkBusy()
    {
        lock (_gate)
            Transition(TargetState.Busy, incidentKind: null);
    }

    public void MarkCompleted(MutationClass mutationClass)
    {
        lock (_gate)
        {
            if (mutationClass == MutationClass.ReadOnly)
            {
                // A successful read proves availability, not that prior target
                // changes disappeared and not the outcome of an ambiguous
                // mutation. Preserve stronger state observations.
                if (_state is
                    TargetState.ReconciliationRequired or
                    TargetState.KnownChanged)
                    return;

                Transition(
                    TargetState.Known,
                    incidentKind: null);
                return;
            }

            Transition(
                TargetState.KnownChanged,
                incidentKind: null);
        }
    }

    public void MarkWorkerLost(bool executionStarted, MutationClass mutationClass)
    {
        lock (_gate)
        {
            // Once a mutation is ambiguous, neither a later read nor worker
            // transport loss is evidence that resolves it.
            if (_state == TargetState.ReconciliationRequired)
                return;

            var mutationMayHaveOccurred =
                executionStarted &&
                mutationClass is not MutationClass.ReadOnly;

            Transition(
                mutationMayHaveOccurred
                    ? TargetState.ReconciliationRequired
                    : TargetState.Unavailable,
                mutationMayHaveOccurred ? "ambiguous_worker_loss" : "worker_loss");
        }
    }

    public void MarkAmbiguousExecution(
        MutationClass mutationClass,
        string incidentKind = "ambiguous_execution")
    {
        lock (_gate)
        {
            if (mutationClass == MutationClass.ReadOnly)
            {
                // A later read failure must not launder uncertainty left by an
                // earlier mutation. In ordinary known state, the read itself
                // creates no mutation ambiguity and therefore changes nothing.
                return;
            }

            Transition(
                TargetState.ReconciliationRequired,
                incidentKind);
        }
    }

    public void MarkHostUnavailable(string incidentKind = "host_unavailable")
    {
        lock (_gate)
        {
            // Availability loss must never erase uncertainty about a prior
            // mutation. With the current single public state axis, unresolved
            // execution ambiguity takes precedence over host availability.
            if (_state == TargetState.ReconciliationRequired)
                return;

            Transition(TargetState.Unavailable, incidentKind);
        }
    }

    public void MarkReconnected()
    {
        lock (_gate)
        {
            // Reconnecting a transport/proxy cannot erase ambiguity about an
            // in-flight mutation. Only explicit reconciliation can do that.
            if (_state != TargetState.ReconciliationRequired)
                Transition(TargetState.Known, incidentKind: null);
        }
    }

    public void MarkReconciled(bool changed)
    {
        lock (_gate)
            Transition(
                changed ? TargetState.KnownChanged : TargetState.Known,
                incidentKind: null);
    }

    public void MarkReconciled(
        long expectedRevision,
        bool changed)
    {
        lock (_gate)
        {
            if (_state != TargetState.ReconciliationRequired)
            {
                throw new TargetStateException(
                    "no_reconciliation_incident",
                    "The target has no unresolved reconciliation incident.",
                    _state,
                    retryable: false);
            }

            if (_revision != expectedRevision)
            {
                throw new TargetStateRevisionException(
                    expectedRevision,
                    _revision);
            }

            Transition(
                changed
                    ? TargetState.KnownChanged
                    : TargetState.Known,
                incidentKind: null);
        }
    }

    private void Transition(TargetState state, string? incidentKind)
    {
        _state = state;
        _incidentKind = incidentKind;
        checked { _revision++; }
    }
}

public sealed record TargetStateSnapshot(
    TargetState State,
    long Revision,
    string? IncidentKind);

public sealed class TargetStateRevisionException(
    long expectedRevision,
    long actualRevision)
    : Exception(
        $"Target state revision changed from expected {expectedRevision} to {actualRevision}.")
{
    public long ExpectedRevision { get; } =
        expectedRevision;

    public long ActualRevision { get; } =
        actualRevision;
}

public sealed class TargetStateException(
    string kind,
    string message,
    TargetState targetState,
    bool retryable)
    : Exception(message)
{
    public string Kind { get; } = kind;
    public TargetState TargetState { get; } = targetState;
    public bool Retryable { get; } = retryable;
}
