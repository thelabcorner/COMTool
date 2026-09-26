using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ComTool.Broker.Protocol;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;
using ComTool.Runtime;

namespace ComTool.Supervisor;

/// <summary>
/// Host-agnostic long-lived runtime control plane. It owns target discovery,
/// per-target supervisors, runtime-scoped operations and routing into host
/// workers. It never talks to Adobe automation APIs directly.
/// </summary>
public sealed partial class RuntimeSupervisor : IAsyncDisposable
{
    private static readonly JsonSerializerOptions RuntimePayloadJson =
        new(JsonSerializerDefaults.Web);
    private static readonly Assembly RuntimeAssembly =
        typeof(RuntimeSupervisor).Assembly;
    private static readonly string RuntimeVersion =
        RuntimeAssembly.GetName().Version?.ToString()
        ?? "unknown";
    private static readonly string RuntimeInformationalVersion =
        RuntimeAssembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? RuntimeVersion;

    private readonly string[] _hosts;
    private readonly WorkerBrokerOptions _workerOptions;
    private readonly RuntimeStateLayout _stateLayout;
    private readonly MutationLedger _mutationLedger;
    private readonly ConcurrentDictionary<string, TargetSupervisor> _supervisors =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HostTargetDescriptor> _targets =
        new(StringComparer.Ordinal);
    // Strong target identity includes process generation, so a moderately
    // long discovery cache improves agent latency without ever permitting a
    // restarted Adobe process to masquerade as the previous target.
    private static readonly TimeSpan TargetDiscoveryCacheTtl =
        TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    private long _lastRefreshTimestamp;
    private int _disposed;

    public RuntimeSupervisor(
        IEnumerable<string> hosts,
        WorkerBrokerOptions workerOptions,
        string? stateDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(workerOptions);

        _hosts = hosts
            .Where(static host => !string.IsNullOrWhiteSpace(host))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (_hosts.Length == 0)
            throw new ArgumentException(
                "At least one host family must be configured.",
                nameof(hosts));

        _workerOptions = workerOptions;
        _stateLayout = RuntimeStateLayout.Open(stateDirectory);
        _mutationLedger = new MutationLedger(
            _stateLayout.MutationLedgerRoot);
        _workflowJobStore = new WorkflowJobStore(
            _stateLayout.WorkflowRoot);
    }

    public IReadOnlyCollection<string> Hosts => _hosts;

