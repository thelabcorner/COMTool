using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Supervisor;

public sealed record HostLaunchDecision(
    HostLaunchObservation Observation,
    HostLaunchAttemptRecord Attempt,
    HostOwnershipRecord? Ownership);

/// <summary>
/// Runtime-owned host lifecycle authority for explicit launch requests.
/// Discovery, launch transport, OS generation proof, and durable provenance are
/// injected so policy can be tested without starting an Adobe process.
/// </summary>
public sealed class HostLaunchRuntime
{
    private readonly IReadOnlyCollection<string> _configuredHosts;
    private readonly IHostTargetDiscovery _discovery;
    private readonly Func<string, IHostLaunchExecutor?> _executorForHost;
    private readonly HostOwnershipLedger _ledger;
    private readonly IHostProcessIdentityProbe _processProbe;
    private readonly string _runtimeId;
    private readonly Func<DateTimeOffset> _clock;

    public HostLaunchRuntime(
        IReadOnlyCollection<string> configuredHosts,
        IHostTargetDiscovery discovery,
        Func<string, IHostLaunchExecutor?> executorForHost,
        HostOwnershipLedger ledger,
        IHostProcessIdentityProbe processProbe,
        string runtimeId,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(configuredHosts);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(executorForHost);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(processProbe);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeId);

        _configuredHosts = configuredHosts;
        _discovery = discovery;
        _executorForHost = executorForHost;
        _ledger = ledger;
        _processProbe = processProbe;
        _runtimeId = runtimeId;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    public async ValueTask<HostLaunchDecision> LaunchAsync(
        string launchRequestId,
        HostLaunchSpec spec,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launchRequestId);
        ArgumentNullException.ThrowIfNull(spec);

        HostLaunchAttemptRecord? priorAttempt;
        try
        {
            priorAttempt = _ledger.TryGetAttempt(launchRequestId);
        }
        catch (HostOwnershipLedgerException ex)
        {
            throw OwnershipFailure(ex, dispatched: false);
        }

        if (priorAttempt is not null)
        {
            throw new HostLaunchException(
                "launch_request_id_reused",
                $"Launch request id '{launchRequestId}' already has durable " +
                "provenance and cannot be reused.",
                retryable: false,
                ExecutionState.NotStarted);
        }

        var validation = HostLaunchSpecValidator.Validate(
            spec,
            _configuredHosts);
        if (validation is not null)
        {
            try
            {
                _ = RecordAttempt(
                    launchRequestId,
                    spec,
                    _clock(),
                    HostLaunchAttemptOutcome.Refused,
                    targetId: null,
                    ambiguityKind: validation.Kind,
                    note: validation.Message);
            }
            catch (HostOwnershipLedgerException ex)
            {
                throw OwnershipFailure(ex, dispatched: false);
            }

            throw new HostLaunchException(
                validation.Kind,
                validation.Message,
                retryable: false,
                ExecutionState.NotStarted);
        }

        var startedAt = _clock();
        var attemptRecorded = false;
        var dispatched = false;

