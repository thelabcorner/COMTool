using System.Text.Json;
using ComTool.Broker.Protocol;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;
using ComTool.Runtime;

namespace ComTool.Supervisor;

/// <summary>
/// Long-lived per-target owner. The target state machine and orchestration
/// lease outlive individual worker processes, so worker replacement cannot
/// erase ambiguity or exclusivity.
/// </summary>
public sealed class TargetSupervisor : IAsyncDisposable
{
    private readonly HostTargetDescriptor _target;
    private readonly WorkerBrokerOptions _options;
    private readonly TargetStateMachine _state;
    private readonly MutationLedger? _mutationLedger;
    private readonly string? _leaseLockDirectory;
    private readonly TargetLeaseManager _leases = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private WorkerBrokerClient? _worker;
    private TargetProcessLeaseLock? _processLeaseLock;
    private Timer? _leaseExpiryTimer;
    private int _disposed;

    public TargetSupervisor(
        HostTargetDescriptor target,
        WorkerBrokerOptions options,
        TargetStateMachine? stateMachine = null)
        : this(
            target,
            options,
            stateMachine,
            mutationLedger: null)
    {
    }

    internal TargetSupervisor(
        HostTargetDescriptor target,
        WorkerBrokerOptions options,
        TargetStateMachine? stateMachine,
        MutationLedger? mutationLedger,
        string? leaseLockDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);

        _target = target;
        _options = options;
        _state = stateMachine ?? new TargetStateMachine();
        _mutationLedger = mutationLedger;
        _leaseLockDirectory = leaseLockDirectory;

        var persisted = _mutationLedger?.GetUnresolvedTarget(
            target.Identity.TargetId);