    public async Task<OperationResult> ExecuteAsync(
        OperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        var clock = Stopwatch.StartNew();

        if (request.ProtocolVersion != ProtocolVersion.Current)
        {
            return Failure(
                request,
                OperationStatus.InvalidRequest,
                TargetState.Known,
                "unsupported_protocol_version",
                $"Protocol version {request.ProtocolVersion} is not supported.",
                ExecutionState.NotStarted,
                totalMs: clock.Elapsed.TotalMilliseconds);
        }

        if (string.IsNullOrWhiteSpace(request.Id))
        {
            return Failure(
                request,
                OperationStatus.InvalidRequest,
                TargetState.Known,
                "request_id_required",
                "Request id must be non-empty.",
                ExecutionState.NotStarted,
                totalMs: clock.Elapsed.TotalMilliseconds);
        }

        if (!BuiltInOperations.Catalog.TryGet(request.Operation, out var definition))
        {
            return Failure(
                request,
                OperationStatus.UnsupportedOperation,
                TargetState.Known,
                "unsupported_operation",
                $"Operation '{request.Operation}' is not registered.",
                ExecutionState.NotStarted,
                totalMs: clock.Elapsed.TotalMilliseconds,
                suggestedActions: ["query_capabilities"]);
        }

        if (definition.RequiresTarget && request.Target is null)
        {
            return Failure(
                request,
                OperationStatus.InvalidRequest,
                TargetState.Known,
                "target_required",
                $"Operation '{request.Operation}' requires an explicit target.",
                ExecutionState.NotStarted,
                totalMs: clock.Elapsed.TotalMilliseconds,
                suggestedActions: ["core.targets.list"]);
        }

        if (definition.RequiresTarget &&
            string.IsNullOrWhiteSpace(request.Target!.Id))
        {
            return Failure(
                request,
                OperationStatus.InvalidRequest,
                TargetState.Known,
                "target_id_required",
                $"Operation '{request.Operation}' requires an explicit target id.",
                ExecutionState.NotStarted,
                totalMs: clock.Elapsed.TotalMilliseconds,
                suggestedActions: ["core.targets.list"]);
        }

        if (definition.Scope == OperationExecutionScope.Host)
        {
            if (definition.Host is not null &&
                !string.Equals(
                    definition.Host,
                    request.Target!.Host,
                    StringComparison.Ordinal))
            {
                return Failure(
                    request,
                    OperationStatus.UnsupportedOperation,
                    TargetState.Known,
                    "operation_host_mismatch",
                    $"Operation '{request.Operation}' is registered for host '{definition.Host}', not '{request.Target.Host}'.",
                    ExecutionState.NotStarted,
                    totalMs: clock.Elapsed.TotalMilliseconds,
                    suggestedActions: ["query_capabilities"]);
            }

            try
            {
                _ = OperationMutationResolver.Resolve(
                    definition,
                    request);
            }
            catch (OperationMutationPolicyException ex)
            {
                return Failure(
                    request,
                    OperationStatus.InvalidRequest,
                    TargetState.Known,
                    ex.Kind,
                    ex.Message,
                    ExecutionState.NotStarted,
                    totalMs: clock.Elapsed.TotalMilliseconds,
                    suggestedActions: ["inspect_operation_input"]);
            }
        }

        try
        {
            return definition.Scope switch
            {
                OperationExecutionScope.Runtime =>
                    await ExecuteRuntimeOperationAsync(
                        request,
                        clock,
                        cancellationToken).ConfigureAwait(false),

                OperationExecutionScope.Host =>
                    await ExecuteHostOperationAsync(
                        request,
                        clock,
                        cancellationToken).ConfigureAwait(false),

                _ => Failure(
                    request,
                    OperationStatus.Failed,
                    TargetState.Known,
                    "invalid_operation_scope",
                    "Operation has an unknown execution scope.",
                    ExecutionState.NotStarted,
                    totalMs: clock.Elapsed.TotalMilliseconds)
            };
        }
        catch (TargetLeaseException ex)
        {
            var state =
                request.Target?.Id is { Length: > 0 } leasedTargetId
                    ? GetKnownState(leasedTargetId)
                    : TargetState.Known;

            var contention =
                ex.Kind is "target_leased" or
                    "target_leased_external";

            var infrastructureFailure =
                ex.Kind is
                    "target_lease_lock_unavailable" or
                    "target_lease_lock_write_failed" or
                    "target_lease_lock_lost";

            return Failure(
                request,
                contention
                    ? OperationStatus.HostBusy
                    : infrastructureFailure
                        ? OperationStatus.Failed
                        : OperationStatus.InvalidRequest,
                state,
                ex.Kind,
                ex.Message,
                ExecutionState.NotStarted,
                clock.Elapsed.TotalMilliseconds,
                retryable: ex.Retryable,
                suggestedActions:
                    contention
                        ? ["retry_after_lease_release_or_expiry"]
                        : infrastructureFailure
                            ? ["inspect_runtime_lease_lock_state"]
                            : ["acquire_or_supply_target_lease"]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                request,
                OperationStatus.Failed,
                request.Target?.Id is { Length: > 0 } cancelledTargetId
                    ? GetKnownState(cancelledTargetId)
                    : TargetState.Known,
                "request_cancelled",
                "Request was cancelled.",
                ExecutionState.NotStarted,
                totalMs: clock.Elapsed.TotalMilliseconds,
                suggestedActions: ["retry_if_safe"]);
        }
        catch (Exception ex)
        {
            return Failure(
                request,
                OperationStatus.Failed,
                request.Target?.Id is { Length: > 0 } failedTargetId
                    ? GetKnownState(failedTargetId)
                    : TargetState.Known,
                "runtime_failure",
                ex.Message,
                ExecutionState.NotStarted,
                totalMs: clock.Elapsed.TotalMilliseconds,
                hresult: ex.HResult,
                suggestedActions: ["inspect_runtime"]);
        }
    }

    public async Task<IReadOnlyList<HostTargetDescriptor>> RefreshTargetsAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _refreshGate
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var discovered = new List<HostTargetDescriptor>();

            // Discovery workers are independent per host family, but keep this
            // sequential until adapters explicitly advertise concurrent-safe
            // discovery/resource behavior. Correctness beats shaving startup.
            foreach (var host in _hosts)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var hostTargets = await WorkerDiscoveryClient
                    .DiscoverAsync(
                        host,
                        _workerOptions,
                        cancellationToken)
                    .ConfigureAwait(false);

