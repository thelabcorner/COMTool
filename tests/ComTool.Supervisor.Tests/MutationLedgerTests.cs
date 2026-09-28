using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;
using ComTool.Runtime;

namespace ComTool.Supervisor.Tests;

public sealed class MutationLedgerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "comtool-v2-ledger-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void PreparedMutationSurvivesLedgerReopen()
    {
        var ledger = new MutationLedger(_root);
        var target = Target();
        var request = Request("req-prepared", """{"kind":"code","source":"return 1;"}""");

        var begin = ledger.Begin(
            target,
            request,
            MutationClass.Unknown);

        Assert.Equal(
            MutationLedgerBeginDisposition.Proceed,
            begin.Disposition);
        Assert.Equal(
            MutationLedgerPhase.Prepared,
            begin.Record.Phase);

        var reopened = new MutationLedger(_root);
        var unresolved = reopened.GetUnresolvedTarget(
            target.Identity.TargetId);

        Assert.NotNull(unresolved);
        Assert.Equal("req-prepared", unresolved!.RequestId);
        Assert.Equal(
            MutationLedgerPhase.Prepared,
            unresolved.Phase);
    }

    [Fact]
    public void ListUnresolvedEnumeratesPreparedMutationWithoutLiveTargetDiscovery()
    {
        var ledger = new MutationLedger(_root);
        var target = Target();
        var request = Request(
            "req-list-unresolved",
            """{"kind":"code","source":"return 1;"}""");

        _ = ledger.Begin(
            target,
            request,
            MutationClass.Unknown);

        var reopened = new MutationLedger(_root);
        var incidents = reopened.ListUnresolved();

        var incident = Assert.Single(incidents);
        Assert.Equal(
            target.Identity.TargetId,
            incident.TargetId);
        Assert.Equal(
            request.Id,
            incident.RequestId);
        Assert.Equal(
            request.Operation,
            incident.Operation);
        Assert.Equal(
            MutationLedgerPhase.Prepared,
            incident.Phase);
    }

    [Fact]
    public void ListUnresolvedSurfacesCorruptActiveMarkerFailClosed()
    {
        var ledger = new MutationLedger(_root);
        var activeDirectory = Path.Combine(_root, "active");
        File.WriteAllText(
            Path.Combine(activeDirectory, "corrupt-marker.json"),
            "{broken");

        var incidents = ledger.ListUnresolved();

        var incident = Assert.Single(incidents);
        Assert.Equal(
            "mutation_ledger_corrupt",
            incident.IncidentKind);
        Assert.Equal(
            "corrupt-active-record",
            incident.RequestId);
        Assert.StartsWith(
            "corrupt-active:",
            incident.TargetId,
            StringComparison.Ordinal);
        Assert.Equal(
            MutationLedgerPhase.Ambiguous,
            incident.Phase);
    }

    [Fact]
    public void CompletedMutationReplaysStoredResultWithoutNewPrepare()
    {
        var ledger = new MutationLedger(_root);
        var target = Target();
        var request = Request("req-completed", """{"kind":"expression","source":"1+1"}""");

        var begin = ledger.Begin(
            target,
            request,
            MutationClass.Unknown);
        var completed = Success(
            request,
            ProtocolValue.FromString("done"));

        ledger.Finalize(begin.Record, completed);

        var reopened = new MutationLedger(_root);
        Assert.Null(
            reopened.GetUnresolvedTarget(
                target.Identity.TargetId));

        var replay = reopened.Begin(
            target,
            request,
            MutationClass.Unknown);

        Assert.Equal(
            MutationLedgerBeginDisposition.ReplayCompleted,
            replay.Disposition);
        Assert.NotNull(replay.StoredResult);
        Assert.True(replay.StoredResult!.Ok);
        Assert.Equal(
            "done",
            replay.StoredResult.Result?.Value?.GetString());
    }

    [Fact]
    public void RequestIdCannotBeReboundToDifferentSemanticIntent()
    {
        var ledger = new MutationLedger(_root);
        var target = Target();
        var first = Request("same-id", """{"a":1}""");
        var changed = Request("same-id", """{"a":2}""");

        var begin = ledger.Begin(
            target,
            first,
            MutationClass.ConditionalWrite);
        ledger.Finalize(
            begin.Record,
            Success(first, ProtocolValue.FromString("ok")));

        var conflict = ledger.Begin(
            target,
            changed,
            MutationClass.ConditionalWrite);

        Assert.Equal(
            MutationLedgerBeginDisposition.RequestIdConflict,
            conflict.Disposition);
    }

    [Fact]
    public void ActiveMutationBlocksDifferentMutationForSameTarget()
    {
        var ledger = new MutationLedger(_root);
        var target = Target();
        var first = Request("req-a", """{"value":1}""");
        var second = Request("req-b", """{"value":2}""");

        _ = ledger.Begin(
            target,
            first,
            MutationClass.NonIdempotentWrite);

        var blocked = ledger.Begin(
            target,
            second,
            MutationClass.NonIdempotentWrite);

        Assert.Equal(
            MutationLedgerBeginDisposition.TargetHasUnresolvedMutation,
            blocked.Disposition);
        Assert.Equal("req-a", blocked.Record.RequestId);
    }

    [Fact]
    public void AmbiguousTerminalResultRemainsActiveAcrossReopen()
    {
        var ledger = new MutationLedger(_root);
        var target = Target();
        var request = Request("req-ambiguous", """{"source":"danger()"}""");

        var begin = ledger.Begin(
            target,
            request,
            MutationClass.Unknown);

        ledger.Finalize(
            begin.Record,
            Ambiguous(request, "worker_watchdog_timeout"));

        var reopened = new MutationLedger(_root);
        var unresolved = reopened.GetUnresolvedTarget(
            target.Identity.TargetId);

        Assert.NotNull(unresolved);
        Assert.Equal(
            MutationLedgerPhase.Ambiguous,
            unresolved!.Phase);
        Assert.Equal(
            "worker_watchdog_timeout",
            unresolved.IncidentKind);

        var repeated = reopened.Begin(
            target,
            request,
            MutationClass.Unknown);

        Assert.Equal(
            MutationLedgerBeginDisposition.Unresolved,
            repeated.Disposition);
        Assert.Equal(
            ExecutionState.Ambiguous,
            repeated.StoredResult?.Error?.Execution);
    }

    [Fact]
    public void NotStartedTerminalResultAllowsSameIntentToRetry()
    {
        var ledger = new MutationLedger(_root);
        var target = Target();
        var request = Request("req-not-started", """{"value":42}""");

        var begin = ledger.Begin(
            target,
            request,
            MutationClass.IdempotentWrite);

        ledger.Finalize(
            begin.Record,
            NotStarted(request),
            executionWasDispatched: false);

        Assert.Null(
            ledger.GetUnresolvedTarget(
                target.Identity.TargetId));

        var retry = ledger.Begin(
            target,
            request,
            MutationClass.IdempotentWrite);

        Assert.Equal(
            MutationLedgerBeginDisposition.Proceed,
            retry.Disposition);
        Assert.Equal(
            MutationLedgerPhase.Prepared,
            retry.Record.Phase);
    }

    [Fact]
    public void DispatchedNotStartedClaimFailsClosedAsAmbiguous()
    {
        var ledger = new MutationLedger(_root);
        var target = Target();
        var request = Request(
            "req-dispatched-not-started",
            """{"value":42}""");

        var begin = ledger.Begin(
            target,
            request,
            MutationClass.NonIdempotentWrite);

        ledger.Finalize(
            begin.Record,
            NotStarted(request));

        var unresolved = ledger.GetUnresolvedTarget(
            target.Identity.TargetId);
        Assert.NotNull(unresolved);
        Assert.Equal(
            MutationLedgerPhase.Ambiguous,
            unresolved!.Phase);
    }

    [Fact]
    public void CurrentWorkerCertifiedNotStartedClearsPreparedMutation()
    {
        var ledger = new MutationLedger(_root);
        var target = Target();
        var request = Request(
            "req-certified-not-started",
            """{"value":42}""");

        var begin = ledger.Begin(
            target,
            request,
            MutationClass.ExternalSideEffect);

        ledger.Finalize(
            begin.Record,
            NotStarted(request),
            executionWasDispatched: true,
            notStartedCertifiedByCurrentWorker:
                true);

        Assert.Null(
            ledger.GetUnresolvedTarget(
                target.Identity.TargetId));

        var retry = ledger.Begin(
            target,
            request,
            MutationClass.ExternalSideEffect);
        Assert.Equal(
            MutationLedgerBeginDisposition.Proceed,
            retry.Disposition);
    }

    [Fact]
    public void FailedResultWithoutErrorFailsClosedAsAmbiguous()
    {
        var ledger = new MutationLedger(_root);
        var target = Target();
        var request = Request(
            "req-malformed-failure",
            """{"value":43}""");

        var begin = ledger.Begin(
            target,
            request,
            MutationClass.NonIdempotentWrite);

        ledger.Finalize(
            begin.Record,
            new OperationResult
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = request.Id,
                Operation = request.Operation,
                Ok = false,
                Status = OperationStatus.Failed,
                TargetState = TargetState.Known
            });

        var unresolved = ledger.GetUnresolvedTarget(
            target.Identity.TargetId);
        Assert.NotNull(unresolved);
        Assert.Equal(
            MutationLedgerPhase.Ambiguous,
            unresolved!.Phase);

        var retry = ledger.Begin(
            target,
            request,
            MutationClass.NonIdempotentWrite);
        Assert.Equal(
            MutationLedgerBeginDisposition.Unresolved,
            retry.Disposition);
    }

    [Fact]
    public void LateRuntimeInterruptedSignalCannotDowngradeCompletedResult()
    {
        var ledger = new MutationLedger(_root);
        var target = Target();
        var request = Request(
            "req-late-interrupt",
            """{"value":44}""");

        var begin = ledger.Begin(
            target,
            request,
            MutationClass.NonIdempotentWrite);
        ledger.Finalize(
            begin.Record,
            Success(
                request,
                ProtocolValue.FromString("committed")));

        ledger.MarkRuntimeInterrupted(
            begin.Record,
            "late_dispatch_interrupted");

        Assert.Null(
            ledger.GetUnresolvedTarget(
                target.Identity.TargetId));
        var replay = ledger.Begin(
            target,
            request,
            MutationClass.NonIdempotentWrite);
        Assert.Equal(
            MutationLedgerBeginDisposition.ReplayCompleted,
            replay.Disposition);
        Assert.Equal(
            "committed",
            replay.StoredResult?.Result?.Value?.GetString());
    }

    [Fact]
    public void ObjectPropertyOrderDoesNotChangeRequestFingerprint()
    {
        var ledger = new MutationLedger(_root);
        var target = Target();
        var first = Request("req-canonical", """{"b":2,"a":1}""");
        var reordered = Request("req-canonical", """{"a":1,"b":2}""");

        _ = ledger.Begin(
            target,
            first,
            MutationClass.Unknown);

        var repeated = ledger.Begin(
            target,
            reordered,
            MutationClass.Unknown);

        Assert.Equal(
            MutationLedgerBeginDisposition.Unresolved,
            repeated.Disposition);
    }

    [Fact]
    public void ActiveMarkerAloneFailsClosedAfterInterruptedDualWrite()
    {
        var ledger = new MutationLedger(_root);
        var target = Target();
        var request = Request("req-active-only", """{"x":true}""");

        _ = ledger.Begin(
            target,
            request,
            MutationClass.Unknown);

        var recordPath = Assert.Single(
            Directory.GetFiles(
                Path.Combine(_root, "records"),
                "*.json"));
        File.Delete(recordPath);

        var reopened = new MutationLedger(_root);
        var unresolved = reopened.GetUnresolvedTarget(
            target.Identity.TargetId);

        Assert.NotNull(unresolved);
        Assert.Equal(
            MutationLedgerPhase.Prepared,
            unresolved!.Phase);
        Assert.Equal("req-active-only", unresolved.RequestId);
    }

    [Fact]
    public async Task TargetSupervisorRecoversPreparedMutationAsReconciliationRequired()
    {
        var ledger = new MutationLedger(_root);
        var target = Target();
        var request = Request(
            "req-supervisor-recovery",
            """{"kind":"code","source":"return 1;"}""");

        _ = ledger.Begin(
            target,
            request,
            MutationClass.Unknown);

        var reopened = new MutationLedger(_root);
        await using var supervisor = new TargetSupervisor(
            target,
            new WorkerBrokerOptions
            {
                WorkerExecutablePath = "unused.exe"
            },
            stateMachine: null,
            mutationLedger: reopened);

        Assert.Equal(
            TargetState.ReconciliationRequired,
            supervisor.State.State);
        Assert.Equal(
            "runtime_interrupted_mutation",
            supervisor.State.IncidentKind);
        Assert.NotNull(supervisor.ActiveIncident);
        Assert.Equal(
            "req-supervisor-recovery",
            supervisor.ActiveIncident!.RequestId);
        Assert.Equal(
            request.Operation,
            supervisor.ActiveIncident.Operation);
        Assert.Equal(
            MutationClass.Unknown,
            supervisor.ActiveIncident.MutationClass);
        Assert.Equal(
            "prepared",
            supervisor.ActiveIncident.Phase);
    }

    [Fact]
    public async Task CompletedRecordDoesNotPoisonSupervisorOnRestart()
    {
        var ledger = new MutationLedger(_root);
        var target = Target();
        var request = Request(
            "req-supervisor-completed",
            """{"kind":"expression","source":"1+1"}""");

        var begin = ledger.Begin(
            target,
            request,
            MutationClass.Unknown);
        ledger.Finalize(
            begin.Record,
            Success(
                request,
                ProtocolValue.FromString("done")));

        var reopened = new MutationLedger(_root);
        await using var supervisor = new TargetSupervisor(
            target,
            new WorkerBrokerOptions
            {
                WorkerExecutablePath = "unused.exe"
            },
            stateMachine: null,
            mutationLedger: reopened);

        Assert.Equal(
            TargetState.Known,
            supervisor.State.State);
        Assert.Null(supervisor.State.IncidentKind);
    }

    [Fact]
    public async Task TargetSupervisorReplaysCompletedMutationWithoutWorkerLaunch()
    {
        var ledger = new MutationLedger(_root);
        var target = Target();
        var original = Request(
            "supervisor-replay",
            """{"kind":"expression","source":"6*7"}""");

        var begin = ledger.Begin(
            target,
            original,
            MutationClass.Unknown);

        using var fortyTwo = JsonDocument.Parse("42");
        ledger.Finalize(
            begin.Record,
            Success(
                original,
                ProtocolValue.From(
                    fortyTwo.RootElement)));

        await using var supervisor = new TargetSupervisor(
            target,
            new WorkerBrokerOptions
            {
                WorkerExecutablePath =
                    Path.Combine(_root, "does-not-exist.exe")
            },
            stateMachine: null,
            mutationLedger: ledger);

        var lease = await supervisor.AcquireLeaseAsync();

        var replay = await supervisor.ExecuteAsync(
            original with
            {
                Policy = new OperationPolicy(
                    LeaseId: lease.LeaseId)
            });

        Assert.True(replay.Ok);
        Assert.Equal("number", replay.Result?.Kind);
        Assert.Equal(
            42d,
            replay.Result?.Value?.GetDouble());
        Assert.Equal(0, replay.Timing?.ExecuteMs);
        Assert.Contains(
            replay.Evidence ?? [],
            evidence =>
                string.Equals(
                    evidence.Kind,
                    "mutation.replay",
                    StringComparison.Ordinal));
        Assert.Null(supervisor.Worker);
    }

    [Fact]
    public async Task PersistedPreparedMutationBlocksNewMutationBeforeWorkerLaunch()
    {
        var ledger = new MutationLedger(_root);
        var target = Target();

        _ = ledger.Begin(
            target,
            Request(
                "interrupted",
                """{"kind":"code","source":"return 1;"}"""),
            MutationClass.Unknown);

        await using var supervisor = new TargetSupervisor(
            target,
            new WorkerBrokerOptions
            {
                WorkerExecutablePath =
                    Path.Combine(_root, "does-not-exist.exe")
            },
            stateMachine: null,
            mutationLedger: new MutationLedger(_root));

        var lease = await supervisor.AcquireLeaseAsync();

        var next = Request(
            "next",
            """{"kind":"code","source":"return 2;"}""") with
        {
            Policy = new OperationPolicy(
                LeaseId: lease.LeaseId)
        };

        var blocked = await supervisor.ExecuteAsync(next);

        Assert.False(blocked.Ok);
        Assert.Equal(
            OperationStatus.ReconciliationRequired,
            blocked.Status);
        Assert.Equal(
            ExecutionState.NotStarted,
            blocked.Error?.Execution);
        Assert.Null(supervisor.Worker);
    }

    [Fact]
    public void ExplicitResolutionIsDurableTerminalState()
    {
        var ledger = new MutationLedger(_root);
        var target = Target();
        var request = Request(
            "resolved-request",
            """{"kind":"code","source":"danger();"}""");

        var begin = ledger.Begin(
            target,
            request,
            MutationClass.Unknown);

        ledger.Finalize(
            begin.Record,
            Ambiguous(
                request,
                "script_error"));

        using var evidenceDocument = JsonDocument.Parse(
            """{"checked":"document fingerprint","matched":true}""");

        var resolved = ledger.ResolveIncident(
            target.Identity.TargetId,
            request.Id,
            changed: false,
            rationale:
                "Postcondition inspection proved the document fingerprint is unchanged.",
            evidenceDocument.RootElement);

        Assert.Equal(
            MutationLedgerPhase.ResolvedUnchanged,
            resolved.Phase);
        Assert.Equal(
            "known_unchanged",
            resolved.Resolution);
        Assert.NotNull(resolved.ResolvedAt);
        Assert.Contains(
            "fingerprint",
            resolved.ResolutionRationale,
            StringComparison.Ordinal);
        Assert.Null(
            ledger.GetUnresolvedTarget(
                target.Identity.TargetId));

        var reopened = new MutationLedger(_root);
        var repeated = reopened.Begin(
            target,
            request,
            MutationClass.Unknown);

        Assert.Equal(
            MutationLedgerBeginDisposition.Resolved,
            repeated.Disposition);
        Assert.Null(repeated.StoredResult);
    }

    [Fact]
    public void IncidentResolutionRequiresExactActiveRequestId()
    {
        var ledger = new MutationLedger(_root);
        var target = Target();
        var request = Request(
            "actual-incident",
            """{"kind":"code","source":"danger();"}""");

        _ = ledger.Begin(
            target,
            request,
            MutationClass.Unknown);

        var error = Assert.Throws<MutationLedgerException>(
            () => ledger.ResolveIncident(
                target.Identity.TargetId,
                "different-request",
                changed: true,
                rationale: "wrong incident",
                evidence: null));

        Assert.Equal(
            "mutation_incident_request_mismatch",
            error.Kind);
        Assert.NotNull(
            ledger.GetUnresolvedTarget(
                target.Identity.TargetId));
    }

    [Fact]
    public async Task SupervisorResolutionClearsDurableIncidentWithoutWorkerLaunch()
    {
        var ledger = new MutationLedger(_root);
        var target = Target();
        var original = Request(
            "supervisor-incident",
            """{"kind":"code","source":"danger();"}""");

        var begin = ledger.Begin(
            target,
            original,
            MutationClass.Unknown);
        ledger.Finalize(
            begin.Record,
            Ambiguous(
                original,
                "script_error"));

        var lockDirectory =
            Path.Combine(_root, "resolution-locks");

        await using var supervisor = new TargetSupervisor(
            target,
            new WorkerBrokerOptions
            {
                WorkerExecutablePath =
                    Path.Combine(
                        _root,
                        "does-not-exist.exe")
            },
            stateMachine: null,
            mutationLedger: new MutationLedger(_root),
            leaseLockDirectory: lockDirectory);

        var observed = supervisor.State;
        Assert.Equal(
            TargetState.ReconciliationRequired,
            observed.State);

        var lease =
            await supervisor.AcquireLeaseAsync();

        using var evidenceDocument = JsonDocument.Parse(
            """{"selectionCount":1,"documentSaved":true}""");

        var resolution =
            await supervisor.ResolveMutationIncidentAsync(
                lease.LeaseId,
                original.Id,
                observed.Revision,
                changed: true,
                rationale:
                    "Postcondition inspection confirms the intended document change is present.",
                evidence: evidenceDocument.RootElement);

        Assert.Equal(
            "known_changed",
            resolution.Resolution);
        Assert.Equal(
            TargetState.KnownChanged,
            resolution.State.State);
        Assert.Null(
            new MutationLedger(_root)
                .GetUnresolvedTarget(
                    target.Identity.TargetId));
        Assert.Null(supervisor.ActiveIncident);
        Assert.Null(supervisor.Worker);

        var oldRequest = original with
        {
            Policy = new OperationPolicy(
                LeaseId: lease.LeaseId)
        };

        var repeat =
            await supervisor.ExecuteAsync(
                oldRequest);

        Assert.False(repeat.Ok);
        Assert.Equal(
            "mutation_request_already_resolved",
            repeat.Error?.Kind);
        Assert.Equal(
            ExecutionState.NotStarted,
            repeat.Error?.Execution);
        Assert.Null(supervisor.Worker);

        await supervisor.ReleaseLeaseAsync(
            lease.LeaseId);
    }

    [Fact]
    public async Task StaleResolutionRevisionLeavesLedgerIncidentActive()
    {
        var ledger = new MutationLedger(_root);
        var target = Target();
        var request = Request(
            "stale-resolution",
            """{"kind":"code","source":"danger();"}""");

        _ = ledger.Begin(
            target,
            request,
            MutationClass.Unknown);

        await using var supervisor = new TargetSupervisor(
            target,
            new WorkerBrokerOptions
            {
                WorkerExecutablePath = "unused.exe"
            },
            stateMachine: null,
            mutationLedger: new MutationLedger(_root),
            leaseLockDirectory:
                Path.Combine(
                    _root,
                    "stale-locks"));

        var observed = supervisor.State;
        var lease =
            await supervisor.AcquireLeaseAsync();

        var error =
            await Assert.ThrowsAsync<TargetStateRevisionException>(
                () => supervisor.ResolveMutationIncidentAsync(
                    lease.LeaseId,
                    request.Id,
                    expectedRevision:
                        observed.Revision + 1,
                    changed: false,
                    rationale: "stale observation"));

        Assert.Equal(
            observed.Revision + 1,
            error.ExpectedRevision);
        Assert.Equal(
            observed.Revision,
            error.ActualRevision);
        Assert.Equal(
            TargetState.ReconciliationRequired,
            supervisor.State.State);
        Assert.NotNull(
            new MutationLedger(_root)
                .GetUnresolvedTarget(
                    target.Identity.TargetId));

        await supervisor.ReleaseLeaseAsync(
            lease.LeaseId);
    }

    [Fact]
    public void ProbeOfNewIntentDoesNotCreatePreparedState()
    {
        var ledger = new MutationLedger(_root);
        var target = Target();
        var request = Request(
            "probe-new",
            """{"kind":"code","source":"return 1;"}""");

        var probe = ledger.Probe(
            target,
            request,
            MutationClass.Unknown);

        Assert.Equal(
            MutationLedgerBeginDisposition.Proceed,
            probe.Disposition);
        Assert.Null(probe.Record);
        Assert.Null(probe.StoredResult);

        Assert.Empty(
            Directory.GetFiles(
                Path.Combine(_root, "records"),
                "*.json"));
        Assert.Empty(
            Directory.GetFiles(
                Path.Combine(_root, "active"),
                "*.json"));
        Assert.Null(
            ledger.GetUnresolvedTarget(
                target.Identity.TargetId));
    }

    [Fact]
    public void ProbeReplaysCompletedIntentWithoutWritingNewState()
    {
        var ledger = new MutationLedger(_root);
        var target = Target();
        var request = Request(
            "probe-completed",
            """{"kind":"expression","source":"2+2"}""");

        var begin = ledger.Begin(
            target,
            request,
            MutationClass.Unknown);

        ledger.Finalize(
            begin.Record,
            Success(
                request,
                ProtocolValue.FromString("done")));

        var recordCount = Directory
            .GetFiles(
                Path.Combine(_root, "records"),
                "*.json")
            .Length;

        var probe = ledger.Probe(
            target,
            request,
            MutationClass.Unknown);

        Assert.Equal(
            MutationLedgerBeginDisposition.ReplayCompleted,
            probe.Disposition);
        Assert.NotNull(probe.Record);
        Assert.True(probe.StoredResult?.Ok);
        Assert.Equal(
            recordCount,
            Directory
                .GetFiles(
                    Path.Combine(_root, "records"),
                    "*.json")
                .Length);
        Assert.Empty(
            Directory.GetFiles(
                Path.Combine(_root, "active"),
                "*.json"));
    }

    [Fact]
    public void OfflineResolutionRejectsStaleIncidentTimestamp()
    {
        var ledger = new MutationLedger(_root);
        var target = Target();
        var request = Request(
            "offline-resolution-stale",
            """{"kind":"code","source":"danger();"}""");
        var begin = ledger.Begin(
            target,
            request,
            MutationClass.Unknown);

        var error = Assert.Throws<MutationLedgerException>(
            () => ledger.ResolveIncident(
                target.Identity.TargetId,
                request.Id,
                changed: false,
                rationale: "stale inspection",
                evidence: null,
                expectedUpdatedAt:
                    begin.Record.UpdatedAt.AddTicks(-1)));

        Assert.Equal(
            "mutation_incident_revision_conflict",
            error.Kind);
        Assert.NotNull(
            ledger.GetUnresolvedTarget(
                target.Identity.TargetId));
    }

    [Fact]
    public void TypedPropertyMutationLedgerIsGenerationBoundAndReplaySafe()
    {
        var ledger = new MutationLedger(_root);
        var target = Target();
        var definition = BuiltInOperations.Catalog.GetRequired(
            "illustrator.layer.setOpacity");
        using var input = JsonDocument.Parse(
            """
            {
              "document":{"index":0},
              "layer":{"name":"Layer B"},
              "value":42.5
            }
            """);
        var request = new OperationRequest
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = "typed-opacity-generation-bound",
            Target = target.Target,
            Operation = definition.Name,
            Input = input.RootElement.Clone()
        };

        Assert.Equal(
            MutationClass.IdempotentWrite,
            definition.MutationClass);
        Assert.True(definition.RequiresLease);

        var begin = ledger.Begin(
            target,
            request,
            definition.MutationClass);
        Assert.Equal(
            target.Identity.TargetId,
            begin.Record.TargetId);
        Assert.Equal(
            target.Identity.ProcessId,
            begin.Record.ProcessId);
        Assert.Equal(
            target.Identity.ProcessStartedAt,
            begin.Record.ProcessStartedAt);
        Assert.Equal(
            MutationClass.IdempotentWrite,
            begin.Record.MutationClass);

        ledger.Finalize(
            begin.Record,
            Success(
                request,
                ProtocolValue.FromString("applied")));

        var replay = ledger.Begin(
            target,
            request,
            definition.MutationClass);
        Assert.Equal(
            MutationLedgerBeginDisposition.ReplayCompleted,
            replay.Disposition);
        Assert.Equal(
            "applied",
            replay.StoredResult?.Result?.Value?.GetString());

        var nextIdentity = target.Identity with
        {
            ProcessStartedAt =
                target.Identity.ProcessStartedAt.AddSeconds(1)
        };
        var nextTarget = new HostTargetDescriptor
        {
            Identity = nextIdentity,
            Target = new TargetRef(
                nextIdentity.Host,
                nextIdentity.TargetId,
                Generation: 0),
            Capabilities = Array.Empty<CapabilityDescriptor>(),
            Running = true
        };
        var nextRequest = request with
        {
            Target = nextTarget.Target
        };

        Assert.NotEqual(
            target.Identity.TargetId,
            nextIdentity.TargetId);
        var nextBegin = ledger.Begin(
            nextTarget,
            nextRequest,
            definition.MutationClass);
        Assert.Equal(
            MutationLedgerBeginDisposition.Proceed,
            nextBegin.Disposition);
        Assert.Equal(
            nextIdentity.TargetId,
            nextBegin.Record.TargetId);
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

    private static HostTargetDescriptor Target()
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
            Capabilities = Array.Empty<CapabilityDescriptor>(),
            Running = true
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

    private static OperationResult NotStarted(
        OperationRequest request) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = false,
            Status = OperationStatus.TargetUnavailable,
            TargetState = TargetState.Unavailable,
            Error = new ProtocolError
            {
                Kind = "worker_start_failed",
                Message = "Worker never started.",
                Retryable = true,
                Execution = ExecutionState.NotStarted
            }
        };
}
