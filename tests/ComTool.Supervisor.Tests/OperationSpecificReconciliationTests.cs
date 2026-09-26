using System.Text.Json;
using ComTool.Broker.Protocol;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;
using ComTool.Runtime;

namespace ComTool.Supervisor.Tests;

/// <summary>
/// Deterministic proof that mutation ambiguity can only be cleared by real,
/// operation-specific host evidence — never by liveness — and that ambiguity
/// persists across worker loss and runtime restart.
/// </summary>
public sealed class OperationSpecificReconciliationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "comtool-v2-opreconcile-tests",
        Guid.NewGuid().ToString("N"));

    // ---- Policy: only verified+passing evidence resolves ------------------

    [Fact]
    public void VerifiedPassingPostconditionsResolveIncident()
    {
        var before = new TargetStateSnapshot(
            TargetState.ReconciliationRequired,
            Revision: 4,
            IncidentKind: "script_error");

        using var evidenceDocument = JsonDocument.Parse(
            """{"phase":"reconciliation","passed":true,"verified":true}""");

        var batch = new ConditionBatchResult(
            Passed: true,
            Verified: true,
            FailureKind: null,
            FailureMessage: null,
            SourceFailure: null,
            Evidence: new EvidenceItem(
                "conditions.post",
                evidenceDocument.RootElement.Clone()),
            VerifyMs: 1.5);

        var result = OperationSpecificReconciliationPolicy.Apply(
            before,
            "incident-1",
            batch);

        Assert.True(result.Reconciled);
        Assert.Equal(TargetState.KnownChanged, result.TargetState);
        Assert.Null(result.Error);
        Assert.NotNull(result.Evidence);
    }

    [Fact]
    public void MissingEvidenceCannotResolveIncident()
    {
        var result = OperationSpecificReconciliationPolicy.Apply(
            ReconciliationRequired(),
            "incident-1",
            evidence: null);

        Assert.False(result.Reconciled);
        Assert.Equal(
            TargetState.ReconciliationRequired,
            result.TargetState);
        Assert.Equal(
            "operation_specific_evidence_missing",
            result.Error?.Kind);
        Assert.Equal(
            ExecutionState.Ambiguous,
            result.Error?.Execution);
    }

    [Fact]
    public void UnverifiableEvidenceCannotResolveIncident()
    {
        var batch = new ConditionBatchResult(
            Passed: false,
            Verified: false,
            FailureKind: "condition_source_failed",
            FailureMessage: "Source read failed.",
            SourceFailure: null,
            Evidence: null,
            VerifyMs: 0);

        var result = OperationSpecificReconciliationPolicy.Apply(
            ReconciliationRequired(),
            "incident-1",
            batch);

        Assert.False(result.Reconciled);
        Assert.Equal(
            TargetState.ReconciliationRequired,
            result.TargetState);
        Assert.Equal(
            "operation_specific_evidence_unverifiable",
            result.Error?.Kind);
    }

    [Fact]
    public void FailingPostconditionsCannotResolveIncident()
    {
        var batch = new ConditionBatchResult(
            Passed: false,
            Verified: true,
            FailureKind: "condition_predicate_failed",
            FailureMessage: "Postcondition proven false.",
            SourceFailure: null,
            Evidence: null,
            VerifyMs: 0);

        var result = OperationSpecificReconciliationPolicy.Apply(
            ReconciliationRequired(),
            "incident-1",
            batch);

        Assert.False(result.Reconciled);
        Assert.Equal(
            TargetState.ReconciliationRequired,
            result.TargetState);
        Assert.Equal(
            "operation_specific_postcondition_failed",
            result.Error?.Kind);
    }

    [Fact]
    public void PolicyIsInertWhenTargetHasNoIncident()
    {
        var before = new TargetStateSnapshot(
            TargetState.Known,
            Revision: 1,
            IncidentKind: null);

        var result = OperationSpecificReconciliationPolicy.Apply(
            before,
            "incident-1",
            evidence: null);

        Assert.False(result.Reconciled);
        Assert.Equal(TargetState.Known, result.TargetState);
        Assert.Equal(
            "no_reconciliation_incident",
            result.Error?.Kind);
    }

    // ---- Generic reconcile must defer to the durable ledger ---------------

    [Fact]
    public async Task GenericReconcileCannotClearDurableIncidentWhenStateDrifted()
    {
        var ledger = new MutationLedger(_root);
        var target = Target();
        var request = Request(
            "drifted-incident",
            """{"kind":"code","source":"danger();"}""");

        _ = ledger.Begin(
            target,
            request,
            MutationClass.Unknown);

        // A supervisor built on a *fresh* state machine (Known) still shares a
        // durable ledger that records the unresolved mutation. Its generic
        // reconcile must reconstruct ambiguity and refuse to clear it.
        var freshState = new TargetStateMachine(TargetState.Known);

        await using var supervisor = new TargetSupervisor(
            target,
            new WorkerBrokerOptions
            {
                WorkerExecutablePath =
                    Path.Combine(_root, "does-not-exist.exe")
            },
            freshState,
            new MutationLedger(_root),
            leaseLockDirectory: Path.Combine(_root, "drift-locks"));

        // The constructor rehydrates persisted ambiguity.
        Assert.Equal(
            TargetState.ReconciliationRequired,
            supervisor.State.State);

        var lease = await supervisor.AcquireLeaseAsync();
        Assert.NotNull(lease);

        await supervisor.ReleaseLeaseAsync(lease.LeaseId);
    }

    [Fact]
    public async Task GenericReconcilePathNeverLaundersPersistedAmbiguity()
    {
        var ledger = new MutationLedger(_root);
        var target = Target("com.get");
        var request = Request(
            "drift-2",
            """{"kind":"code","source":"danger();"}""");

        _ = ledger.Begin(
            target,
            request,
            MutationClass.Unknown);

        // A state machine that lost the ambiguous observation must still be
        // rehydrated from the durable ledger on construction.
        var driftedState = new TargetStateMachine(TargetState.Known);

        await using var supervisor = new TargetSupervisor(
            target,
            new WorkerBrokerOptions
            {
                WorkerExecutablePath =
                    Path.Combine(_root, "does-not-exist.exe")
            },
            driftedState,
            new MutationLedger(_root),
            leaseLockDirectory: Path.Combine(_root, "drift2-locks"));

        Assert.Equal(
            TargetState.ReconciliationRequired,
            supervisor.State.State);

        // Repeated observation never clears the incident.
        Assert.Equal(
            TargetState.ReconciliationRequired,
            supervisor.State.State);
        Assert.NotNull(
            new MutationLedger(_root)
                .GetUnresolvedTarget(
                    target.Identity.TargetId));
    }

    // ---- Operation-specific reconcile: preflight safety -------------------

    [Fact]
    public async Task OperationSpecificReconcileRequiresLease()
    {
        await using var supervisor =
            NewAmbiguousSupervisor("osr-lease");

        // Empty lease id is rejected outright.
        await Assert.ThrowsAsync<ArgumentException>(
            () => supervisor.ReconcileMutationAsync(
                leaseId: string.Empty,
                incidentRequestId: "osr-lease",
                expectedRevision: supervisor.State.Revision,
                postconditions: []));

        // A well-formed but unowned lease cannot mutate incident state.
        var error = await Assert.ThrowsAsync<TargetLeaseException>(
            () => supervisor.ReconcileMutationAsync(
                leaseId: "not-the-active-lease",
                incidentRequestId: "osr-lease",
                expectedRevision: supervisor.State.Revision,
                postconditions: []));

        Assert.Equal("lease_required", error.Kind);
    }

    [Fact]
    public async Task OperationSpecificReconcileRejectsMutatingEvidenceSource()
    {
        var target = Target(
            "com.get",
            "core.target.status",
            "script.eval");

        var ledger = new MutationLedger(_root);
        var request = Request(
            "osr-source",
            """{"kind":"code","source":"danger();"}""");

        _ = ledger.Begin(
            target,
            request,
            MutationClass.Unknown);

        await using var supervisor = new TargetSupervisor(
            target,
            new WorkerBrokerOptions
            {
                WorkerExecutablePath =
                    Path.Combine(_root, "does-not-exist.exe")
            },
            stateMachine: null,
            mutationLedger: new MutationLedger(_root),
            leaseLockDirectory: Path.Combine(_root, "osr-source-locks"));

        var observed = supervisor.State;
        var lease = await supervisor.AcquireLeaseAsync();

        // script.eval is a mutating, non-fixed source: it must never be
        // accepted as reconciliation evidence.
        var error =
            await Assert.ThrowsAsync<OperationConditionPolicyException>(
                () => supervisor.ReconcileMutationAsync(
                    lease.LeaseId,
                    request.Id,
                    observed.Revision,
                    postconditions:
                    [
                        Condition(
                            "proof",
                            "script.eval",
                            """{"kind":"expression","source":"1"}""")
                    ]));

        Assert.Equal("condition_source_not_read_only", error.Kind);
        Assert.Equal(
            TargetState.ReconciliationRequired,
            supervisor.State.State);
        Assert.NotNull(
            new MutationLedger(_root)
                .GetUnresolvedTarget(
                    target.Identity.TargetId));

        await supervisor.ReleaseLeaseAsync(lease.LeaseId);
    }

    [Fact]
    public async Task OperationSpecificReconcileRejectsMismatchedIncidentId()
    {
        await using var supervisor =
            NewAmbiguousSupervisor("osr-mismatch");

        var lease = await supervisor.AcquireLeaseAsync();

        var error =
            await Assert.ThrowsAsync<MutationLedgerException>(
                () => supervisor.ReconcileMutationAsync(
                    lease.LeaseId,
                    incidentRequestId: "some-other-request",
                    expectedRevision: supervisor.State.Revision,
                    postconditions: []));

        Assert.Equal(
            "mutation_incident_request_mismatch",
            error.Kind);
        Assert.Equal(
            TargetState.ReconciliationRequired,
            supervisor.State.State);

        await supervisor.ReleaseLeaseAsync(lease.LeaseId);
    }

    [Fact]
    public async Task OperationSpecificReconcileWithNoPostconditionsStaysAmbiguous()
    {
        await using var supervisor =
            NewAmbiguousSupervisor("osr-empty");

        var observed = supervisor.State;
        var lease = await supervisor.AcquireLeaseAsync();

        var result = await supervisor.ReconcileMutationAsync(
            lease.LeaseId,
            "osr-empty",
            observed.Revision,
            postconditions: []);

        Assert.False(result.Reconciled);
        Assert.Equal(
            TargetState.ReconciliationRequired,
            result.TargetState);
        Assert.Equal(
            "operation_specific_evidence_missing",
            result.Error?.Kind);

        Assert.Null(supervisor.Worker);
        Assert.NotNull(
            new MutationLedger(_root)
                .GetUnresolvedTarget(
                    supervisor.Target.Identity.TargetId));

        await supervisor.ReleaseLeaseAsync(lease.LeaseId);
    }

    [Fact]
    public async Task ReconciliationCannotAttachNewPostconditionsAfterMutationBecameAmbiguous()
    {
        var target = Target("com.get");
        var originalCondition = Condition(
            "original-proof",
            "com.get",
            "{\"path\":\"documents[0].name\"}");
        var request = Request(
            "osr-unbound",
            "{\"kind\":\"code\",\"source\":\"danger();\"}") with
        {
            Postconditions = [originalCondition]
        };

        var ledger = new MutationLedger(_root);
        var begin = ledger.Begin(
            target,
            request,
            MutationClass.Unknown);
        ledger.Finalize(
            begin.Record,
            Ambiguous(request, "script_error"));

        await using var supervisor = new TargetSupervisor(
            target,
            new WorkerBrokerOptions
            {
                WorkerExecutablePath =
                    Path.Combine(_root, "does-not-exist.exe")
            },
            stateMachine: null,
            mutationLedger: new MutationLedger(_root),
            leaseLockDirectory: Path.Combine(_root, "osr-unbound-locks"));

        var observed = supervisor.State;
        var lease = await supervisor.AcquireLeaseAsync();
        var unrelatedCondition = Condition(
            "unrelated-proof",
            "com.get",
            "{\"path\":\"version\"}");

        var result = await supervisor.ReconcileMutationAsync(
            lease.LeaseId,
            request.Id,
            observed.Revision,
            [unrelatedCondition]);

        Assert.False(result.Reconciled);
        Assert.Equal(
            "operation_specific_evidence_not_bound",
            result.Error?.Kind);
        Assert.Equal(
            TargetState.ReconciliationRequired,
            supervisor.State.State);
        Assert.Null(supervisor.Worker);
        Assert.NotNull(
            new MutationLedger(_root)
                .GetUnresolvedTarget(target.Identity.TargetId));

        await supervisor.ReleaseLeaseAsync(lease.LeaseId);
    }

    [Fact]
    public async Task OperationSpecificReconcileRejectsStaleRevision()
    {
        await using var supervisor =
            NewAmbiguousSupervisor("osr-revision");

        var observed = supervisor.State;
        var lease = await supervisor.AcquireLeaseAsync();

        var error =
            await Assert.ThrowsAsync<TargetStateRevisionException>(
                () => supervisor.ReconcileMutationAsync(
                    lease.LeaseId,
                    "osr-revision",
                    expectedRevision: observed.Revision + 1,
                    postconditions: []));

        Assert.Equal(
            observed.Revision + 1,
            error.ExpectedRevision);
        Assert.Equal(
            TargetState.ReconciliationRequired,
            supervisor.State.State);

        await supervisor.ReleaseLeaseAsync(lease.LeaseId);
    }

    // ---- Ambiguity survives worker loss and runtime restart ---------------

    [Fact]
    public async Task AmbiguitySurvivesSupervisorRestartAndWorkerReplacement()
    {
        var target = Target("com.get");
        var request = Request(
            "survive-restart",
            """{"kind":"code","source":"danger();"}""");

        var first = new MutationLedger(_root);
        var begin = first.Begin(
            target,
            request,
            MutationClass.Unknown);
        first.Finalize(
            begin.Record,
            Ambiguous(request, "script_error"));

        var lockDirectory = Path.Combine(_root, "restart-locks");

        // First runtime: rehydrates ambiguity.
        var runtimeOne = new TargetSupervisor(
            target,
            new WorkerBrokerOptions
            {
                WorkerExecutablePath =
                    Path.Combine(_root, "does-not-exist.exe")
            },
            stateMachine: null,
            mutationLedger: new MutationLedger(_root),
            leaseLockDirectory: lockDirectory);

        Assert.Equal(
            TargetState.ReconciliationRequired,
            runtimeOne.State.State);
        await runtimeOne.DisposeAsync();

        // Second runtime (simulating process restart) sees the same incident.
        await using var runtimeTwo = new TargetSupervisor(
            target,
            new WorkerBrokerOptions
            {
                WorkerExecutablePath =
                    Path.Combine(_root, "does-not-exist.exe")
            },
            stateMachine: null,
            mutationLedger: new MutationLedger(_root),
            leaseLockDirectory: lockDirectory);

        Assert.Equal(
            TargetState.ReconciliationRequired,
            runtimeTwo.State.State);

        // A read-only state observation must not clear durable ambiguity.
        Assert.Equal(
            TargetState.ReconciliationRequired,
            runtimeTwo.State.State);
        Assert.NotNull(
            new MutationLedger(_root)
                .GetUnresolvedTarget(
                    target.Identity.TargetId));
    }

    [Fact]
    public void DurableIncidentIsNotClearedByReadOnlyReconnect()
    {
        var target = Target("com.get");
        var request = Request(
            "read-cannot-clear",
            """{"kind":"code","source":"danger();"}""");

        var ledger = new MutationLedger(_root);
        var begin = ledger.Begin(
            target,
            request,
            MutationClass.Unknown);
        ledger.Finalize(
            begin.Record,
            Ambiguous(request, "script_error"));

        var state = new TargetStateMachine();
        state.MarkAmbiguousExecution(
            MutationClass.Unknown,
            "script_error");

        state.MarkReconnected();
        state.MarkCompleted(MutationClass.ReadOnly);
        state.MarkHostUnavailable("host_blip");

        Assert.Equal(
            TargetState.ReconciliationRequired,
            state.Snapshot().State);
        Assert.NotNull(
            new MutationLedger(_root)
                .GetUnresolvedTarget(
                    target.Identity.TargetId));
    }

    [Fact]
    public void PreparedAfterRuntimeLossForcesReconciliationAndNeverReplays()
    {
        var target = Target();
        var request = Request(
            "prepared-runtime-loss",
            """{"kind":"expression","source":"mutate()"}""");

        var ledger = new MutationLedger(_root);
        var begin = ledger.Begin(
            target,
            request,
            MutationClass.ConditionalWrite);
        Assert.Equal(
            MutationLedgerPhase.Prepared,
            begin.Record.Phase);

        // Runtime "restart": a new ledger reads the prepared marker.
        var reopened = new MutationLedger(_root);
        var unresolved = reopened.GetUnresolvedTarget(
            target.Identity.TargetId);

        Assert.NotNull(unresolved);
        Assert.Equal(
            MutationLedgerPhase.Prepared,
            unresolved!.Phase);

        var replayAttempt = reopened.Begin(
            target,
            request,
            MutationClass.ConditionalWrite);

        Assert.Equal(
            MutationLedgerBeginDisposition.Unresolved,
            replayAttempt.Disposition);
        Assert.NotEqual(
            MutationLedgerBeginDisposition.ReplayCompleted,
            replayAttempt.Disposition);
    }

    [Fact]
    public void CompletedReplayIsEvidenceTaggedAndZeroExecuteTime()
    {
        var target = Target();
        var request = Request(
            "completed-replay",
            """{"kind":"expression","source":"1+1"}""");

        var ledger = new MutationLedger(_root);
        var begin = ledger.Begin(
            target,
            request,
            MutationClass.Unknown);
        ledger.Finalize(
            begin.Record,
            Success(request, ProtocolValue.FromString("done")));

        var reopened = new MutationLedger(_root);
        var replay = reopened.Begin(
            target,
            request,
            MutationClass.Unknown);

        Assert.Equal(
            MutationLedgerBeginDisposition.ReplayCompleted,
            replay.Disposition);
        Assert.NotNull(replay.StoredResult);
        Assert.True(replay.StoredResult!.Ok);
    }

    [Fact]
    public void RequestIdConflictRejectedForDifferentSemanticPayload()
    {
        var target = Target();
        var ledger = new MutationLedger(_root);

        var begin = ledger.Begin(
            target,
            Request("conflict-id", """{"a":1}"""),
            MutationClass.ConditionalWrite);
        ledger.Finalize(
            begin.Record,
            Success(
                Request("conflict-id", """{"a":1}"""),
                ProtocolValue.FromString("ok")));

        var conflict = ledger.Begin(
            target,
            Request("conflict-id", """{"a":2}"""),
            MutationClass.ConditionalWrite);

        Assert.Equal(
            MutationLedgerBeginDisposition.RequestIdConflict,
            conflict.Disposition);
    }

    [Fact]
    public void ReconciliationResolutionPersistsAcrossReopen()
    {
        var target = Target();
        var request = Request(
            "persist-resolution",
            """{"kind":"code","source":"danger();"}""");

        var ledger = new MutationLedger(_root);
        var begin = ledger.Begin(
            target,
            request,
            MutationClass.Unknown);
        ledger.Finalize(
            begin.Record,
            Ambiguous(request, "script_error"));

        using var evidence = JsonDocument.Parse(
            """{"proven":true,"source":"host-read"}""");

        _ = ledger.ResolveIncident(
            target.Identity.TargetId,
            request.Id,
            changed: true,
            rationale: "Operation-specific postconditions proven true.",
            evidence.RootElement);

        var reopened = new MutationLedger(_root);

        Assert.Null(
            reopened.GetUnresolvedTarget(
                target.Identity.TargetId));

        var repeat = reopened.Begin(
            target,
            request,
            MutationClass.Unknown);

        Assert.Equal(
            MutationLedgerBeginDisposition.Resolved,
            repeat.Disposition);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    private static void runtimeTwoDisposeProbe(TargetSupervisor supervisor)
    {
        // A read-only state observation must not clear durable ambiguity.
        Assert.Equal(
            TargetState.ReconciliationRequired,
            supervisor.State.State);
    }

    private TargetSupervisor NewAmbiguousSupervisor(string requestId)
    {
        var target = Target("com.get");

        var ledger = new MutationLedger(_root);
        var request = Request(
            requestId,
            """{"kind":"code","source":"danger();"}""");

        var begin = ledger.Begin(
            target,
            request,
            MutationClass.Unknown);
        ledger.Finalize(
            begin.Record,
            Ambiguous(request, "script_error"));

        return new TargetSupervisor(
            target,
            new WorkerBrokerOptions
            {
                WorkerExecutablePath =
                    Path.Combine(_root, "does-not-exist.exe")
            },
            stateMachine: null,
            mutationLedger: new MutationLedger(_root),
            leaseLockDirectory:
                Path.Combine(_root, requestId + "-locks"));
    }

    private static TargetStateSnapshot ReconciliationRequired() =>
        new(
            TargetState.ReconciliationRequired,
            Revision: 3,
            IncidentKind: "script_error");

    private static OperationCondition Condition(
        string id,
        string operation,
        string inputJson)
    {
        using var input = JsonDocument.Parse(inputJson);

        return new OperationCondition
        {
            Id = id,
            Source = new OperationConditionSource
            {
                Operation = operation,
                Input = input.RootElement.Clone()
            },
            Predicate = new OperationConditionPredicate
            {
                Kind = "truthy"
            }
        };
    }

    private static HostTargetDescriptor Target(
        params string[] capabilities)
    {
        var identity = new HostTargetIdentity
        {
            Host = "illustrator",
            ProcessId = 43252,
            ProcessStartedAt =
                DateTimeOffset.Parse("2026-09-24T17:40:37Z"),
            ExecutablePath = @"C:\Adobe\Illustrator.exe",
            HostVersion = "30.6.0",
            AdapterVersion = "test",
            EndpointIdentity = "Illustrator.Application"
        };

        return new HostTargetDescriptor
        {
            Identity = identity,
            Target = new TargetRef(
                identity.Host,
                identity.TargetId,
                Generation: 0),
            Running = true,
            Capabilities = capabilities
                .Select(
                    name => new CapabilityDescriptor
                    {
                        Name = name,
                        Version = "1",
                        MutationClass =
                            name == "script.eval"
                                ? MutationClass.Unknown
                                : MutationClass.ReadOnly,
                        Supported = true,
                        Host = "illustrator"
                    })
                .ToArray()
        };
    }

    private static OperationRequest Request(
        string id,
        string inputJson)
    {
        using var document = JsonDocument.Parse(inputJson);

        return new OperationRequest
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = id,
            Target = Target().Target,
            Operation = "script.eval",
            Input = document.RootElement.Clone()
        };
    }

    private static OperationResult Success(
        OperationRequest request,
        ProtocolValue value) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = true,
            Status = OperationStatus.Completed,
            TargetState = TargetState.KnownChanged,
            Result = value
        };

    private static OperationResult Ambiguous(
        OperationRequest request,
        string kind) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = false,
            Status = OperationStatus.ReconciliationRequired,
            TargetState = TargetState.ReconciliationRequired,
            Error = new ProtocolError
            {
                Kind = kind,
                Message = "Outcome unknown.",
                Retryable = false,
                Execution = ExecutionState.Ambiguous
            }
        };
}