        try
        {
            var existing = await _discovery
                .DiscoverAsync(spec.Host, cancellationToken)
                .ConfigureAwait(false);

            if (existing.Count > 0)
            {
                if (spec.ExistingInstance ==
                    HostLaunchExistingInstancePolicy.Fail)
                {
                    _ = RecordAttempt(
                        launchRequestId,
                        spec,
                        startedAt,
                        HostLaunchAttemptOutcome.Refused,
                        targetId: null,
                        ambiguityKind: "host_already_running",
                        note:
                            $"Observed {existing.Count} running target(s); " +
                            "existing-instance policy is fail.");
                    attemptRecorded = true;

                    throw new HostLaunchException(
                        "host_already_running",
                        $"Host family '{spec.Host}' already has " +
                        $"{existing.Count} running target(s).",
                        retryable: false,
                        ExecutionState.NotStarted);
                }

                if (existing.Count != 1)
                {
                    _ = RecordAttempt(
                        launchRequestId,
                        spec,
                        startedAt,
                        HostLaunchAttemptOutcome.Refused,
                        targetId: null,
                        ambiguityKind: "preexisting_target_ambiguous",
                        note:
                            $"Cannot explicitly bind {existing.Count} " +
                            "preexisting targets as one generation.");
                    attemptRecorded = true;

                    throw new HostLaunchException(
                        "preexisting_target_ambiguous",
                        $"Expected one preexisting target, found " +
                        $"{existing.Count}.",
                        retryable: false,
                        ExecutionState.NotStarted);
                }

                var identity = existing[0].Identity;
                if (spec.ExpectedHostVersion is { } expected &&
                    !string.Equals(
                        expected,
                        identity.HostVersion,
                        StringComparison.Ordinal))
                {
                    _ = RecordAttempt(
                        launchRequestId,
                        spec,
                        startedAt,
                        HostLaunchAttemptOutcome.Refused,
                        identity.TargetId,
                        "host_version_mismatch",
                        $"Expected {expected}; observed {identity.HostVersion}.");
                    attemptRecorded = true;

                    throw new HostLaunchException(
                        "host_version_mismatch",
                        $"Expected host version {expected}, observed " +
                        $"{identity.HostVersion}.",
                        retryable: false,
                        ExecutionState.NotStarted);
                }

                var observation = new HostLaunchObservation
                {
                    Host = spec.Host,
                    ProgId = spec.ProgId,
                    Ownership =
                        HostLaunchOwnership.PreexistingWithoutOwnership,
                    Identity = identity,
                    ObservedAt = _clock(),
                    OpenDocumentCount = -1
                };

                var attempt = RecordAttempt(
                    launchRequestId,
                    spec,
                    startedAt,
                    HostLaunchAttemptOutcome.PreexistingWithoutOwnership,
                    identity.TargetId,
                    ambiguityKind: null,
                    note:
                        "Returned the exact preexisting generation without " +
                        "creating ownership.");
                attemptRecorded = true;

                return new HostLaunchDecision(
                    observation,
                    attempt,
                    Ownership: null);
            }

            var executor = _executorForHost(spec.Host);
            if (executor is null)
            {
                _ = RecordAttempt(
                    launchRequestId,
                    spec,
                    startedAt,
                    HostLaunchAttemptOutcome.Refused,
                    targetId: null,
                    ambiguityKind: "host_launch_unsupported",
                    note:
                        $"No launch executor is registered for '{spec.Host}'.");
                attemptRecorded = true;

                throw new HostLaunchException(
                    "host_launch_unsupported",
                    $"Host family '{spec.Host}' has no launch executor.",
                    retryable: false,
                    ExecutionState.NotStarted);
            }

            dispatched = true;
            var observed = await executor
                .LaunchAsync(spec, cancellationToken)
                .ConfigureAwait(false);

            var normalized = await NormalizeObservationAsync(
                    spec,
                    observed,
                    cancellationToken)
                .ConfigureAwait(false);

            HostOwnershipRecord? ownership = null;
            HostLaunchAttemptOutcome outcome;

            if (normalized.Ownership ==
                HostLaunchOwnership.LaunchedByRuntime)
            {
                var identity = normalized.Identity
                    ?? throw new HostLaunchException(
                        "launch_identity_missing",
                        "A launched_by_runtime observation omitted strong " +
                        "target identity.",
                        retryable: false,
                        ExecutionState.Ambiguous);

                ownership = _ledger.RecordLaunched(
                    identity,
                    spec.ProgId,
                    spec.SpecKey,
                    _runtimeId,
                    launchRequestId,
                    normalized.ObservedAt,
                    note:
                        "Ownership granted only after OS process-generation " +
                        "revalidation.");

                outcome = HostLaunchAttemptOutcome.Launched;
            }
            else if (normalized.Ownership ==
                     HostLaunchOwnership.PreexistingWithoutOwnership)
            {
                outcome =
                    HostLaunchAttemptOutcome.PreexistingWithoutOwnership;
            }
            else
            {
                outcome = string.Equals(
                        normalized.AmbiguityKind,
                        "launch_timeout",
                        StringComparison.Ordinal)
                    ? HostLaunchAttemptOutcome.Timeout
                    : HostLaunchAttemptOutcome.Unproven;
            }

            var attemptRecord = RecordAttempt(
                launchRequestId,
                spec,
                startedAt,
                outcome,
                normalized.Identity?.TargetId,
                normalized.AmbiguityKind,
                normalized.AmbiguityMessage);
            attemptRecorded = true;

            return new HostLaunchDecision(
                normalized,
                attemptRecord,
                ownership);
        }
        catch (OperationCanceledException)
        {
            if (!attemptRecorded)
            {
                try
                {
                    _ = RecordAttempt(
                        launchRequestId,
                        spec,
                        startedAt,
                        HostLaunchAttemptOutcome.Cancelled,
                        targetId: null,
                        ambiguityKind:
                            dispatched ? "launch_cancelled_after_dispatch" : null,
                        note:
                            dispatched
                                ? "Cancellation occurred after the launch command " +
                                  "was dispatched; no ownership was granted."
                                : "Cancellation occurred before launch dispatch.");
                }
                catch (HostOwnershipLedgerException ex)
                {
                    throw OwnershipFailure(ex, dispatched);
                }
            }

            throw;
        }
        catch (HostLaunchException ex)
        {
            if (!attemptRecorded)
            {
                try
                {
                    _ = RecordAttempt(
                        launchRequestId,
                        spec,
                        startedAt,
                        ex.Execution == ExecutionState.NotStarted
                            ? HostLaunchAttemptOutcome.Refused
                            : HostLaunchAttemptOutcome.Unproven,
                        targetId: null,
                        ambiguityKind: ex.Kind,
                        note: ex.Message);
                }
                catch (HostOwnershipLedgerException ledgerEx)
                {
                    throw OwnershipFailure(
                        ledgerEx,
                        dispatched ||
                        ex.Execution != ExecutionState.NotStarted);
                }
            }

            throw;
        }
        catch (HostOwnershipLedgerException ex)
        {
            throw OwnershipFailure(ex, dispatched);
        }
        catch (Exception ex)
        {
            if (!attemptRecorded)
            {
                try
                {
                    _ = RecordAttempt(
                        launchRequestId,
                        spec,
                        startedAt,
                        dispatched
                            ? HostLaunchAttemptOutcome.Unproven
                            : HostLaunchAttemptOutcome.Refused,
                        targetId: null,
                        ambiguityKind: "host_launch_runtime_failure",
                        note: ex.Message);
                }
                catch (HostOwnershipLedgerException ledgerEx)
                {
                    throw OwnershipFailure(ledgerEx, dispatched);
                }
            }

            throw new HostLaunchException(
                "host_launch_runtime_failure",
                $"Host launch authority failed: {ex.Message}",
                retryable: false,
                dispatched
                    ? ExecutionState.Ambiguous
                    : ExecutionState.NotStarted,
                ex.HResult,
                ex);
        }
    }

    private static HostLaunchException OwnershipFailure(
        HostOwnershipLedgerException exception,
        bool dispatched) =>
        new(
            exception.Kind,
            $"Host launch provenance could not be read or updated: " +
            exception.Message,
            retryable: false,
            dispatched
                ? ExecutionState.Ambiguous
                : ExecutionState.NotStarted,
            exception.HResult,
            exception);

    private async ValueTask<HostLaunchObservation> NormalizeObservationAsync(
        HostLaunchSpec spec,
        HostLaunchObservation observation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (!string.Equals(
                observation.Host,
                spec.Host,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                observation.ProgId,
                spec.ProgId,
                StringComparison.Ordinal))
        {
            return observation with
            {
                Ownership = HostLaunchOwnership.Unproven,
                AmbiguityKind = "launch_observation_mismatch",
                AmbiguityMessage =
                    "Launch worker observation does not match the requested " +
                    "host family and ProgID."
            };
        }

        if (observation.Ownership !=
            HostLaunchOwnership.LaunchedByRuntime)
        {
            return observation;
        }

        if (observation.Identity is not { } identity)
        {
            return observation with
            {
                Ownership = HostLaunchOwnership.Unproven,
                AmbiguityKind = "launch_identity_missing",
                AmbiguityMessage =
                    "Launch worker claimed ownership without a strong target " +
                    "identity."
            };
        }

        if (!string.Equals(
                identity.Host,
                spec.Host,
                StringComparison.OrdinalIgnoreCase))
        {
            return observation with
            {
                Ownership = HostLaunchOwnership.Unproven,
                AmbiguityKind = "launch_identity_host_mismatch",
                AmbiguityMessage =
                    "Observed strong identity belongs to a different host " +
                    "family."
            };
        }

        if (spec.ExpectedHostVersion is { } expected &&
            !string.Equals(
                expected,
                identity.HostVersion,
                StringComparison.Ordinal))
        {
            return observation with
            {
                Ownership = HostLaunchOwnership.Unproven,
                AmbiguityKind = "host_version_mismatch",
                AmbiguityMessage =
                    $"Expected host version {expected}, observed " +
                    $"{identity.HostVersion}."
            };
        }

        var process = await _processProbe
            .TryReadAsync(identity.ProcessId, cancellationToken)
            .ConfigureAwait(false);

        if (process is null || !process.Matches(identity))
        {
            return observation with
            {
                Ownership = HostLaunchOwnership.Unproven,
                AmbiguityKind = "launch_generation_unproven",
                AmbiguityMessage =
                    "The OS no longer proves the exact PID/start-time/executable " +
                    "generation reported by the launch worker."
            };
        }

        return observation;
    }

    private HostLaunchAttemptRecord RecordAttempt(
        string requestId,
        HostLaunchSpec spec,
        DateTimeOffset startedAt,
        HostLaunchAttemptOutcome outcome,
        string? targetId,
        string? ambiguityKind,
        string? note)
    {
        var completedAt = _clock();
        var elapsed = Math.Max(
            0,
            (completedAt - startedAt).TotalMilliseconds);

        var attempt = new HostLaunchAttemptRecord
        {
            RecordFormat = HostLaunchAttemptRecord.Format,
            SchemaVersion =
                HostLaunchAttemptRecord.CurrentSchemaVersion,
            LaunchRequestId = requestId,
            Host = spec.Host,
            ProgId = spec.ProgId,
            LaunchSpecKey = spec.SpecKey,
            RuntimeId = _runtimeId,
            RuntimeProcessId = Environment.ProcessId,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            ElapsedMs = elapsed,
            Outcome = outcome,
            TargetId = targetId,
            AmbiguityKind = ambiguityKind,
            Note = note
        };

        _ledger.RecordAttempt(attempt);
        return attempt;
    }
}
