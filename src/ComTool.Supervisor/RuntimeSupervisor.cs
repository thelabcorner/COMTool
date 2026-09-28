using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ComTool.Broker.Protocol;
using ComTool.Hosts.Abstractions;
using ComTool.Knowledge;
using ComTool.Protocol;
using ComTool.Runtime;
using ComTool.Runtime.Artifacts;
using ComTool.Runtime.Examples;

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
    private const int ArtifactSweepWriteInterval = 64;

    private readonly string[] _hosts;
    private readonly WorkerBrokerOptions _workerOptions;
    private readonly RuntimeStateLayout _stateLayout;
    private readonly MutationLedger _mutationLedger;
    private readonly IArtifactStore _artifactStore;
    private readonly IHostTargetDiscovery _hostTargetDiscovery;
    private readonly HostAttachRuntime _hostAttachRuntime;
    private readonly HostOwnershipLedger _hostOwnershipLedger;
    private readonly IHostProcessIdentityProbe _hostProcessIdentityProbe;
    private readonly HostLaunchRuntime _hostLaunchRuntime;
    private readonly HostTerminationAuthority _hostTerminationAuthority;
    private readonly string _runtimeId;
    private readonly string? _runtimeEndpoint;
    private readonly string _frontEnd;
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
    private int _artifactWritesSinceSweep;
    private int _artifactSweepGate;
    private ArtifactSweepReport? _lastArtifactSweep;
    private DateTimeOffset? _lastArtifactSweepAtUtc;
    private string? _lastArtifactSweepError;
    private int _disposed;

    public RuntimeSupervisor(
        IEnumerable<string> hosts,
        WorkerBrokerOptions workerOptions,
        string? stateDirectory = null,
        string? runtimeEndpoint = null,
        string frontEnd = "embedded")
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

        using (var runtimeProcess = Process.GetCurrentProcess())
        {
            _runtimeId =
                $"runtime:{Environment.ProcessId}:" +
                new DateTimeOffset(runtimeProcess.StartTime)
                    .ToUniversalTime()
                    .Ticks;
        }

        _mutationLedger = new MutationLedger(
            _stateLayout.MutationLedgerRoot);
        _artifactStore = new FileSystemArtifactStore(
            new ArtifactStoreOptions
            {
                StateRoot = _stateLayout.Root
            });
        TrySweepArtifacts();
        _hostTargetDiscovery =
            new WorkerHostTargetDiscovery(_workerOptions);
        _hostAttachRuntime = new HostAttachRuntime(
            _hosts,
            _hostTargetDiscovery);
        _hostOwnershipLedger =
            new HostOwnershipLedger(_stateLayout.Root);
        _hostProcessIdentityProbe =
            new SystemHostProcessIdentityProbe();
        _hostLaunchRuntime = new HostLaunchRuntime(
            _hosts,
            _hostTargetDiscovery,
            host => new WorkerHostLaunchExecutor(
                host,
                _workerOptions),
            _hostOwnershipLedger,
            _hostProcessIdentityProbe,
            _runtimeId);
        _hostTerminationAuthority = new HostTerminationAuthority(
            _hostOwnershipLedger,
            _hostProcessIdentityProbe);
        _workflowJobStore = new WorkflowJobStore(
            _stateLayout.WorkflowRoot);
        _runtimeEndpoint = runtimeEndpoint;
        _frontEnd = string.IsNullOrWhiteSpace(frontEnd)
            ? "embedded"
            : frontEnd;
    }

    public IReadOnlyCollection<string> Hosts => _hosts;

    internal IArtifactStore ArtifactStore => _artifactStore;

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

        if (request.Policy?.RetryBudgetMs is { } retryBudgetMs &&
            !OperationPolicy.IsValidRetryBudget(retryBudgetMs))
        {
            return Failure(
                request,
                OperationStatus.InvalidRequest,
                TargetState.Known,
                "invalid_retry_budget",
                $"{nameof(OperationPolicy.RetryBudgetMs)} must be between " +
                $"{OperationPolicy.MinRetryBudgetMs} and " +
                $"{OperationPolicy.MaxRetryBudgetMs} ms.",
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
                suggestedActions: ["core.target.capabilities"]);
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
                    suggestedActions: ["core.target.capabilities"]);
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

            if (request.Operation is
                ComMutationKnowledgeValidator.SetOperation or
                ComMutationKnowledgeValidator.CallOperation or
                ComMutationKnowledgeValidator.GetOperation or
                ComMutationKnowledgeValidator.CallReadOperation)
            {
                ComInventoryValidationResult inventoryValidation;
                try
                {
                    inventoryValidation =
                        ComMutationKnowledgeValidator.Validate(
                            request.Operation,
                            request.Input);
                }
                catch (KnowledgePackException ex)
                {
                    return Failure(
                        request,
                        OperationStatus.Failed,
                        TargetState.Known,
                        ex.Kind,
                        ex.Message,
                        ExecutionState.NotStarted,
                        totalMs: clock.Elapsed.TotalMilliseconds,
                        suggestedActions:
                            ex.SuggestedActions ??
                            ["repair_knowledge_pack"]);
                }

                if (inventoryValidation.Invalid)
                {
                    return Failure(
                        request,
                        OperationStatus.InvalidRequest,
                        TargetState.Known,
                        inventoryValidation.Kind ??
                            "com_inventory_validation_failed",
                        inventoryValidation.Message ??
                            "The COM request contradicts the embedded inventory signature.",
                        ExecutionState.NotStarted,
                        totalMs: clock.Elapsed.TotalMilliseconds,
                        suggestedActions:
                        [
                            "knowledge.symbol",
                            "knowledge.paths",
                            "inspect_operation_input"
                        ]);
                }
            }
        }

        try
        {
            var result = definition.Scope switch
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

            // Runtime-scoped producers share the same public 1 MiB transport
            // ceiling as host results. Materialize large successful payloads
            // before they reach RuntimeHost transports so one large runtime
            // response cannot fault a framed client connection. Artifact
            // retrieval is the bounded escape hatch itself and must remain
            // inline rather than recursively offloading its base64 page.
            if (definition.Scope == OperationExecutionScope.Runtime &&
                !string.Equals(
                    request.Operation,
                    ArtifactRuntimeOperations.DescribeOperation,
                    StringComparison.Ordinal) &&
                !string.Equals(
                    request.Operation,
                    ArtifactRuntimeOperations.ReadOperation,
                    StringComparison.Ordinal))
            {
                result = MaterializeLargeOperationResult(
                    result,
                    definition.MutationClass);
            }

            return result;
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
            MutationLedgerRecord? durableIncident = null;
            var failedTargetId = request.Target?.Id;
            if (!string.IsNullOrEmpty(failedTargetId))
            {
                try
                {
                    durableIncident =
                        _mutationLedger.GetUnresolvedTarget(
                            failedTargetId);
                }
                catch (MutationLedgerException)
                {
                    // A corrupt/unreadable ledger is itself an uncertainty
                    // signal. Preserve conservative execution metadata below.
                    durableIncident = MutationLedgerRecord.CorruptActive(
                        failedTargetId);
                }
            }

            var ambiguous = durableIncident is not null;
            return Failure(
                request,
                ambiguous
                    ? OperationStatus.ReconciliationRequired
                    : OperationStatus.Failed,
                !string.IsNullOrEmpty(failedTargetId)
                    ? ambiguous
                        ? TargetState.ReconciliationRequired
                        : GetKnownState(failedTargetId)
                    : TargetState.Known,
                "runtime_failure",
                ex.Message,
                ambiguous
                    ? ExecutionState.Ambiguous
                    : ExecutionState.NotStarted,
                totalMs: clock.Elapsed.TotalMilliseconds,
                hresult: ex.HResult,
                suggestedActions:
                    ambiguous
                        ?
                        [
                            "inspect_mutation_ledger",
                            "reconcile_target_before_mutation"
                        ]
                        : ["inspect_runtime"]);
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

                var hostTargets = await _hostTargetDiscovery
                    .DiscoverAsync(
                        host,
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
                        mutationLedger: _mutationLedger,
                        resultMaterializer: MaterializeLargeHostResult));
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
        _stateLayout.Dispose();
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
                var activeIncidents =
                    _mutationLedger.ListUnresolved();
                var payload = JsonSerializer.SerializeToElement(new
                {
                    protocolVersion = ProtocolVersion.Current,
                    runtime = "comtool-v2",
                    runtimeVersion = RuntimeVersion,
                    runtimeInformationalVersion =
                        RuntimeInformationalVersion,
                    processId = Environment.ProcessId,
                    frontEnd = _frontEnd,
                    pipeName = _runtimeEndpoint,
                    stateRoot = _stateLayout.Root,
                    workerExecutablePath =
                        Path.GetFullPath(
                            _workerOptions.WorkerExecutablePath),
                    baseDirectory = AppContext.BaseDirectory,
                    stateSchemaVersion =
                        RuntimeStateLayout.CurrentSchemaVersion,
                    artifactLayoutVersion =
                        _artifactStore.LayoutVersion,
                    artifactSweep = new
                    {
                        lastAtUtc = _lastArtifactSweepAtUtc,
                        lastError = _lastArtifactSweepError,
                        scanned = _lastArtifactSweep?.Scanned,
                        expiredRemoved =
                            _lastArtifactSweep?.ExpiredRemoved,
                        blobsRemoved =
                            _lastArtifactSweep?.BlobsRemoved,
                        stagingFilesRemoved =
                            _lastArtifactSweep?.StagingFilesRemoved,
                        stagingBytesReclaimed =
                            _lastArtifactSweep?.StagingBytesReclaimed,
                        bytesReclaimed =
                            _lastArtifactSweep?.BytesReclaimed,
                        incomplete =
                            _lastArtifactSweep?.Incomplete,
                        contentReclamationSkipped =
                            _lastArtifactSweep?.
                                ContentReclamationSkipped
                    },
                    uptimeMs = Math.Round(_uptime.Elapsed.TotalMilliseconds, 3),
                    configuredHosts = _hosts,
                    knownTargets = _targets.Count,
                    liveWorkers = _supervisors.Values.Count(
                        static supervisor => supervisor.Worker is not null),
                    activeIncidents = activeIncidents.Count
                }, RuntimePayloadJson);

                return Success(
                    request,
                    ProtocolValue.From(payload),
                    TargetState.Known,
                    clock.Elapsed.TotalMilliseconds);
            }

            case "core.incidents.list":
            {
                var incidents = _mutationLedger.ListUnresolved();
                var payload = JsonSerializer.SerializeToElement(
                    incidents.Select(static incident => new
                    {
                        targetId = incident.TargetId,
                        host = incident.Host,
                        processId = incident.ProcessId,
                        processStartedAt = incident.ProcessStartedAt,
                        requestId = incident.RequestId,
                        operation = incident.Operation,
                        mutationClass = incident.MutationClass,
                        phase = incident.Phase switch
                        {
                            MutationLedgerPhase.Prepared => "prepared",
                            MutationLedgerPhase.Ambiguous => "ambiguous",
                            _ => throw new InvalidOperationException(
                                $"Inactive mutation phase '{incident.Phase}' cannot be listed as unresolved.")
                        },
                        incidentKind = incident.IncidentKind,
                        reconciliationFingerprint =
                            incident.ReconciliationFingerprint,
                        preparedAt = incident.PreparedAt,
                        updatedAt = incident.UpdatedAt,
                        recordCorrupt =
                            string.Equals(
                                incident.IncidentKind,
                                "mutation_ledger_corrupt",
                                StringComparison.Ordinal)
                    }).ToArray(),
                    RuntimePayloadJson);

                return Success(
                    request,
                    ProtocolValue.From(payload),
                    TargetState.Known,
                    clock.Elapsed.TotalMilliseconds);
            }

            case "core.incident.resolve":
            {
                if (!TryReadOfflineIncidentResolution(
                        request,
                        out var resolutionInput,
                        out var resolutionInputError))
                {
                    return Failure(
                        request,
                        OperationStatus.InvalidRequest,
                        TargetState.Known,
                        "invalid_incident_resolution",
                        resolutionInputError!,
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds);
                }

                var activeIncident = _mutationLedger.GetUnresolvedTarget(
                    resolutionInput!.TargetId);
                if (activeIncident is null)
                {
                    return Failure(
                        request,
                        OperationStatus.InvalidRequest,
                        TargetState.Unavailable,
                        "mutation_incident_not_found",
                        "No unresolved mutation incident exists for this target.",
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds);
                }

                // This path exists specifically for a generation that can no
                // longer acquire a lease. Check the durable process identity
                // directly instead of starting discovery/worker machinery.
                if (IsProcessGenerationRunning(activeIncident))
                {
                    return Failure(
                        request,
                        OperationStatus.InvalidRequest,
                        TargetState.Known,
                        "incident_target_still_running",
                        "The incident target generation is still running. Resolve it through core.target.incident.resolve with a target lease.",
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds,
                        suggestedActions:
                        [
                            "acquire_target_lease",
                            "use_core_target_incident_resolve"
                        ]);
                }

                MutationLedgerRecord resolved;
                try
                {
                    resolved = _mutationLedger.ResolveIncident(
                        resolutionInput.TargetId,
                        resolutionInput.IncidentRequestId,
                        resolutionInput.Changed,
                        resolutionInput.Rationale,
                        resolutionInput.Evidence,
                        resolutionInput.ExpectedUpdatedAt);
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
                        TargetState.Unavailable,
                        ex.Kind,
                        ex.Message,
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds,
                        suggestedActions:
                            ex.Kind == "mutation_incident_revision_conflict"
                                ? ["list_incidents_again"]
                                : ["inspect_mutation_ledger"],
                        retryable:
                            ex.Kind == "mutation_incident_revision_conflict");
                }

                var payload = JsonSerializer.SerializeToElement(new
                {
                    targetId = resolved.TargetId,
                    host = resolved.Host,
                    incidentRequestId = resolved.RequestId,
                    resolution = resolved.Resolution,
                    rationale = resolved.ResolutionRationale,
                    resolvedAt = resolved.ResolvedAt,
                    targetRunning = false
                }, RuntimePayloadJson);

                return Success(
                    request,
                    ProtocolValue.From(payload),
                    TargetState.Unavailable,
                    clock.Elapsed.TotalMilliseconds);
            }

            case "core.operations.list":
            {
                var payload = JsonSerializer.SerializeToElement(
                    BuiltInOperations.Catalog.Definitions
                        .OrderBy(
                            static operation => operation.Name,
                            StringComparer.Ordinal)
                        .Select(DescribeOperation)
                        .ToArray(),
                    RuntimePayloadJson);

                return Success(
                    request,
                    ProtocolValue.From(payload),
                    TargetState.Known,
                    clock.Elapsed.TotalMilliseconds);
            }

            case "core.operation.describe":
            {
                if (request.Input.ValueKind != JsonValueKind.Object ||
                    !request.Input.TryGetProperty("name", out var nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(nameElement.GetString()))
                {
                    return Failure(
                        request,
                        OperationStatus.InvalidRequest,
                        TargetState.Known,
                        "operation_name_required",
                        "core.operation.describe requires input.name as a non-empty operation name.",
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds,
                        suggestedActions: ["core.operations.list"]);
                }

                var name = nameElement.GetString()!;
                if (!BuiltInOperations.Catalog.TryGet(name, out var operation))
                {
                    return Failure(
                        request,
                        OperationStatus.UnsupportedOperation,
                        TargetState.Known,
                        "unsupported_operation",
                        $"Operation '{name}' is not registered.",
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds,
                        suggestedActions: ["core.operations.list"]);
                }

                var payload = JsonSerializer.SerializeToElement(
                    DescribeOperation(operation),
                    RuntimePayloadJson);

                return Success(
                    request,
                    ProtocolValue.From(payload),
                    TargetState.Known,
                    clock.Elapsed.TotalMilliseconds);
            }

            case OperationExamplesRegistry.OperationName:
            {
                var result = OperationExamplesRegistry.Execute(request);
                return result with
                {
                    Timing = new OperationTiming(
                        TotalMs: clock.Elapsed.TotalMilliseconds)
                };
            }

            case ArtifactRuntimeOperations.DescribeOperation:
            case ArtifactRuntimeOperations.ReadOperation:
                return ArtifactRuntimeOperations.Execute(
                    _artifactStore,
                    request,
                    clock.Elapsed.TotalMilliseconds,
                    cancellationToken);

            case IllustratorComKnowledgeService.DescribeOperation:
            case IllustratorComKnowledgeService.SearchOperation:
            case IllustratorComKnowledgeService.SymbolOperation:
            case IllustratorComKnowledgeService.EnumOperation:
            case IllustratorComKnowledgeService.PathsOperation:
            {
                var result =
                    IllustratorComKnowledgeService.Execute(request);
                return result with
                {
                    Timing = new OperationTiming(
                        TotalMs: clock.Elapsed.TotalMilliseconds)
                };
            }

            case ScriptEs3Preflight.Operation:
            {
                try
                {
                    var validationRequest =
                        ScriptEs3Preflight.ParseRequest(request.Input);
                    var analysis = ScriptEs3Preflight.Analyze(
                        validationRequest,
                        cancellationToken);
                    return ScriptEs3Preflight.BuildResult(
                        request,
                        analysis,
                        clock.Elapsed.TotalMilliseconds);
                }
                catch (ArgumentException ex)
                {
                    return Failure(
                        request,
                        OperationStatus.InvalidRequest,
                        TargetState.Known,
                        "invalid_script_validate_request",
                        ex.Message,
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds,
                        suggestedActions: ["inspect_operation_input"]);
                }
            }

            case WatchConditionPoller.OperationName:
                return await ExecuteWatchConditionAsync(
                        request,
                        clock,
                        cancellationToken)
                    .ConfigureAwait(false);

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

            case "core.target.attach":
            {
                if (request.Input.ValueKind != JsonValueKind.Object ||
                    request.Input.EnumerateObject().Any())
                {
                    return Failure(
                        request,
                        OperationStatus.InvalidRequest,
                        TargetState.Known,
                        "invalid_target_attach_request",
                        "core.target.attach uses the request target field and " +
                        "requires an empty input object.",
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds,
                        suggestedActions: ["core.targets.list"]);
                }

                try
                {
                    var attached = await _hostAttachRuntime
                        .AttachAsync(
                            request.Target!,
                            cancellationToken)
                        .ConfigureAwait(false);

                    _targets[attached.Identity.TargetId] = attached;
                    _supervisors.GetOrAdd(
                        attached.Identity.TargetId,
                        _ => new TargetSupervisor(
                            attached,
                            _workerOptions,
                            stateMachine: null,
                            mutationLedger: _mutationLedger,
                            resultMaterializer: MaterializeLargeHostResult));

                    var payload = JsonSerializer.SerializeToElement(
                        new
                        {
                            target = attached.Target,
                            identity = attached.Identity,
                            attached.Running,
                            capabilities = attached.Capabilities,
                            ownershipAcquired = false
                        },
                        RuntimePayloadJson);

                    return Success(
                        request,
                        ProtocolValue.From(payload),
                        TargetState.Known,
                        clock.Elapsed.TotalMilliseconds);
                }
                catch (HostAttachException ex)
                {
                    var missing =
                        ex.Kind == "target_generation_not_found";
                    var infrastructure =
                        ex.Kind == "target_discovery_failed";

                    return Failure(
                        request,
                        missing
                            ? OperationStatus.TargetUnavailable
                            : infrastructure
                                ? OperationStatus.Failed
                                : OperationStatus.InvalidRequest,
                        missing
                            ? TargetState.Unavailable
                            : TargetState.Known,
                        ex.Kind,
                        ex.Message,
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds,
                        retryable: ex.Retryable,
                        suggestedActions:
                        [
                            "core.targets.list",
                            "select_exact_running_generation"
                        ]);
                }
            }

            case "core.target.launch":
            {
                HostLaunchSpec spec;
                try
                {
                    if (request.Input.ValueKind != JsonValueKind.Object)
                    {
                        throw new JsonException(
                            "core.target.launch input must be a JSON object.");
                    }

                    spec = request.Input.Deserialize<HostLaunchSpec>(
                               RuntimePayloadJson)
                           ?? throw new JsonException(
                               "core.target.launch input decoded to null.");
                }
                catch (Exception ex) when (
                    ex is JsonException or NotSupportedException)
                {
                    return Failure(
                        request,
                        OperationStatus.InvalidRequest,
                        TargetState.Known,
                        "invalid_host_launch_request",
                        ex.Message,
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds,
                        suggestedActions: ["inspect_operation_input"]);
                }

                try
                {
                    var decision = await _hostLaunchRuntime
                        .LaunchAsync(
                            request.Id,
                            spec,
                            cancellationToken)
                        .ConfigureAwait(false);

                    var observation = decision.Observation;
                    if (observation.Ownership ==
                        HostLaunchOwnership.Unproven)
                    {
                        return Failure(
                            request,
                            OperationStatus.ReconciliationRequired,
                            TargetState.ReconciliationRequired,
                            observation.AmbiguityKind ??
                                "host_launch_unproven",
                            observation.AmbiguityMessage ??
                                "Host activation outcome could not be " +
                                "attributed to one strong generation.",
                            ExecutionState.Ambiguous,
                            clock.Elapsed.TotalMilliseconds,
                            suggestedActions:
                            [
                                "core.targets.list",
                                "do_not_retry_launch_until_generation_is_reconciled"
                            ]);
                    }

                    var identity = observation.Identity;
                    var payload = JsonSerializer.SerializeToElement(
                        new
                        {
                            host = observation.Host,
                            progId = observation.ProgId,
                            launchSpecKey = spec.SpecKey,
                            ownership = observation.Ownership,
                            target = identity is null
                                ? null
                                : new TargetRef(
                                    identity.Host,
                                    identity.TargetId,
                                    Generation: 0),
                            identity,
                            observation.ObservedAt,
                            observation.OpenDocumentCount,
                            observation.AttachHResult,
                            observation.ActivationHResult,
                            attempt = new
                            {
                                requestId =
                                    decision.Attempt.LaunchRequestId,
                                outcome =
                                    HostOwnershipLedger
                                        .DescribeAttemptOutcome(
                                            decision.Attempt.Outcome),
                                decision.Attempt.StartedAt,
                                decision.Attempt.CompletedAt,
                                decision.Attempt.ElapsedMs
                            },
                            durableOwnership =
                                decision.Ownership is null
                                    ? null
                                    : new
                                    {
                                        targetId =
                                            decision.Ownership.TargetId,
                                        state =
                                            decision.Ownership.State,
                                        launchRequestId =
                                            decision.Ownership
                                                .LaunchRequestId,
                                        launchSpecKey =
                                            decision.Ownership
                                                .LaunchSpecKey
                                    }
                        },
                        RuntimePayloadJson);

                    return Success(
                        request,
                        ProtocolValue.From(payload),
                        TargetState.Known,
                        clock.Elapsed.TotalMilliseconds);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    HostLaunchAttemptRecord? attempt = null;
                    try
                    {
                        attempt =
                            _hostOwnershipLedger.TryGetAttempt(
                                request.Id);
                    }
                    catch (HostOwnershipLedgerException)
                    {
                    }

                    var ambiguous = string.Equals(
                        attempt?.AmbiguityKind,
                        "launch_cancelled_after_dispatch",
                        StringComparison.Ordinal);

                    return Failure(
                        request,
                        ambiguous
                            ? OperationStatus.ReconciliationRequired
                            : OperationStatus.Failed,
                        ambiguous
                            ? TargetState.ReconciliationRequired
                            : TargetState.Known,
                        "request_cancelled",
                        ambiguous
                            ? "Launch request was cancelled after activation " +
                              "dispatch; ownership was not granted."
                            : "Launch request was cancelled before activation " +
                              "dispatch.",
                        ambiguous
                            ? ExecutionState.Ambiguous
                            : ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds,
                        suggestedActions:
                            ambiguous
                                ?
                                [
                                    "core.targets.list",
                                    "do_not_retry_launch_until_generation_is_reconciled"
                                ]
                                : ["retry_if_safe"]);
                }
                catch (HostLaunchException ex)
                {
                    var ambiguous =
                        ex.Execution == ExecutionState.Ambiguous;
                    var infrastructure = ex.Kind is
                        "launch_worker_not_found" or
                        "launch_worker_start_failed" or
                        "launch_worker_handshake_failed" or
                        "launch_worker_failure" or
                        "host_launch_runtime_failure" or
                        "host_ownership_unavailable" or
                        "host_ownership_corrupt" or
                        "host_ownership_schema_mismatch" or
                        "host_ownership_write_failed";

                    return Failure(
                        request,
                        ambiguous
                            ? OperationStatus.ReconciliationRequired
                            : infrastructure
                                ? OperationStatus.Failed
                                : OperationStatus.InvalidRequest,
                        ambiguous
                            ? TargetState.ReconciliationRequired
                            : TargetState.Known,
                        ex.Kind,
                        ex.Message,
                        ex.Execution,
                        clock.Elapsed.TotalMilliseconds,
                        hresult: ex.HResultCode,
                        retryable: ex.Retryable,
                        suggestedActions:
                            ambiguous
                                ?
                                [
                                    "core.targets.list",
                                    "do_not_retry_launch_until_generation_is_reconciled"
                                ]
                                : infrastructure
                                    ? ["inspect_runtime"]
                                    :
                                    [
                                        "inspect_operation_input",
                                        "core.targets.list"
                                    ]);
                }
            }

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
                            lease = supervisor?.LeaseStatus,
                            activeIncident = supervisor?.ActiveIncident
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
                                lease = managed.Supervisor.LeaseStatus,
                                activeIncident = managed.Supervisor.ActiveIncident
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
                // Releasing ownership is local runtime state and must remain
                // possible after the host exits or is break-glass terminated.
                // Never force fresh COM discovery merely to relinquish a lease.
                var managed = ResolveKnownTarget(request.Target!);

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

            case "core.target.host.terminate":
            {
                ManagedTarget managed;
                try
                {
                    managed = ResolveKnownTarget(request.Target!);
                }
                catch (InvalidOperationException ex)
                {
                    return Failure(
                        request,
                        OperationStatus.InvalidRequest,
                        TargetState.Unavailable,
                        "target_not_known",
                        ex.Message,
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds,
                        suggestedActions:
                        ["inspect_core_targets_list_before_termination"]);
                }

                var leaseId = RequireLeaseId(request);

                if (!TryReadHostTermination(
                        request,
                        out var terminationInput,
                        out var terminationInputError))
                {
                    return Failure(
                        request,
                        OperationStatus.InvalidRequest,
                        managed.Supervisor.State.State,
                        "invalid_host_termination",
                        terminationInputError!,
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds);
                }

                if (terminationInput!.ExpectedProcessId !=
                        managed.Descriptor.Identity.ProcessId ||
                    terminationInput.ExpectedProcessStartedAt
                            .ToUniversalTime() !=
                        managed.Descriptor.Identity.ProcessStartedAt
                            .ToUniversalTime())
                {
                    return Failure(
                        request,
                        OperationStatus.InvalidRequest,
                        managed.Supervisor.State.State,
                        "target_termination_confirmation_mismatch",
                        "The supplied process identity does not match the known target generation.",
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds,
                        suggestedActions:
                        ["inspect_core_targets_list_before_termination"]);
                }

                HostOwnershipRecord ownership;
                try
                {
                    ownership = await _hostTerminationAuthority
                        .RequireOwnedGenerationAsync(
                            managed.Descriptor,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (HostTerminationAuthorityException ex)
                {
                    return Failure(
                        request,
                        ex.ReconciliationRequired
                            ? OperationStatus.ReconciliationRequired
                            : OperationStatus.InvalidRequest,
                        ex.ReconciliationRequired
                            ? TargetState.ReconciliationRequired
                            : managed.Supervisor.State.State,
                        ex.Kind,
                        ex.Message,
                        ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds,
                        retryable: false,
                        suggestedActions:
                            ex.ReconciliationRequired
                                ?
                                [
                                    "refresh_target_identity_before_termination",
                                    "inspect_host_launch_provenance",
                                    "do_not_terminate_unproven_generation"
                                ]
                                :
                                [
                                    "inspect_host_launch_provenance",
                                    "do_not_terminate_unowned_generation"
                                ]);
                }

                TargetHostTerminationResult termination;
                try
                {
                    termination = await managed.Supervisor
                        .TerminateHostGenerationAsync(
                            leaseId,
                            terminationInput.ExpectedProcessId,
                            terminationInput.ExpectedProcessStartedAt,
                            terminationInput.WaitTimeoutMs,
                            cancellationToken)
                        .ConfigureAwait(false);
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
                        ["refresh_target_identity_before_termination"]);
                }

                HostOwnershipRecord finalOwnership = ownership;
                try
                {
                    if (termination.ExitObserved)
                    {
                        finalOwnership = _hostOwnershipLedger.MarkState(
                            managed.Descriptor.Identity.TargetId,
                            HostOwnershipRecordState.Quitted,
                            DateTimeOffset.UtcNow,
                            termination.AlreadyExited
                                ? "Owned host generation had already exited when termination was finalized."
                                : "Owned host generation exit was observed after explicit termination.");
                    }
                    else if (termination.KillIssued)
                    {
                        finalOwnership = _hostOwnershipLedger.MarkState(
                            managed.Descriptor.Identity.TargetId,
                            HostOwnershipRecordState.Unproven,
                            DateTimeOffset.UtcNow,
                            "Termination was issued but process exit was not observed; ownership is no longer safe for cleanup authority.");
                    }
                }
                catch (HostOwnershipLedgerException ex)
                {
                    return Failure(
                        request,
                        OperationStatus.ReconciliationRequired,
                        TargetState.ReconciliationRequired,
                        ex.Kind,
                        "Host lifecycle action completed or may have completed, " +
                        "but durable ownership provenance could not be finalized: " +
                        ex.Message,
                        termination.ExitObserved
                            ? ExecutionState.Completed
                            : termination.KillIssued
                                ? ExecutionState.Ambiguous
                                : ExecutionState.NotStarted,
                        clock.Elapsed.TotalMilliseconds,
                        retryable: false,
                        suggestedActions:
                        [
                            "inspect_process_generation",
                            "inspect_host_launch_provenance",
                            "do_not_retry_host_termination_until_reconciled"
                        ]);
                }

                if (!termination.ExitObserved)
                {
                    return Failure(
                        request,
                        OperationStatus.ReconciliationRequired,
                        TargetState.ReconciliationRequired,
                        "host_termination_exit_unconfirmed",
                        "Host termination was issued, but process exit was not observed before the recovery wait expired. Durable ownership was downgraded to unproven.",
                        ExecutionState.Ambiguous,
                        clock.Elapsed.TotalMilliseconds,
                        suggestedActions:
                        [
                            "inspect_process_generation",
                            "inspect_host_launch_provenance",
                            "do_not_retry_host_termination_until_reconciled"
                        ]);
                }

                var payload = JsonSerializer.SerializeToElement(
                    new
                    {
                        target = managed.Descriptor.Target,
                        processId = termination.ProcessId,
                        processStartedAt = termination.ProcessStartedAt,
                        termination.KillIssued,
                        termination.AlreadyExited,
                        termination.ExitObserved,
                        termination.WorkerAbortIssued,
                        durableOwnership = new
                        {
                            targetId = finalOwnership.TargetId,
                            state = finalOwnership.State,
                            finalOwnership.UpdatedAt
                        }
                    },
                    RuntimePayloadJson);

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

    private static bool TryReadHostTermination(
        OperationRequest request,
        out HostTerminationInput? input,
        out string? error)
    {
        input = null;
        error = null;

        if (request.Input.ValueKind != JsonValueKind.Object)
        {
            error = "Host termination input must be a JSON object.";
            return false;
        }

        int? expectedProcessId = null;
        DateTimeOffset? expectedProcessStartedAt = null;
        var waitTimeoutMs =
            TargetSupervisor.DefaultHostTerminationWaitMs;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in request.Input.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                error =
                    $"Duplicate host termination field '{property.Name}'.";
                return false;
            }

            switch (property.Name)
            {
                case "expectedProcessId":
                    if (property.Value.ValueKind != JsonValueKind.Number ||
                        !property.Value.TryGetInt32(out var parsedProcessId) ||
                        parsedProcessId <= 0)
                    {
                        error =
                            "'expectedProcessId' must be a positive integer from core.targets.list.";
                        return false;
                    }

                    expectedProcessId = parsedProcessId;
                    break;

                case "expectedProcessStartedAt":
                    if (property.Value.ValueKind != JsonValueKind.String ||
                        !DateTimeOffset.TryParse(
                            property.Value.GetString(),
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind,
                            out var parsedStartedAt))
                    {
                        error =
                            "'expectedProcessStartedAt' must be the ISO-8601 process start timestamp from core.targets.list.";
                        return false;
                    }

                    expectedProcessStartedAt = parsedStartedAt;
                    break;

                case "waitTimeoutMs":
                    if (property.Value.ValueKind != JsonValueKind.Number ||
                        !property.Value.TryGetInt32(out var parsedWaitTimeout) ||
                        parsedWaitTimeout is <
                            TargetSupervisor.MinHostTerminationWaitMs or
                            > TargetSupervisor.MaxHostTerminationWaitMs)
                    {
                        error =
                            $"'waitTimeoutMs' must be between {TargetSupervisor.MinHostTerminationWaitMs} and {TargetSupervisor.MaxHostTerminationWaitMs} ms.";
                        return false;
                    }

                    waitTimeoutMs = parsedWaitTimeout;
                    break;

                default:
                    error =
                        $"Unknown host termination field '{property.Name}'.";
                    return false;
            }
        }

        if (expectedProcessId is null ||
            expectedProcessStartedAt is null)
        {
            error =
                "'expectedProcessId' and 'expectedProcessStartedAt' are required.";
            return false;
        }

        input = new HostTerminationInput(
            expectedProcessId.Value,
            expectedProcessStartedAt.Value,
            waitTimeoutMs);
        return true;
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

    private static bool TryReadOfflineIncidentResolution(
        OperationRequest request,
        out OfflineIncidentResolutionInput? input,
        out string? error)
    {
        input = null;
        error = null;

        if (request.Input.ValueKind != JsonValueKind.Object)
        {
            error =
                "Offline incident resolution input must be a JSON object.";
            return false;
        }

        string? targetId = null;
        string? incidentRequestId = null;
        DateTimeOffset? expectedUpdatedAt = null;
        string? resolution = null;
        string? rationale = null;
        JsonElement? evidence = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in request.Input.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                error =
                    $"Duplicate offline incident resolution field '{property.Name}'.";
                return false;
            }

            switch (property.Name)
            {
                case "targetId":
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        error = "'targetId' must be a string.";
                        return false;
                    }
                    targetId = property.Value.GetString();
                    if (string.IsNullOrWhiteSpace(targetId) ||
                        targetId.Length > 512)
                    {
                        error =
                            "'targetId' must be non-empty and at most 512 characters.";
                        return false;
                    }
                    break;

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

                case "expectedUpdatedAt":
                    if (property.Value.ValueKind != JsonValueKind.String ||
                        !DateTimeOffset.TryParse(
                            property.Value.GetString(),
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind,
                            out var parsedUpdatedAt))
                    {
                        error =
                            "'expectedUpdatedAt' must be an ISO-8601 timestamp from core.incidents.list.";
                        return false;
                    }
                    expectedUpdatedAt = parsedUpdatedAt;
                    break;

                case "resolution":
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        error =
                            "'resolution' must be 'known_changed' or 'known_unchanged'.";
                        return false;
                    }
                    resolution = property.Value.GetString();
                    if (resolution is not (
                            "known_changed" or "known_unchanged"))
                    {
                        error =
                            "'resolution' must be 'known_changed' or 'known_unchanged'.";
                        return false;
                    }
                    break;

                case "rationale":
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        error = "'rationale' must be a string.";
                        return false;
                    }
                    rationale = property.Value.GetString();
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
                        $"Unknown offline incident resolution field '{property.Name}'.";
                    return false;
            }
        }

        if (targetId is null ||
            incidentRequestId is null ||
            expectedUpdatedAt is null ||
            resolution is null ||
            rationale is null)
        {
            error =
                "'targetId', 'incidentRequestId', 'expectedUpdatedAt', 'resolution', and 'rationale' are required.";
            return false;
        }

        input = new OfflineIncidentResolutionInput(
            targetId,
            incidentRequestId,
            expectedUpdatedAt.Value,
            string.Equals(
                resolution,
                "known_changed",
                StringComparison.Ordinal),
            rationale,
            evidence);

        return true;
    }

    private static bool IsProcessGenerationRunning(
        MutationLedgerRecord incident)
    {
        if (incident.ProcessId <= 0)
            return false;

        try
        {
            using var process =
                Process.GetProcessById(incident.ProcessId);
            var observedStart =
                new DateTimeOffset(process.StartTime).ToUniversalTime();
            return observedStart ==
                incident.ProcessStartedAt.ToUniversalTime();
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Fail closed when the OS cannot establish process identity.
            return true;
        }
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

        var watchdog = request.Policy?.WorkerWatchdogMs is { } watchdogMs
            ? TimeSpan.FromMilliseconds(watchdogMs)
            : (TimeSpan?)null;

        var result = await managed.Supervisor
            .ExecuteAsync(
                request,
                watchdog,
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

    private ManagedTarget ResolveKnownTarget(TargetRef targetRef)
    {
        if (string.IsNullOrWhiteSpace(targetRef.Id))
            throw new InvalidOperationException("Target id is required.");

        if (!_targets.TryGetValue(targetRef.Id, out var descriptor) ||
            !_supervisors.TryGetValue(targetRef.Id, out var supervisor))
        {
            throw new InvalidOperationException(
                $"Target '{targetRef.Id}' is not known to this runtime. Break-glass recovery never performs fresh host discovery.");
        }

        if (!string.Equals(
                descriptor.Identity.Host,
                targetRef.Host,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Target host does not match the registered target identity.");
        }

        return new ManagedTarget(descriptor, supervisor);
    }

    private TargetState GetKnownState(string targetId) =>
        _supervisors.TryGetValue(targetId, out var supervisor)
            ? supervisor.State.State
            : TargetState.Unavailable;

    internal OperationResult MaterializeLargeHostResult(
        OperationResult result,
        MutationClass mutationClass) =>
        MaterializeLargeOperationResult(result, mutationClass);

    private OperationResult MaterializeLargeOperationResult(
        OperationResult result,
        MutationClass mutationClass)
    {
        if (!result.Ok ||
            result.Result?.Value is not { } value ||
            IsArtifactEnvelope(result.Result))
        {
            return result;
        }

        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                value,
                RuntimePayloadJson);

            if (payload.LongLength <
                ArtifactResultOffload.DefaultThresholdByteCount)
            {
                return result;
            }

            var artifactBytes =
                result.Result.Kind == "string" &&
                value.ValueKind == JsonValueKind.String
                    ? System.Text.Encoding.UTF8.GetBytes(
                        value.GetString() ?? string.Empty)
                    : payload;
            var mediaType =
                ArtifactResultOffload.MediaTypeFor(result.Result);

            using var content = new MemoryStream(
                artifactBytes,
                writable: false);
            var descriptor = _artifactStore.Put(
                new ArtifactWriteRequest
                {
                    Content = content,
                    MediaType = mediaType,
                    Encoding = ArtifactMediaTypes.Utf8,
                    TimeToLive =
                        mutationClass == MutationClass.ReadOnly
                            ? null
                            : TimeSpan.FromDays(7)
                });
            NoteArtifactWrite();

            var offloadEvidence = new EvidenceItem(
                "artifact.offload",
                JsonSerializer.SerializeToElement(
                    new
                    {
                        descriptor.ArtifactId,
                        descriptor.Sha256,
                        descriptor.ByteCount,
                        descriptor.ExpiresAt,
                        descriptor.MediaType,
                        originalResultKind = result.Result.Kind
                    },
                    RuntimePayloadJson));

            return result with
            {
                Result = ArtifactResultOffload.Envelope(
                    descriptor,
                    result.Result.Kind),
                Evidence = AppendEvidence(
                    result.Evidence,
                    offloadEvidence)
            };
        }
        catch (ArtifactStoreException ex)
        {
            var tooLarge =
                ex.Kind == "artifact_too_large";
            return result with
            {
                Ok = false,
                Status = OperationStatus.Failed,
                Result = null,
                Error = new ProtocolError
                {
                    Kind = tooLarge
                        ? "artifact_result_too_large"
                        : "artifact_offload_failed",
                    Message = ex.Message,
                    Retryable =
                        !tooLarge &&
                        mutationClass == MutationClass.ReadOnly,
                    Execution = ExecutionState.Completed,
                    SuggestedActions = tooLarge
                        ?
                        [
                            "request_bounded_result",
                            "use_operation_specific_pagination"
                        ]
                        :
                        [
                            "repair_runtime_state_storage",
                            "retry_read_only_operation_if_safe"
                        ]
                }
            };
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                JsonException)
        {
            return result with
            {
                Ok = false,
                Status = OperationStatus.Failed,
                Result = null,
                Error = new ProtocolError
                {
                    Kind = "artifact_offload_failed",
                    Message =
                        $"The operation completed, but its large result " +
                        $"could not be materialized into the runtime artifact " +
                        $"store: {ex.Message}",
                    Retryable =
                        mutationClass == MutationClass.ReadOnly,
                    Execution = ExecutionState.Completed,
                    SuggestedActions =
                    [
                        "repair_runtime_state_storage",
                        "retry_read_only_operation_if_safe"
                    ]
                }
            };
        }
    }

    private void NoteArtifactWrite()
    {
        if (Interlocked.Increment(
                ref _artifactWritesSinceSweep) <
            ArtifactSweepWriteInterval)
        {
            return;
        }

        Interlocked.Exchange(
            ref _artifactWritesSinceSweep,
            0);
        TrySweepArtifacts();
    }

    private void TrySweepArtifacts()
    {
        if (Interlocked.CompareExchange(
                ref _artifactSweepGate,
                1,
                0) != 0)
        {
            return;
        }

        try
        {
            var report = _artifactStore.Sweep();
            _lastArtifactSweep = report;
            _lastArtifactSweepAtUtc =
                DateTimeOffset.UtcNow;
            _lastArtifactSweepError = null;
        }
        catch (Exception ex) when (
            ex is ArtifactStoreException or
                IOException or
                UnauthorizedAccessException)
        {
            _lastArtifactSweepAtUtc =
                DateTimeOffset.UtcNow;
            _lastArtifactSweepError =
                $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            Volatile.Write(
                ref _artifactSweepGate,
                0);
        }
    }

    private static bool IsArtifactEnvelope(ProtocolValue result)
    {
        if (!string.Equals(
                result.Kind,
                "object",
                StringComparison.Ordinal) ||
            result.Value is not { ValueKind: JsonValueKind.Object } value)
        {
            return false;
        }

        return value.TryGetProperty(
                   "offloaded",
                   out var offloaded) &&
               offloaded.ValueKind == JsonValueKind.True &&
               value.TryGetProperty(
                   "artifact",
                   out var artifact) &&
               artifact.ValueKind == JsonValueKind.Object;
    }

    private static IReadOnlyList<EvidenceItem> AppendEvidence(
        IReadOnlyList<EvidenceItem>? existing,
        EvidenceItem item) =>
        existing is null || existing.Count == 0
            ? [item]
            : existing.Concat([item]).ToArray();

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

    private static object DescribeOperation(
        OperationDefinition operation) =>
        new
        {
            name = operation.Name,
            version = operation.Version,
            mutationClass = operation.MutationClass,
            requiresTarget = operation.RequiresTarget,
            executionScope =
                operation.Scope == OperationExecutionScope.Runtime
                    ? "runtime"
                    : "host",
            host = operation.Host,
            requiresLease = operation.RequiresLease,
            mutationResolution =
                operation.MutationResolution == MutationResolutionMode.Fixed
                    ? "fixed"
                    : "declared_or_unknown",
            policyLimits = new
            {
                workerWatchdogMs = new
                {
                    min = OperationPolicy.MinWorkerWatchdogMs,
                    max = OperationPolicy.MaxWorkerWatchdogMs
                },
                retryBudgetMs = new
                {
                    min = OperationPolicy.MinRetryBudgetMs,
                    max = OperationPolicy.MaxRetryBudgetMs,
                    @default = OperationPolicy.DefaultRetryBudgetMs
                }
            }
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

    private sealed record OfflineIncidentResolutionInput(
        string TargetId,
        string IncidentRequestId,
        DateTimeOffset ExpectedUpdatedAt,
        bool Changed,
        string Rationale,
        JsonElement? Evidence);

    private sealed record HostTerminationInput(
        int ExpectedProcessId,
        DateTimeOffset ExpectedProcessStartedAt,
        int WaitTimeoutMs);

    private sealed record MutationReconciliationInput(
        string IncidentRequestId,
        long ExpectedRevision);

    private sealed record ManagedTarget(
        HostTargetDescriptor Descriptor,
        TargetSupervisor Supervisor);
}