                discovered.AddRange(hostTargets);
            }

            var liveIds = discovered
                .Select(static target => target.Identity.TargetId)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var target in discovered)
            {
                _targets[target.Identity.TargetId] = target;

                _supervisors.GetOrAdd(
                    target.Identity.TargetId,
                    _ => new TargetSupervisor(
                        target,
                        _workerOptions,
                        stateMachine: null,
                        mutationLedger: _mutationLedger));
            }

            foreach (var entry in _targets.ToArray())
            {
                if (liveIds.Contains(entry.Key))
                    continue;

                var stale = entry.Value with { Running = false };
                _targets[entry.Key] = stale;

                if (_supervisors.TryGetValue(entry.Key, out var supervisor))
                {
                    await supervisor
                        .MarkTargetUnavailableAsync(
                            "target_not_discovered",
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            Volatile.Write(
                ref _lastRefreshTimestamp,
                Stopwatch.GetTimestamp());

            return SnapshotTargets();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<IReadOnlyList<HostTargetDescriptor>> GetTargetsAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var last = Volatile.Read(ref _lastRefreshTimestamp);
        var stale =
            last == 0 ||
            Stopwatch.GetElapsedTime(last) > TargetDiscoveryCacheTtl;

        if (forceRefresh || _targets.IsEmpty || stale)
            return await RefreshTargetsAsync(cancellationToken)
                .ConfigureAwait(false);

        return SnapshotTargets();
    }

    private IReadOnlyList<HostTargetDescriptor> SnapshotTargets() =>
        _targets.Values
            .OrderByDescending(static target => target.Running)
            .ThenBy(static target => target.Identity.Host, StringComparer.Ordinal)
            .ThenBy(static target => target.Identity.TargetId, StringComparer.Ordinal)
            .ToArray();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await StopWorkflowJobsAsync().ConfigureAwait(false);

        var supervisors = _supervisors.Values.ToArray();
        _supervisors.Clear();
        _targets.Clear();

        foreach (var supervisor in supervisors)
            await supervisor.DisposeAsync().ConfigureAwait(false);

        _refreshGate.Dispose();
    }

    private async Task<OperationResult> ExecuteRuntimeOperationAsync(
        OperationRequest request,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        switch (request.Operation)
        {
            case "core.runtime.health":
            {
                var payload = JsonSerializer.SerializeToElement(new
                {
                    protocolVersion = ProtocolVersion.Current,
                    runtime = "comtool-v2",
                    runtimeVersion = RuntimeVersion,
                    runtimeInformationalVersion =
                        RuntimeInformationalVersion,
                    stateSchemaVersion =
                        RuntimeStateLayout.CurrentSchemaVersion,
                    uptimeMs = Math.Round(_uptime.Elapsed.TotalMilliseconds, 3),
                    configuredHosts = _hosts,
                    knownTargets = _targets.Count,
                    liveWorkers = _supervisors.Values.Count(
                        static supervisor => supervisor.Worker is not null)
                }, RuntimePayloadJson);

                return Success(
                    request,
                    ProtocolValue.From(payload),
                    TargetState.Known,
                    clock.Elapsed.TotalMilliseconds);
            }

            case "core.workflow.submit":
                return await SubmitWorkflowAsync(
                        request,
                        clock,
                        cancellationToken)
                    .ConfigureAwait(false);

            case "core.workflow.get":
                return GetWorkflow(request, clock);

            case "core.workflow.cancel":
                return CancelWorkflow(request, clock);

            case "core.workflow.resume":
                return ResumeWorkflow(request, clock);

            case "core.targets.list":
            {
                var forceRefresh =
                    request.Input.ValueKind == JsonValueKind.Object &&
                    request.Input.TryGetProperty("refresh", out var refresh) &&
                    refresh.ValueKind == JsonValueKind.True;

                var targets = await GetTargetsAsync(
                        forceRefresh,
                        cancellationToken)
                    .ConfigureAwait(false);

                var payload = JsonSerializer.SerializeToElement(
                    targets.Select(target =>
                    {
                        _supervisors.TryGetValue(
                            target.Identity.TargetId,
                            out var supervisor);

                        return new
                        {
                            target = target.Target,
                            identity = target.Identity,
                            target.Running,
                            capabilities = target.Capabilities,
                            runtimeState = supervisor?.State,
                            lease = supervisor?.LeaseStatus
                        };
                    }).ToArray(),
                    RuntimePayloadJson);

                return Success(
                    request,
                    ProtocolValue.From(payload),
                    TargetState.Known,
                    clock.Elapsed.TotalMilliseconds);
            }

            case "core.target.capabilities":
            {
                var managed = await ResolveLiveTargetAsync(
                        request.Target!,
                        cancellationToken)
                    .ConfigureAwait(false);

                return await managed.Supervisor
                    .WithLeaseAccessAsync(
                        request.Policy?.LeaseId,
                        requiresLease: false,
                        _ =>
                        {
                            var payload = JsonSerializer.SerializeToElement(new
                            {
                                target = managed.Descriptor.Target,
                                capabilities = managed.Descriptor.Capabilities,
                                runtimeState = managed.Supervisor.State,
                                lease = managed.Supervisor.LeaseStatus
                            }, RuntimePayloadJson);

                            return Task.FromResult(Success(
                                request,
                                ProtocolValue.From(payload),
                                managed.Supervisor.State.State,
                                clock.Elapsed.TotalMilliseconds));
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            case "core.target.lease.acquire":
            {
                var managed = await ResolveLiveTargetAsync(
                        request.Target!,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (request.Policy?.LeaseId is not null)
                {
                    return Failure(
                        request,
                        OperationStatus.InvalidRequest,
                        managed.Supervisor.State.State,
                        "lease_id_not_allowed",
                        "Lease acquisition must not supply an existing leaseId.",
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds);
                }

                if (!TryReadLeaseTtl(
                        request,
                        allowTtl: true,
                        out var ttlMs,
                        out var leaseInputError))
                {
                    return Failure(
                        request,
                        OperationStatus.InvalidRequest,
                        managed.Supervisor.State.State,
                        "invalid_lease_request",
                        leaseInputError!,
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds);
                }

                var grant = await managed.Supervisor
                    .AcquireLeaseAsync(
                        ttlMs,
                        cancellationToken)
                    .ConfigureAwait(false);

                var payload = JsonSerializer.SerializeToElement(new
                {
                    target = managed.Descriptor.Target,
                    grant.LeaseId,
                    grant.AcquiredAt,
                    grant.ExpiresAt
                }, RuntimePayloadJson);

                return Success(
                    request,
                    ProtocolValue.From(payload),
                    managed.Supervisor.State.State,
                    clock.Elapsed.TotalMilliseconds);
            }

            case "core.target.lease.renew":
            {
                var managed = await ResolveLiveTargetAsync(
                        request.Target!,
                        cancellationToken)
                    .ConfigureAwait(false);

                var leaseId = RequireLeaseId(request);

                if (!TryReadLeaseTtl(
                        request,
                        allowTtl: true,
                        out var ttlMs,
                        out var leaseInputError))
                {
                    return Failure(
                        request,
                        OperationStatus.InvalidRequest,
                        managed.Supervisor.State.State,
                        "invalid_lease_request",
                        leaseInputError!,
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds);
                }

                var grant = await managed.Supervisor
                    .RenewLeaseAsync(
                        leaseId,
                        ttlMs,
                        cancellationToken)
                    .ConfigureAwait(false);

                var payload = JsonSerializer.SerializeToElement(new
                {
                    target = managed.Descriptor.Target,
                    grant.LeaseId,
                    grant.AcquiredAt,
                    grant.ExpiresAt
                }, RuntimePayloadJson);

                return Success(
                    request,
                    ProtocolValue.From(payload),
                    managed.Supervisor.State.State,
                    clock.Elapsed.TotalMilliseconds);
            }

            case "core.target.lease.release":
            {
                var managed = await ResolveLiveTargetAsync(
                        request.Target!,
                        cancellationToken)
                    .ConfigureAwait(false);

                var leaseId = RequireLeaseId(request);

                if (!TryReadLeaseTtl(
                        request,
                        allowTtl: false,
                        out _,
                        out var leaseInputError))
                {
                    return Failure(
                        request,
                        OperationStatus.InvalidRequest,
                        managed.Supervisor.State.State,
                        "invalid_lease_request",
                        leaseInputError!,
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds);
                }

                await managed.Supervisor
                    .ReleaseLeaseAsync(
                        leaseId,
                        cancellationToken)
                    .ConfigureAwait(false);

                var payload = JsonSerializer.SerializeToElement(new
                {
                    target = managed.Descriptor.Target,
                    released = true,
                    lease = managed.Supervisor.LeaseStatus
                }, RuntimePayloadJson);

                return Success(
                    request,
                    ProtocolValue.From(payload),
                    managed.Supervisor.State.State,
                    clock.Elapsed.TotalMilliseconds);
            }

            case "core.target.incident.resolve":
            {
                var managed = await ResolveLiveTargetAsync(
                        request.Target!,
                        cancellationToken)
                    .ConfigureAwait(false);

                var leaseId = RequireLeaseId(request);

                if (!TryReadIncidentResolution(
                        request,
                        out var resolutionInput,
                        out var resolutionInputError))
                {
                    return Failure(
                        request,
                        OperationStatus.InvalidRequest,
                        managed.Supervisor.State.State,
                        "invalid_incident_resolution",
                        resolutionInputError!,
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds);
                }

                MutationIncidentResolution resolution;
                try
                {
                    resolution = await managed.Supervisor
                        .ResolveMutationIncidentAsync(
                            leaseId,
                            resolutionInput!.IncidentRequestId,
                            resolutionInput.ExpectedRevision,
                            resolutionInput.Changed,
                            resolutionInput.Rationale,
                            resolutionInput.Evidence,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TargetStateRevisionException ex)
                {
                    return Failure(
                        request,
                        OperationStatus.InvalidRequest,
                        managed.Supervisor.State.State,
                        "target_state_revision_conflict",
                        ex.Message,
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds,
                        suggestedActions:
                        [
                            "refresh_target_state",
                            "reinspect_incident_before_resolution"
                        ],
                        retryable: true);
                }
                catch (TargetStateException ex)
                {
                    return Failure(
                        request,
                        OperationStatus.InvalidRequest,
                        ex.TargetState,
                        ex.Kind,
                        ex.Message,
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds,
                        suggestedActions:
                        [
                            "refresh_target_state",
                            "inspect_active_incident"
                        ],
                        retryable: ex.Retryable);
                }
                catch (MutationLedgerException ex)
                {
                    var infrastructureFailure =
                        ex.Kind is
                            "mutation_ledger_corrupt" or
                            "mutation_ledger_write_failed" or
                            "mutation_ledger_unavailable";

                    return Failure(
                        request,
                        infrastructureFailure
                            ? OperationStatus.Failed
                            : OperationStatus.InvalidRequest,
                        managed.Supervisor.State.State,
                        ex.Kind,
                        ex.Message,
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds,
                        suggestedActions:
                            infrastructureFailure
                                ? ["repair_runtime_state_storage"]
                                : ["inspect_active_incident"]);
                }

                var payload = JsonSerializer.SerializeToElement(new
                {
                    target = managed.Descriptor.Target,
                    incidentRequestId =
                        resolution.IncidentRequestId,
                    resolution = resolution.Resolution,
                    rationale = resolution.Rationale,
                    resolvedAt = resolution.ResolvedAt,
                    state = resolution.State,
                    evidence = resolution.Evidence
                }, RuntimePayloadJson);

                return Success(
                    request,
                    ProtocolValue.From(payload),
                    resolution.State.State,
                    clock.Elapsed.TotalMilliseconds,
                    evidence:
                    [
                        new EvidenceItem(
                            "mutation.incident.resolved",
                            payload.Clone())
                    ]);
            }

            case "core.target.reconcile":
            {
                var managed = await ResolveLiveTargetAsync(
                        request.Target!,
                        cancellationToken)
                    .ConfigureAwait(false);

                var reconciliation = await managed.Supervisor
                    .ReconcileAsync(
                        request.Policy?.LeaseId,
                        cancellationToken)
                    .ConfigureAwait(false);

                var payload = JsonSerializer.SerializeToElement(new
                {
                    reconciliation.Reconciled,
                    reconciliation.TargetState,
                    reconciliation.Evidence
                }, RuntimePayloadJson);

                return reconciliation.Reconciled
                    ? Success(
                        request,
                        ProtocolValue.From(payload),
                        managed.Supervisor.State.State,
                        clock.Elapsed.TotalMilliseconds,
                        reconciliation.Evidence)
                    : Failure(
                        request,
                        reconciliation.TargetState switch
                        {
                            TargetState.ReconciliationRequired =>
                                OperationStatus.ReconciliationRequired,
                            TargetState.Unavailable =>
                                OperationStatus.TargetUnavailable,
                            _ => OperationStatus.Failed
                        },
                        reconciliation.TargetState,
                        reconciliation.Error?.Kind ?? "reconciliation_failed",
                        reconciliation.Error?.Message ?? "Target reconciliation failed.",
                        reconciliation.Error?.Execution ?? ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds,
                        reconciliation.Error?.HResult,
                        reconciliation.Error?.SuggestedActions,
                        reconciliation.Error?.Retryable ?? false);
            }

            case "core.target.mutation.reconcile":
            {
                var managed = await ResolveLiveTargetAsync(
                        request.Target!,
                        cancellationToken)
                    .ConfigureAwait(false);

                var leaseId = RequireLeaseId(request);

                if (!TryReadMutationReconciliation(
                        request,
                        out var reconciliationInput,
                        out var reconciliationInputError))
                {
                    return Failure(
                        request,
                        OperationStatus.InvalidRequest,
                        managed.Supervisor.State.State,
                        "invalid_mutation_reconciliation",
                        reconciliationInputError!,
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds);
                }

                BrokerReconciliation reconciliation;
                try
                {
                    // Operation-specific path only. The generic reconcile path
                    // deliberately cannot clear a mutation incident from host
                    // liveness; this one requires verified, passing postconditions
                    // evaluated against live host state.
                    reconciliation = await managed.Supervisor
                        .ReconcileMutationAsync(
                            leaseId,
                            reconciliationInput!.IncidentRequestId,
                            reconciliationInput.ExpectedRevision,
                            request.Postconditions,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TargetStateRevisionException ex)
                {
                    return Failure(
                        request,
                        OperationStatus.InvalidRequest,
                        managed.Supervisor.State.State,
                        "target_state_revision_conflict",
                        ex.Message,
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds,
                        suggestedActions:
                        [
                            "refresh_target_state",
                            "reinspect_incident_before_resolution"
                        ],
                        retryable: true);
                }
                catch (TargetStateException ex)
                {
                    return Failure(
                        request,
                        OperationStatus.InvalidRequest,
                        ex.TargetState,
                        ex.Kind,
                        ex.Message,
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds,
                        suggestedActions:
                        [
                            "refresh_target_state",
                            "inspect_active_incident"
                        ],
                        retryable: ex.Retryable);
                }
                catch (OperationConditionPolicyException ex)
                {
                    return Failure(
                        request,
                        OperationStatus.InvalidRequest,
                        managed.Supervisor.State.State,
                        ex.Kind,
                        ex.Message,
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds,
                        suggestedActions: ["inspect_condition_source"]);
                }
                catch (MutationLedgerException ex)
                {
                    var infrastructureFailure =
                        ex.Kind is
                            "mutation_ledger_corrupt" or
                            "mutation_ledger_write_failed" or
                            "mutation_ledger_unavailable";

                    return Failure(
                        request,
                        infrastructureFailure
                            ? OperationStatus.Failed
                            : OperationStatus.InvalidRequest,
                        managed.Supervisor.State.State,
                        ex.Kind,
                        ex.Message,
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds,
                        suggestedActions:
                            infrastructureFailure
                                ? ["repair_runtime_state_storage"]
                                : ["inspect_active_incident"]);
                }

                var mutationPayload = JsonSerializer.SerializeToElement(new
                {
                    reconciliation.Reconciled,
                    reconciliation.TargetState,
                    reconciliation.Evidence
                }, RuntimePayloadJson);

                return reconciliation.Reconciled
                    ? Success(
                        request,
                        ProtocolValue.From(mutationPayload),
                        managed.Supervisor.State.State,
                        clock.Elapsed.TotalMilliseconds,
                        reconciliation.Evidence)
                    : Failure(
                        request,
                        reconciliation.TargetState switch
                        {
                            TargetState.ReconciliationRequired =>
                                OperationStatus.ReconciliationRequired,
                            TargetState.Unavailable =>
                                OperationStatus.TargetUnavailable,
                            _ => OperationStatus.Failed
                        },
                        reconciliation.TargetState,
                        reconciliation.Error?.Kind ??
                            "mutation_reconciliation_failed",
                        reconciliation.Error?.Message ??
                            "Operation-specific mutation reconciliation failed.",
                        reconciliation.Error?.Execution ??
                            ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds,
                        reconciliation.Error?.HResult,
                        reconciliation.Error?.SuggestedActions,
                        reconciliation.Error?.Retryable ?? false);
            }

            default:
                return Failure(
                    request,
                    OperationStatus.UnsupportedOperation,
                    TargetState.Known,
                    "runtime_operation_not_implemented",
                    $"Runtime operation '{request.Operation}' is not implemented.",
                    ExecutionState.NotStarted,
                    clock.Elapsed.TotalMilliseconds);
        }
    }

    private static bool TryReadMutationReconciliation(
        OperationRequest request,
        out MutationReconciliationInput? input,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(request);

        input = null;
        error = null;

        if (request.Input.ValueKind != JsonValueKind.Object)
        {
            error =
                "Mutation reconciliation input must be a JSON object.";
            return false;
        }

        string? incidentRequestId = null;
        long? expectedRevision = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in request.Input.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                error =
                    $"Duplicate mutation reconciliation field '{property.Name}'.";
                return false;
            }

            switch (property.Name)
            {
                case "incidentRequestId":
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        error = "'incidentRequestId' must be a string.";
                        return false;
                    }

                    incidentRequestId = property.Value.GetString();
                    if (string.IsNullOrWhiteSpace(incidentRequestId) ||
                        incidentRequestId.Length > 512)
                    {
                        error =
                            "'incidentRequestId' must be non-empty and at most 512 characters.";
                        return false;
                    }

                    break;

                case "expectedRevision":
                    if (property.Value.ValueKind != JsonValueKind.Number ||
                        !property.Value.TryGetInt64(out var parsedRevision) ||
                        parsedRevision < 0)
                    {
                        error =
                            "'expectedRevision' must be a non-negative 64-bit integer.";
                        return false;
                    }

                    expectedRevision = parsedRevision;
                    break;

                default:
                    error =
                        $"Unknown mutation reconciliation field '{property.Name}'.";
                    return false;
            }
        }

        if (incidentRequestId is null || expectedRevision is null)
        {
            error =
                "Mutation reconciliation requires incidentRequestId and a non-negative expectedRevision.";
            return false;
        }

        input = new MutationReconciliationInput(
            incidentRequestId,
            expectedRevision.Value);
        return true;
    }

    private async Task<OperationResult> ExecuteHostOperationAsync(
        OperationRequest request,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        var managed = await ResolveLiveTargetAsync(
                request.Target!,
                cancellationToken)
            .ConfigureAwait(false);

        var result = await managed.Supervisor
            .ExecuteAsync(
                request,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var timing = result.Timing ?? new OperationTiming();
        return result with
        {
            Timing = timing with
            {
                TotalMs = clock.Elapsed.TotalMilliseconds
            }
        };
    }

    private static string RequireLeaseId(OperationRequest request)
    {
        var leaseId = request.Policy?.LeaseId;
        if (string.IsNullOrWhiteSpace(leaseId))
        {
            throw new TargetLeaseException(
                "lease_required",
                "This operation requires policy.leaseId.",
                retryable: false);
        }

        return leaseId;
    }

    private static bool TryReadIncidentResolution(
        OperationRequest request,
        out IncidentResolutionInput? input,
        out string? error)
    {
        input = null;
        error = null;

        if (request.Input.ValueKind != JsonValueKind.Object)
        {
            error =
                "Incident resolution input must be a JSON object.";
            return false;
        }

        string? incidentRequestId = null;
        long? expectedRevision = null;
        string? resolution = null;
        string? rationale = null;
        JsonElement? evidence = null;

        var seen = new HashSet<string>(
            StringComparer.Ordinal);

        foreach (var property in request.Input.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                error =
                    $"Duplicate incident resolution field '{property.Name}'.";
                return false;
            }

            switch (property.Name)
            {
                case "incidentRequestId":
                    if (property.Value.ValueKind !=
                        JsonValueKind.String)
                    {
                        error =
                            "'incidentRequestId' must be a string.";
                        return false;
                    }

                    incidentRequestId =
                        property.Value.GetString();

                    if (string.IsNullOrWhiteSpace(
                            incidentRequestId) ||
                        incidentRequestId.Length > 512)
                    {
                        error =
                            "'incidentRequestId' must be non-empty and at most 512 characters.";
                        return false;
                    }

                    break;

                case "expectedRevision":
                    if (property.Value.ValueKind !=
                            JsonValueKind.Number ||
                        !property.Value.TryGetInt64(
                            out var parsedRevision) ||
                        parsedRevision < 0)
                    {
                        error =
                            "'expectedRevision' must be a non-negative 64-bit integer.";
                        return false;
                    }

                    expectedRevision = parsedRevision;
                    break;

                case "resolution":
                    if (property.Value.ValueKind !=
                        JsonValueKind.String)
                    {
                        error =
                            "'resolution' must be 'known_changed' or 'known_unchanged'.";
                        return false;
                    }

                    resolution =
                        property.Value.GetString();

                    if (resolution is not (
                            "known_changed" or
                            "known_unchanged"))
                    {
                        error =
                            "'resolution' must be 'known_changed' or 'known_unchanged'.";
                        return false;
                    }

                    break;

                case "rationale":
                    if (property.Value.ValueKind !=
                        JsonValueKind.String)
                    {
                        error =
                            "'rationale' must be a string.";
                        return false;
                    }

                    rationale =
                        property.Value.GetString();

                    if (string.IsNullOrWhiteSpace(rationale) ||
                        rationale.Length > 4096)
                    {
                        error =
                            "'rationale' must be non-empty and at most 4096 characters.";
                        return false;
                    }

                    rationale = rationale.Trim();
                    break;

                case "evidence":
                    evidence = property.Value.Clone();
                    break;

                default:
                    error =
                        $"Unknown incident resolution field '{property.Name}'.";
                    return false;
            }
        }

        if (incidentRequestId is null)
        {
            error = "'incidentRequestId' is required.";
            return false;
        }

        if (expectedRevision is null)
        {
            error = "'expectedRevision' is required.";
            return false;
        }

        if (resolution is null)
        {
            error = "'resolution' is required.";
            return false;
        }

        if (rationale is null)
        {
            error = "'rationale' is required.";
            return false;
        }

        input = new IncidentResolutionInput(
            incidentRequestId,
            expectedRevision.Value,
            string.Equals(
                resolution,
                "known_changed",
                StringComparison.Ordinal),
            rationale,
            evidence);

        return true;
    }

    private static bool TryReadLeaseTtl(
        OperationRequest request,
        bool allowTtl,
        out int? ttlMs,
        out string? error)
    {
        ttlMs = null;
        error = null;

        if (request.Input.ValueKind is
            JsonValueKind.Null or JsonValueKind.Undefined)
            return true;

        if (request.Input.ValueKind != JsonValueKind.Object)
        {
            error = "Lease operation input must be null or a JSON object.";
            return false;
        }

        foreach (var property in request.Input.EnumerateObject())
        {
            if (!string.Equals(
                    property.Name,
                    "ttlMs",
                    StringComparison.Ordinal) ||
                !allowTtl)
            {
                error = $"Unknown lease input field '{property.Name}'.";
                return false;
            }

            if (property.Value.ValueKind != JsonValueKind.Number ||
                !property.Value.TryGetInt32(out var parsedTtl))
            {
                error = "'ttlMs' must be an integer.";
                return false;
            }

            try
            {
                ttlMs = TargetLeaseManager.ValidateTtl(parsedTtl);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                error = ex.Message;
                return false;
            }
        }

        return true;
    }

    private async Task<ManagedTarget> ResolveLiveTargetAsync(
        TargetRef targetRef,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetRef.Id))
            throw new InvalidOperationException("Target id is required.");

        var targetId = targetRef.Id;

        if (!_targets.TryGetValue(targetId, out var descriptor) ||
            !descriptor.Running)
        {
            await RefreshTargetsAsync(cancellationToken).ConfigureAwait(false);

            if (!_targets.TryGetValue(targetId, out descriptor) ||
                !descriptor.Running)
            {
                throw new InvalidOperationException(
                    $"Target '{targetId}' is not currently running.");
            }
        }

        if (!string.Equals(
                descriptor.Identity.Host,
                targetRef.Host,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Target host does not match the registered target identity.");
        }

        if (!_supervisors.TryGetValue(
                descriptor.Identity.TargetId,
                out var supervisor))
        {
            throw new InvalidOperationException(
                "Target supervisor was not created during discovery.");
        }

        return new ManagedTarget(descriptor, supervisor);
    }

    private TargetState GetKnownState(string targetId) =>
        _supervisors.TryGetValue(targetId, out var supervisor)
            ? supervisor.State.State
            : TargetState.Unavailable;

    private static OperationResult Success(
        OperationRequest request,
        ProtocolValue result,
        TargetState state,
        double totalMs,
        IReadOnlyList<EvidenceItem>? evidence = null) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = true,
            Status = OperationStatus.Completed,
            TargetState = state,
            Result = result,
            Evidence = evidence,
            Timing = new OperationTiming(TotalMs: totalMs)
        };

    private static OperationResult Failure(
        OperationRequest request,
        OperationStatus status,
        TargetState state,
        string kind,
        string message,
        ExecutionState execution,
        double totalMs,
        int? hresult = null,
        IReadOnlyList<string>? suggestedActions = null,
        bool retryable = false) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = false,
            Status = status,
            TargetState = state,
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
            Timing = new OperationTiming(TotalMs: totalMs)
        };

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

    private sealed record IncidentResolutionInput(
        string IncidentRequestId,
        long ExpectedRevision,
        bool Changed,
        string Rationale,
        JsonElement? Evidence);

    private sealed record MutationReconciliationInput(
        string IncidentRequestId,
        long ExpectedRevision);

    private sealed record ManagedTarget(
        HostTargetDescriptor Descriptor,
        TargetSupervisor Supervisor);
}