        if (persisted is not null &&
            _state.Snapshot().State != TargetState.ReconciliationRequired)
        {
            _state.MarkAmbiguousExecution(
                persisted.MutationClass,
                persisted.Phase == MutationLedgerPhase.Prepared
                    ? "runtime_interrupted_mutation"
                    : persisted.IncidentKind
                        ?? "persisted_mutation_ambiguity");
        }
    }

    public HostTargetDescriptor Target => _target;

    public TargetStateSnapshot State => _state.Snapshot();

    public TargetLeaseStatus LeaseStatus => _leases.Status;

    public ActiveMutationIncident? ActiveIncident
    {
        get
        {
            var active = _mutationLedger?.GetUnresolvedTarget(
                _target.Identity.TargetId);
            return active is null
                ? null
                : new ActiveMutationIncident(
                    active.RequestId,
                    active.Operation,
                    active.MutationClass,
                    active.Phase switch
                    {
                        MutationLedgerPhase.Prepared => "prepared",
                        MutationLedgerPhase.Ambiguous => "ambiguous",
                        _ => throw new InvalidOperationException(
                            $"Resolved mutation phase '{active.Phase}' cannot be exposed as an active incident.")
                    },
                    active.PreparedAt,
                    active.UpdatedAt,
                    active.IncidentKind,
                    active.ReconciliationFingerprint);
        }
    }

    public BrokerWorkerStatus? Worker =>
        _worker is { IsAlive: true } worker
            ? worker.Worker
            : null;

    public async Task<TargetLeaseGrant> AcquireLeaseAsync(
        int? ttlMs = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _operationGate
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            ThrowIfDisposed();
            ReleaseExpiredProcessLeaseLockIfNeeded();

            var acquiredProcessLock = false;
            if (_processLeaseLock is null)
            {
                _processLeaseLock = TargetProcessLeaseLock.Acquire(
                    _target.Identity.TargetId,
                    _leaseLockDirectory);
                acquiredProcessLock = true;
            }

            try
            {
                var grant = _leases.Acquire(ttlMs);
                ScheduleLeaseExpiry(grant);
                return grant;
            }
            catch
            {
                if (acquiredProcessLock)
                    ReleaseProcessLeaseLockCore();
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<TargetLeaseGrant> RenewLeaseAsync(
        string leaseId,
        int? ttlMs = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _operationGate
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            ThrowIfDisposed();
            ReleaseExpiredProcessLeaseLockIfNeeded();

            if (_processLeaseLock is null)
            {
                throw new TargetLeaseException(
                    "target_lease_lock_lost",
                    "The local lease no longer owns the cross-process target lock.",
                    retryable: false);
            }

            var grant = _leases.Renew(leaseId, ttlMs);
            ScheduleLeaseExpiry(grant);
            return grant;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task ReleaseLeaseAsync(
        string leaseId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _operationGate
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            ThrowIfDisposed();
            ReleaseExpiredProcessLeaseLockIfNeeded();
            _leases.Release(leaseId);
            ReleaseProcessLeaseLockCore();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<T> WithLeaseAccessAsync<T>(
        string? leaseId,
        bool requiresLease,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ThrowIfDisposed();

        await _operationGate
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            ThrowIfDisposed();
            ReleaseExpiredProcessLeaseLockIfNeeded();
            _leases.EnsureAccess(leaseId, requiresLease);

            if (requiresLease && _processLeaseLock is null)
            {
                throw new TargetLeaseException(
                    "target_lease_lock_lost",
                    "The active lease no longer owns the cross-process target lock.",
                    retryable: false);
            }

            return await action(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<BrokerWorkerStatus> PingAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        return await WithLeaseAccessAsync(
            leaseId: null,
            requiresLease: false,
            async ct =>
            {
                var worker = await EnsureWorkerAsync(ct)
                    .ConfigureAwait(false);

                try
                {
                    return await worker
                        .PingAsync(ct)
                        .ConfigureAwait(false);
                }
                catch
                {
                    await DropDeadWorkerAsync(worker).ConfigureAwait(false);
                    throw;
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult> ExecuteAsync(
        OperationRequest request,
        TimeSpan? watchdog = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        try
        {
            ProtocolJson.ValidateRequest(request);
        }
        catch (ProtocolValidationException ex)
        {
            return Failure(
                request,
                OperationStatus.InvalidRequest,
                _state.Snapshot().State,
                ex.Kind,
                ex.Message,
                ExecutionState.NotStarted,
                suggestedActions:
                ["inspect_request_contract"]);
        }

        if (!BuiltInOperations.Catalog.TryGet(
                request.Operation,
                out var definition))
        {
            throw new InvalidOperationException(
                $"Operation '{request.Operation}' is not registered.");
        }

        MutationClass mutationClass;
        try
        {
            mutationClass = OperationMutationResolver.Resolve(
                definition,
                request);
        }
        catch (OperationMutationPolicyException ex)
        {
            return Failure(
                request,
                OperationStatus.InvalidRequest,
                _state.Snapshot().State,
                ex.Kind,
                ex.Message,
                ExecutionState.NotStarted,
                suggestedActions: ["inspect_operation_input"]);
        }

        try
        {
            OperationConditionEvaluator.ValidateSources(
                request.Preconditions,
                _target,
                "precondition");
            OperationConditionEvaluator.ValidateSources(
                request.Postconditions,
                _target,
                "postcondition");
        }
        catch (OperationConditionPolicyException ex)
        {
            return Failure(
                request,
                OperationStatus.InvalidRequest,
                _state.Snapshot().State,
                ex.Kind,
                ex.Message,
                ExecutionState.NotStarted,
                suggestedActions:
                ["inspect_condition_source"]);
        }

        return await WithLeaseAccessAsync(
            request.Policy?.LeaseId,
            definition.RequiresLease,
            async ct =>
            {
                try
                {
                    _state.EnsureOperationAllowed(mutationClass);
                }
                catch (TargetStateException ex)
                {
                    return Failure(
                        request,
                        ex.TargetState ==
                            TargetState.ReconciliationRequired
                            ? OperationStatus.ReconciliationRequired
                            : ex.TargetState == TargetState.Unavailable
                                ? OperationStatus.TargetUnavailable
                                : OperationStatus.HostBusy,
                        ex.TargetState,
                        ex.Kind,
                        ex.Message,
                        ExecutionState.NotStarted,
                        suggestedActions:
                            ex.TargetState ==
                                TargetState.ReconciliationRequired
                                ? ["reconcile_target_before_mutation"]
                                : ["ping_or_reconnect_target"],
                        retryable: ex.Retryable);
                }

                if (mutationClass != MutationClass.ReadOnly &&
                    _mutationLedger is null)
                {
                    return Failure(
                        request,
                        OperationStatus.Failed,
                        _state.Snapshot().State,
                        "mutation_ledger_unavailable",
                        "Mutating operations require a durable mutation ledger. Use the persistent runtime instead of an untracked direct supervisor.",
                        ExecutionState.NotStarted,
                        suggestedActions:
                        [
                            "use_persistent_runtime",
                            "do_not_execute_mutation"
                        ]);
                }

                var hasPreconditions =
                    request.Preconditions is { Count: > 0 };
                var hasPostconditions =
                    request.Postconditions is { Count: > 0 };

                MutationLedgerRecord? prepared = null;
                WorkerBrokerClient? worker = null;
                EvidenceItem? preconditionEvidence = null;
                double verifyMs = 0;

                // Condition-bearing mutations first inspect durable identity
                // without creating a prepared marker. This preserves exactly-once
                // replay while ensuring a crash during a read-only precondition
                // cannot masquerade as an in-flight mutation.
                if (mutationClass != MutationClass.ReadOnly &&
                    _mutationLedger is not null &&
                    (hasPreconditions || hasPostconditions))
                {
                    MutationLedgerProbeResult probe;
                    try
                    {
                        probe = _mutationLedger.Probe(
                            _target,
                            request,
                            mutationClass);
                    }
                    catch (MutationLedgerException ex)
                    {
                        return Failure(
                            request,
                            OperationStatus.Failed,
                            _state.Snapshot().State,
                            ex.Kind,
                            ex.Message,
                            ExecutionState.NotStarted,
                            suggestedActions:
                            [
                                "repair_runtime_state_storage",
                                "do_not_execute_mutation"
                            ]);
                    }

                    var probeResult =
                        HandleMutationLedgerDisposition(
                            request,
                            probe.Disposition,
                            probe.Record,
                            probe.StoredResult);

                    if (probeResult is not null)
                        return probeResult;
                }

                if (hasPreconditions)
                {
                    try
                    {
                        worker = await EnsureWorkerAsync(ct)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        return Failure(
                            request,
                            OperationStatus.Failed,
                            _state.Snapshot().State,
                            "precondition_worker_start_failed",
                            ex.Message,
                            ExecutionState.NotStarted,
                            suggestedActions:
                            ["retry_after_worker_recovery"],
                            hresult: ex.HResult);
                    }

                    var preconditions =
                        await OperationConditionEvaluator
                            .EvaluateAsync(
                                request.Preconditions,
                                "precondition",
                                request,
                                _target,
                                worker,
                                watchdog,
                                ct)
                            .ConfigureAwait(false);

                    verifyMs += preconditions.VerifyMs;
                    preconditionEvidence =
                        preconditions.Evidence;

                    if (!preconditions.Passed)
                    {
                        if (!worker.IsAlive)
                            await DropDeadWorkerAsync(worker)
                                .ConfigureAwait(false);

                        return CreatePreconditionFailure(
                            request,
                            preconditions,
                            verifyMs);
                    }
                }

                if (mutationClass != MutationClass.ReadOnly &&
                    _mutationLedger is not null)
                {
                    MutationLedgerBeginResult begin;
                    try
                    {
                        begin = _mutationLedger.Begin(
                            _target,
                            request,
                            mutationClass);
                    }
                    catch (MutationLedgerException ex)
                    {
                        return Failure(
                            request,
                            OperationStatus.Failed,
                            _state.Snapshot().State,
                            ex.Kind,
                            ex.Message,
                            ExecutionState.NotStarted,
                            suggestedActions:
                            [
                                "repair_runtime_state_storage",
                                "do_not_execute_mutation"
                            ],
                            evidence:
                                EvidenceArray(
                                    preconditionEvidence),
                            verifyMs: verifyMs);
                    }

                    var beginResult =
                        HandleMutationLedgerDisposition(
                            request,
                            begin.Disposition,
                            begin.Record,
                            begin.StoredResult);

                    if (beginResult is not null)
                        return beginResult;

                    prepared = begin.Record;
                }

                try
                {
                    worker ??= await EnsureWorkerAsync(ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (prepared is not null &&
                        _mutationLedger is not null)
                    {
                        TryFinalizeNotStarted(
                            prepared,
                            request,
                            ex);
                    }

                    throw;
                }

                var dispatchedRequest =
                    request.Preconditions is null &&
                    request.Postconditions is null
                        ? request
                        : request with
                        {
                            Preconditions = null,
                            Postconditions = null
                        };

                OperationResult result;
                try
                {
                    result = await worker
                        .ExecuteAsync(
                            dispatchedRequest,
                            watchdog,
                            ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (prepared is not null &&
                        _mutationLedger is not null)
                    {
                        TryMarkRuntimeInterrupted(
                            prepared,
                            mutationClass,
                            "supervisor_dispatch_interrupted");
                    }

                    if (!worker.IsAlive)
                        await DropDeadWorkerAsync(worker)
                            .ConfigureAwait(false);

                    return Failure(
                        request,
                        mutationClass == MutationClass.ReadOnly
                            ? OperationStatus.Failed
                            : OperationStatus.ReconciliationRequired,
                        mutationClass == MutationClass.ReadOnly
                            ? _state.Snapshot().State
                            : TargetState.ReconciliationRequired,
                        "supervisor_dispatch_interrupted",
                        $"Operation dispatch was interrupted: {ex.Message}",
                        mutationClass == MutationClass.ReadOnly
                            ? ExecutionState.NotStarted
                            : ExecutionState.Ambiguous,
                        suggestedActions:
                            mutationClass == MutationClass.ReadOnly
                                ? ["retry_after_worker_recovery"]
                                :
                                [
                                    "inspect_mutation_ledger",
                                    "apply_operation_specific_postconditions"
                                ],
                        evidence:
                            EvidenceArray(
                                preconditionEvidence),
                        verifyMs: verifyMs);
                }

                result = WithConditionEvidence(
                    result,
                    preconditionEvidence,
                    verifyMs);

                if (result.Ok &&
                    hasPostconditions)
                {
                    var postconditions =
                        await OperationConditionEvaluator
                            .EvaluateAsync(
                                request.Postconditions,
                                "postcondition",
                                request,
                                _target,
                                worker,
                                watchdog,
                                ct)
                            .ConfigureAwait(false);

                    verifyMs += postconditions.VerifyMs;

                    if (postconditions.Passed)
                    {
                        result = WithConditionEvidence(
                            result,
                            postconditions.Evidence,
                            verifyMs);
                    }
                    else if (
                        mutationClass !=
                            MutationClass.ReadOnly)
                    {
                        var incidentKind =
                            postconditions.Verified
                                ? "postcondition_failed"
                                : "postcondition_unverifiable";

                        _state.MarkAmbiguousExecution(
                            mutationClass,
                            incidentKind);

                        result = CreatePostconditionFailure(
                            request,
                            result,
                            postconditions,
                            incidentKind,
                            verifyMs);
                    }
                    else
                    {
                        result =
                            CreateReadOnlyPostconditionFailure(
                                request,
                                result,
                                postconditions,
                                verifyMs);
                    }
                }

                if (prepared is not null &&
                    _mutationLedger is not null)
                {
                    try
                    {
                        _mutationLedger.Finalize(
                            prepared,
                            result);
                    }
                    catch (MutationLedgerException ex)
                    {
                        _state.MarkAmbiguousExecution(
                            mutationClass,
                            ex.Kind);

                        return Failure(
                            request,
                            OperationStatus.ReconciliationRequired,
                            TargetState.ReconciliationRequired,
                            ex.Kind,
                            "The host operation returned, but its terminal outcome could not be durably recorded. The mutation is treated as ambiguous.",
                            ExecutionState.Ambiguous,
                            suggestedActions:
                            [
                                "repair_runtime_state_storage",
                                "inspect_mutation_ledger",
                                "apply_operation_specific_postconditions"
                            ],
                            evidence: result.Evidence,
                            verifyMs: verifyMs);
                    }
                }

                if (!worker.IsAlive)
                    await DropDeadWorkerAsync(worker)
                        .ConfigureAwait(false);

                return result;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<BrokerReconciliation> ReconcileAsync(
        string? leaseId = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        return await WithLeaseAccessAsync(
            leaseId,
            requiresLease: true,
            async ct =>
            {
                // Reconciliation is explicitly allowed to start a fresh worker
                // while preserving prior target ambiguity in _state.
                var worker = await EnsureWorkerAsync(ct)
                    .ConfigureAwait(false);

                var stateBefore = _state.Snapshot();

                var result = await worker
                    .ReconcileAsync(ct)
                    .ConfigureAwait(false);

                if (!worker.IsAlive)
                    await DropDeadWorkerAsync(worker).ConfigureAwait(false);

                // The durable ledger is authoritative. If an unresolved
                // mutation incident exists, generic host evidence may never
                // resolve it — even if the in-memory state machine has drifted
                // away from ReconciliationRequired (for example after a
                // rebuilt supervisor or a lost state update).
                var durableIncident = _mutationLedger?.GetUnresolvedTarget(
                    _target.Identity.TargetId);

                if (stateBefore.State == TargetState.ReconciliationRequired ||
                    durableIncident is not null)
                {
                    if (durableIncident is not null &&
                        stateBefore.State != TargetState.ReconciliationRequired)
                    {
                        _state.MarkAmbiguousExecution(
                            durableIncident.MutationClass,
                            durableIncident.IncidentKind
                                ?? "persisted_mutation_ambiguity");
                    }

                    // Availability evidence may update secondary observations,
                    // but the public ambiguity state remains authoritative.
                    if (result.TargetState == TargetState.Unavailable)
                        _state.MarkHostUnavailable("reconciliation_probe_unavailable");

                    return GenericReconciliationPolicy.Apply(
                        _state.Snapshot(),
                        result) with
                    {
                        TargetState = _state.Snapshot().State
                    };
                }

                if (result.Reconciled)
                {
                    _state.MarkReconciled(
                        result.TargetState == TargetState.KnownChanged);
                }
                else if (result.TargetState == TargetState.Unavailable)
                {
                    _state.MarkHostUnavailable("reconciliation_failed");
                }

                return result with
                {
                    TargetState = _state.Snapshot().State
                };
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Operation-specific reconciliation. This is the only generic-shaped path
    /// that may clear a durable mutation incident, and only when the caller
    /// supplies read-only postconditions that are proven true against the live
    /// host. Host liveness, a bare ping, or worker replacement never qualify.
    /// </summary>
    public async Task<BrokerReconciliation> ReconcileMutationAsync(
        string leaseId,
        string incidentRequestId,
        long expectedRevision,
        IReadOnlyList<OperationCondition>? postconditions,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(incidentRequestId);
        ThrowIfDisposed();

        return await WithLeaseAccessAsync(
            leaseId,
            requiresLease: true,
            async ct =>
            {
                var before = _state.Snapshot();

                if (before.State != TargetState.ReconciliationRequired)
                {
                    throw new TargetStateException(
                        "no_reconciliation_incident",
                        "The target has no unresolved reconciliation incident.",
                        before.State,
                        retryable: false);
                }

                if (before.Revision != expectedRevision)
                {
                    throw new TargetStateRevisionException(
                        expectedRevision,
                        before.Revision);
                }

                if (_mutationLedger is null)
                {
                    throw new MutationLedgerException(
                        "mutation_ledger_unavailable",
                        "This supervisor has no durable mutation ledger and cannot reconcile an incident from operation-specific evidence.");
                }

                var active = _mutationLedger.GetUnresolvedTarget(
                    _target.Identity.TargetId)
                    ?? throw new MutationLedgerException(
                        "mutation_incident_record_missing",
                        "Target state requires reconciliation but no durable active mutation record was found.");

                if (!string.Equals(
                        active.RequestId,
                        incidentRequestId,
                        StringComparison.Ordinal))
                {
                    throw new MutationLedgerException(
                        "mutation_incident_request_mismatch",
                        $"Active mutation incident belongs to request '{active.RequestId}', not '{incidentRequestId}'.");
                }

                // Validate that the supplied evidence sources are fixed
                // read-only host operations before touching the host. This
                // prevents a caller from smuggling a mutating source into a
                // reconciliation probe.
                OperationConditionEvaluator.ValidateSources(
                    postconditions,
                    _target,
                    "reconciliation");

                var suppliedReconciliationFingerprint =
                    MutationLedger.CreateReconciliationFingerprint(
                        postconditions);

                if (suppliedReconciliationFingerprint is not null &&
                    !string.Equals(
                        active.ReconciliationFingerprint,
                        suppliedReconciliationFingerprint,
                        StringComparison.Ordinal))
                {
                    return OperationSpecificReconciliationPolicy
                        .EvidenceNotBound(incidentRequestId);
                }

                ConditionBatchResult? evidence = null;

                if (postconditions is { Count: > 0 })
                {
                    var worker = await EnsureWorkerAsync(ct)
                        .ConfigureAwait(false);

                    var probeRequest = new OperationRequest
                    {
                        ProtocolVersion = ProtocolVersion.Current,
                        Id = incidentRequestId,
                        Target = _target.Target,
                        Operation = active.Operation,
                        Input = JsonSerializer.SerializeToElement(
                            new { })
                    };

                    evidence = await OperationConditionEvaluator
                        .EvaluateAsync(
                            postconditions,
                            "reconciliation",
                            probeRequest,
                            _target,
                            worker,
                            watchdog: null,
                            ct)
                        .ConfigureAwait(false);

                    if (!worker.IsAlive)
                        await DropDeadWorkerAsync(worker)
                            .ConfigureAwait(false);
                }

                var reconciliation =
                    OperationSpecificReconciliationPolicy.Apply(
                        before,
                        incidentRequestId,
                        evidence);

                if (!reconciliation.Reconciled)
                    return reconciliation with
                    {
                        TargetState = _state.Snapshot().State
                    };

                _mutationLedger.ResolveIncident(
                    _target.Identity.TargetId,
                    incidentRequestId,
                    changed: true,
                    rationale:
                        "Operation-specific postconditions were verified true against the live host, proving the ambiguous mutation's intended effect is present.",
                    evidence: evidence?.Evidence is { } resolutionEvidence
                        ? resolutionEvidence.Value
                        : null);

                // The operation gate holds this revision stable between the
                // preflight check and this transition.
                _state.MarkReconciled(
                    expectedRevision,
                    changed: true);

                return reconciliation with
                {
                    TargetState = _state.Snapshot().State
                };
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<MutationIncidentResolution> ResolveMutationIncidentAsync(
        string leaseId,
        string incidentRequestId,
        long expectedRevision,
        bool changed,
        string rationale,
        JsonElement? evidence = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(incidentRequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rationale);
        ThrowIfDisposed();

        if (rationale.Length > 4096)
            throw new ArgumentException(
                "Incident resolution rationale exceeds 4096 characters.",
                nameof(rationale));

        return await WithLeaseAccessAsync(
            leaseId,
            requiresLease: true,
            _ =>
            {
                var before = _state.Snapshot();

                if (before.State != TargetState.ReconciliationRequired)
                {
                    throw new TargetStateException(
                        "no_reconciliation_incident",
                        "The target has no unresolved reconciliation incident.",
                        before.State,
                        retryable: false);
                }

                if (before.Revision != expectedRevision)
                {
                    throw new TargetStateRevisionException(
                        expectedRevision,
                        before.Revision);
                }

                if (_mutationLedger is null)
                {
                    throw new MutationLedgerException(
                        "mutation_ledger_unavailable",
                        "This supervisor has no durable mutation ledger and cannot explicitly resolve an incident.");
                }

                var active = _mutationLedger.GetUnresolvedTarget(
                    _target.Identity.TargetId)
                    ?? throw new MutationLedgerException(
                        "mutation_incident_record_missing",
                        "Target state requires reconciliation but no durable active mutation record was found.");

                if (!string.Equals(
                        active.RequestId,
                        incidentRequestId,
                        StringComparison.Ordinal))
                {
                    throw new MutationLedgerException(
                        "mutation_incident_request_mismatch",
                        $"Active mutation incident belongs to request '{active.RequestId}', not '{incidentRequestId}'.");
                }

                var resolved = _mutationLedger.ResolveIncident(
                    _target.Identity.TargetId,
                    incidentRequestId,
                    changed,
                    rationale,
                    evidence);

                // The target operation gate prevents another target operation
                // from changing revision between the preflight check and here.
                _state.MarkReconciled(
                    expectedRevision,
                    changed);

                var after = _state.Snapshot();

                return Task.FromResult(
                    new MutationIncidentResolution(
                        incidentRequestId,
                        changed
                            ? "known_changed"
                            : "known_unchanged",
                        rationale,
                        resolved.ResolvedAt
                            ?? resolved.UpdatedAt,
                        after,
                        evidence?.Clone()));
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<BrokerWorkerStatus> RestartWorkerAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _operationGate
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            ThrowIfDisposed();
            return await RestartWorkerCoreAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task MarkTargetUnavailableAsync(
        string incidentKind = "target_not_discovered",
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _operationGate
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            ThrowIfDisposed();
            _state.MarkHostUnavailable(incidentKind);
            await StopWorkerCoreAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task StopWorkerAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _operationGate
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            ThrowIfDisposed();
            await StopWorkerCoreAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _leaseExpiryTimer?.Dispose();
            _leaseExpiryTimer = null;
            ReleaseProcessLeaseLockCore();

            await _lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_worker is not null)
                {
                    await _worker.DisposeAsync().ConfigureAwait(false);
                    _worker = null;
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }
        finally
        {
            _operationGate.Release();
            _lifecycleGate.Dispose();
            _operationGate.Dispose();
        }
    }

    private void ScheduleLeaseExpiry(
        TargetLeaseGrant grant) =>
        ScheduleLeaseExpiryAt(grant.ExpiresAt);

    private void ScheduleLeaseExpiryAt(DateTimeOffset expiresAt)
    {
        var due = expiresAt - DateTimeOffset.UtcNow;
        if (due < TimeSpan.Zero)
            due = TimeSpan.Zero;

        _leaseExpiryTimer ??= new Timer(
            static state =>
                ((TargetSupervisor)state!)
                    .OnLeaseExpiryTimer(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

        _leaseExpiryTimer.Change(
            due,
            Timeout.InfiniteTimeSpan);
    }

    private void OnLeaseExpiryTimer()
    {
        _ = ReleaseExpiredProcessLeaseLockAsync();
    }

    private async Task ReleaseExpiredProcessLeaseLockAsync()
    {
        var entered = false;

        try
        {
            await _operationGate.WaitAsync().ConfigureAwait(false);
            entered = true;

            if (Volatile.Read(ref _disposed) != 0)
                return;

            var status = _leases.Status;
            if (!status.Held)
            {
                ReleaseProcessLeaseLockCore();
                return;
            }

            if (status.ExpiresAt is { } expiresAt)
                ScheduleLeaseExpiryAt(expiresAt);
        }
        catch (ObjectDisposedException)
        {
            // Teardown won the race.
        }
        finally
        {
            if (entered)
            {
                try
                {
                    _operationGate.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }

    private void ReleaseExpiredProcessLeaseLockIfNeeded()
    {
        var status = _leases.Status;

        if (!status.Held)
        {
            ReleaseProcessLeaseLockCore();
            return;
        }

        if (status.ExpiresAt is { } expiresAt)
            ScheduleLeaseExpiryAt(expiresAt);
    }

    private void ReleaseProcessLeaseLockCore()
    {
        _leaseExpiryTimer?.Change(
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

        _processLeaseLock?.Dispose();
        _processLeaseLock = null;
    }

    private async Task<BrokerWorkerStatus> RestartWorkerCoreAsync(
        CancellationToken cancellationToken)
    {
        await _lifecycleGate
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            if (_worker is not null)
            {
                await _worker.DisposeAsync().ConfigureAwait(false);
                _worker = null;
            }

            _worker = await WorkerBrokerClient
                .LaunchAsync(
                    _target,
                    _options,
                    _state,
                    cancellationToken)
                .ConfigureAwait(false);

            return _worker.Worker;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopWorkerCoreAsync(
        CancellationToken cancellationToken)
    {
        await _lifecycleGate
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            if (_worker is null)
                return;

            await _worker.DisposeAsync().ConfigureAwait(false);
            _worker = null;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<WorkerBrokerClient> EnsureWorkerAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        var current = Volatile.Read(ref _worker);
        if (current is { IsAlive: true })
            return current;

        await _lifecycleGate
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            ThrowIfDisposed();

            current = _worker;
            if (current is { IsAlive: true })
                return current;

            if (current is not null)
            {
                await current.DisposeAsync().ConfigureAwait(false);
                _worker = null;
            }

            _worker = await WorkerBrokerClient
                .LaunchAsync(
                    _target,
                    _options,
                    _state,
                    cancellationToken)
                .ConfigureAwait(false);

            return _worker;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task DropDeadWorkerAsync(WorkerBrokerClient candidate)
    {
        if (candidate.IsAlive)
            return;

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_worker, candidate))
                return;

            await candidate.DisposeAsync().ConfigureAwait(false);
            _worker = null;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private OperationResult? HandleMutationLedgerDisposition(
        OperationRequest request,
        MutationLedgerBeginDisposition disposition,
        MutationLedgerRecord? record,
        OperationResult? storedResult)
    {
        if (disposition == MutationLedgerBeginDisposition.Proceed)
            return null;

        if (record is null)
        {
            return Failure(
                request,
                OperationStatus.Failed,
                _state.Snapshot().State,
                "mutation_ledger_record_missing",
                "Mutation ledger returned a terminal disposition without a record.",
                ExecutionState.NotStarted,
                suggestedActions:
                ["inspect_mutation_ledger"]);
        }

        switch (disposition)
        {
            case MutationLedgerBeginDisposition.ReplayCompleted:
                return storedResult is not null
                    ? ReplayStoredResult(
                        storedResult,
                        record)
                    : Failure(
                        request,
                        OperationStatus.Failed,
                        _state.Snapshot().State,
                        "mutation_ledger_result_missing",
                        "A completed mutation record has no stored operation result.",
                        ExecutionState.NotStarted,
                        suggestedActions:
                        [
                            "inspect_mutation_ledger",
                            "do_not_replay_mutation"
                        ]);

            case MutationLedgerBeginDisposition.Unresolved:
            case MutationLedgerBeginDisposition.TargetHasUnresolvedMutation:
                _state.MarkAmbiguousExecution(
                    record.MutationClass,
                    record.IncidentKind
                        ?? "persisted_mutation_ambiguity");

                return storedResult is not null
                    ? storedResult with
                    {
                        TargetState =
                            TargetState.ReconciliationRequired,
                        Status =
                            OperationStatus.ReconciliationRequired
                    }
                    : Failure(
                        request,
                        OperationStatus.ReconciliationRequired,
                        TargetState.ReconciliationRequired,
                        record.Phase ==
                            MutationLedgerPhase.Prepared
                            ? "runtime_interrupted_mutation"
                            : record.IncidentKind
                                ?? "persisted_mutation_ambiguity",
                        "A prior mutation with this target has an unresolved outcome; it will not be replayed.",
                        ExecutionState.Ambiguous,
                        suggestedActions:
                        [
                            "inspect_mutation_ledger",
                            "apply_operation_specific_postconditions",
                            "resolve_incident_explicitly"
                        ]);

            case MutationLedgerBeginDisposition.RequestIdConflict:
                return Failure(
                    request,
                    OperationStatus.InvalidRequest,
                    _state.Snapshot().State,
                    "request_id_reuse_mismatch",
                    "This mutation request id was already used for different semantic input.",
                    ExecutionState.NotStarted,
                    suggestedActions:
                    [
                        "use_original_request_payload",
                        "choose_new_request_id_for_new_intent"
                    ]);

            case MutationLedgerBeginDisposition.Resolved:
                return Failure(
                    request,
                    OperationStatus.InvalidRequest,
                    _state.Snapshot().State,
                    "mutation_request_already_resolved",
                    $"Mutation request '{record.RequestId}' was explicitly resolved as '{record.Resolution ?? "resolved"}' and will never be replayed.",
                    ExecutionState.NotStarted,
                    suggestedActions:
                    [
                        "inspect_resolution_record",
                        "choose_new_request_id_for_new_intent"
                    ]);

            default:
                return Failure(
                    request,
                    OperationStatus.Failed,
                    _state.Snapshot().State,
                    "mutation_ledger_invalid_decision",
                    "Mutation ledger returned an unknown disposition.",
                    ExecutionState.NotStarted);
        }
    }

    private OperationResult CreatePreconditionFailure(
        OperationRequest request,
        ConditionBatchResult conditions,
        double verifyMs)
    {
        var sourceFailure =
            conditions.SourceFailure;

        var status =
            sourceFailure?.Status switch
            {
                OperationStatus.HostBusy =>
                    OperationStatus.HostBusy,
                OperationStatus.TargetUnavailable =>
                    OperationStatus.TargetUnavailable,
                _ => OperationStatus.Failed
            };

        return Failure(
            request,
            status,
            _state.Snapshot().State,
            conditions.Verified
                ? "precondition_failed"
                : "precondition_unverifiable",
            conditions.FailureMessage
                ?? "A precondition failed.",
            ExecutionState.NotStarted,
            suggestedActions:
                conditions.Verified
                    ? ["inspect_precondition_evidence"]
                    :
                    [
                        "inspect_precondition_evidence",
                        "retry_only_after_source_recovery"
                    ],
            retryable:
                !conditions.Verified &&
                sourceFailure?.Error?.Retryable == true,
            evidence:
                EvidenceArray(
                    conditions.Evidence),
            verifyMs: verifyMs);
    }

    private OperationResult CreatePostconditionFailure(
        OperationRequest request,
        OperationResult hostResult,
        ConditionBatchResult conditions,
        string incidentKind,
        double verifyMs)
    {
        var hostCompletion = new EvidenceItem(
            "mutation.host_completion",
            JsonSerializer.SerializeToElement(new
            {
                hostResult.Status,
                hostResult.TargetState,
                result = hostResult.Result,
                hostEvidence = hostResult.Evidence,
                hostTiming = hostResult.Timing
            }));

        return new OperationResult
        {
            ProtocolVersion =
                ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = false,
            Status =
                OperationStatus.ReconciliationRequired,
            TargetState =
                TargetState.ReconciliationRequired,
            Error = new ProtocolError
            {
                Kind = incidentKind,
                Message =
                    conditions.FailureMessage
                    ?? "Mutation completed but its postcondition was not proven.",
                Retryable = false,
                Execution =
                    ExecutionState.Ambiguous,
                SuggestedActions =
                [
                    "inspect_postcondition_evidence",
                    "resolve_incident_explicitly"
                ]
            },
            Evidence = MergeEvidence(
                hostResult.Evidence,
                hostCompletion,
                conditions.Evidence),
            Timing =
                WithVerifyMs(
                    hostResult.Timing,
                    verifyMs)
        };
    }

    private OperationResult CreateReadOnlyPostconditionFailure(
        OperationRequest request,
        OperationResult hostResult,
        ConditionBatchResult conditions,
        double verifyMs) =>
        new()
        {
            ProtocolVersion =
                ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = false,
            Status =
                conditions.SourceFailure?.Status switch
                {
                    OperationStatus.HostBusy =>
                        OperationStatus.HostBusy,
                    OperationStatus.TargetUnavailable =>
                        OperationStatus.TargetUnavailable,
                    _ => OperationStatus.Failed
                },
            TargetState =
                _state.Snapshot().State,
            Error = new ProtocolError
            {
                Kind =
                    conditions.Verified
                        ? "postcondition_failed"
                        : "postcondition_unverifiable",
                Message =
                    conditions.FailureMessage
                    ?? "A postcondition failed.",
                Retryable =
                    !conditions.Verified &&
                    conditions.SourceFailure?.Error?.Retryable ==
                        true,
                Execution =
                    ExecutionState.Completed,
                SuggestedActions =
                ["inspect_postcondition_evidence"]
            },
            Evidence = MergeEvidence(
                hostResult.Evidence,
                conditions.Evidence),
            Timing =
                WithVerifyMs(
                    hostResult.Timing,
                    verifyMs)
        };

    private static OperationResult WithConditionEvidence(
        OperationResult result,
        EvidenceItem? evidence,
        double verifyMs) =>
        result with
        {
            Evidence =
                MergeEvidence(
                    result.Evidence,
                    evidence),
            Timing =
                WithVerifyMs(
                    result.Timing,
                    verifyMs)
        };

    private static OperationTiming WithVerifyMs(
        OperationTiming? timing,
        double verifyMs)
    {
        var existing =
            timing ?? new OperationTiming();

        return existing with
        {
            VerifyMs = verifyMs
        };
    }

    private static IReadOnlyList<EvidenceItem>? EvidenceArray(
        params EvidenceItem?[] evidence)
    {
        var items = evidence
            .Where(static item => item is not null)
            .Select(static item => item!)
            .ToArray();

        return items.Length == 0
            ? null
            : items;
    }

    private static IReadOnlyList<EvidenceItem>? MergeEvidence(
        IReadOnlyList<EvidenceItem>? existing,
        params EvidenceItem?[] appended)
    {
        var additions = appended
            .Where(static item => item is not null)
            .Select(static item => item!)
            .ToArray();

        if ((existing is null ||
             existing.Count == 0) &&
            additions.Length == 0)
            return null;

        if (existing is null ||
            existing.Count == 0)
            return additions;

        if (additions.Length == 0)
            return existing;

        return existing
            .Concat(additions)
            .ToArray();
    }

    private void TryFinalizeNotStarted(
        MutationLedgerRecord prepared,
        OperationRequest request,
        Exception exception)
    {
        if (_mutationLedger is null)
            return;

        try
        {
            _mutationLedger.Finalize(
                prepared,
                Failure(
                    request,
                    OperationStatus.Failed,
                    _state.Snapshot().State,
                    "worker_start_failed",
                    exception.Message,
                    ExecutionState.NotStarted,
                    hresult: exception.HResult),
                executionWasDispatched: false);
        }
        catch (MutationLedgerException)
        {
            // The durable prepared marker already exists. Leaving it in place
            // is conservative and will force reconciliation on restart.
        }
    }

    private void TryMarkRuntimeInterrupted(
        MutationLedgerRecord prepared,
        MutationClass mutationClass,
        string incidentKind)
    {
        try
        {
            _mutationLedger?.MarkRuntimeInterrupted(
                prepared,
                incidentKind);
        }
        catch (MutationLedgerException)
        {
            // Begin() already durably wrote the active prepared marker.
        }

        _state.MarkAmbiguousExecution(
            mutationClass,
            incidentKind);
    }

    private static OperationResult ReplayStoredResult(
        OperationResult stored,
        MutationLedgerRecord record)
    {
        var replayEvidence = new EvidenceItem(
            "mutation.replay",
            JsonSerializer.SerializeToElement(new
            {
                requestId = record.RequestId,
                phase = record.Phase
                    .ToString()
                    .ToLowerInvariant(),
                preparedAt = record.PreparedAt,
                completedAt = record.UpdatedAt,
                requestFingerprint =
                    record.RequestFingerprint
            }));

        var evidence = stored.Evidence is null
            ? new[] { replayEvidence }
            : stored.Evidence
                .Concat([replayEvidence])
                .ToArray();

        return stored with
        {
            Evidence = evidence,
            Timing = new OperationTiming(
                QueueMs: null,
                ExecuteMs: 0,
                VerifyMs: 0,
                TotalMs: null)
        };
    }

    private static OperationResult Failure(
        OperationRequest request,
        OperationStatus status,
        TargetState targetState,
        string kind,
        string message,
        ExecutionState execution,
        IReadOnlyList<string>? suggestedActions = null,
        bool retryable = false,
        int? hresult = null,
        IReadOnlyList<EvidenceItem>? evidence = null,
        double? verifyMs = null) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = false,
            Status = status,
            TargetState = targetState,
            Error = new ProtocolError
            {
                Kind = kind,
                Message = message,
                Retryable = retryable,
                Execution = execution,
                HResult = hresult,
                HResultHex = hresult is null
                    ? null
                    : $"0x{unchecked((uint)hresult.Value):X8}",
                SuggestedActions = suggestedActions
            },
            Evidence = evidence,
            Timing = verifyMs is null
                ? null
                : new OperationTiming(
                    VerifyMs: verifyMs)
        };

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
}

public sealed record ActiveMutationIncident(
    string RequestId,
    string Operation,
    MutationClass MutationClass,
    string Phase,
    DateTimeOffset PreparedAt,
    DateTimeOffset UpdatedAt,
    string? IncidentKind,
    string? ReconciliationFingerprint);
