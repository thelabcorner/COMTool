using ComTool.Hosts.Abstractions;
using ComTool.Protocol;
using ComTool.Runtime;

namespace ComTool.Runtime.Tests;

public sealed class RuntimeSafetyTests
{
    [Fact]
    public void AmbiguousMutationRequiresExplicitReconciliation()
    {
        var state = new TargetStateMachine();

        state.MarkWorkerLost(
            executionStarted: true,
            MutationClass.NonIdempotentWrite);

        Assert.Equal(
            TargetState.ReconciliationRequired,
            state.Snapshot().State);

        Assert.Throws<TargetStateException>(
            () => state.EnsureOperationAllowed(MutationClass.ConditionalWrite));

        // Read-only inspection remains allowed so reconciliation can inspect
        // host/document state.
        state.EnsureOperationAllowed(MutationClass.ReadOnly);

        // Reconnecting transport/proxy is insufficient to erase ambiguity.
        state.MarkReconnected();
        Assert.Equal(
            TargetState.ReconciliationRequired,
            state.Snapshot().State);

        state.MarkReconciled(changed: true);

        Assert.Equal(TargetState.KnownChanged, state.Snapshot().State);
        state.EnsureOperationAllowed(MutationClass.NonIdempotentWrite);
    }

    [Fact]
    public void SurvivingWorkerCanReportAmbiguousMutation()
    {
        var state = new TargetStateMachine();

        state.MarkAmbiguousExecution(
            MutationClass.ConditionalWrite,
            "host_server_fault_after_dispatch");

        var snapshot = state.Snapshot();
        Assert.Equal(TargetState.ReconciliationRequired, snapshot.State);
        Assert.Equal("host_server_fault_after_dispatch", snapshot.IncidentKind);

        Assert.Throws<TargetStateException>(
            () => state.EnsureOperationAllowed(MutationClass.NonIdempotentWrite));
    }

    [Fact]
    public void AmbiguousReadDoesNotPoisonTargetForMutation()
    {
        var state = new TargetStateMachine();

        state.MarkAmbiguousExecution(MutationClass.ReadOnly);

        Assert.Equal(TargetState.Known, state.Snapshot().State);
        state.EnsureOperationAllowed(MutationClass.ConditionalWrite);
    }

    [Fact]
    public void SuccessfulReadDoesNotEraseExistingMutationAmbiguity()
    {
        var state = new TargetStateMachine();

        state.MarkAmbiguousExecution(
            MutationClass.NonIdempotentWrite,
            "mutation_outcome_unknown");

        state.MarkCompleted(MutationClass.ReadOnly);

        var snapshot = state.Snapshot();
        Assert.Equal(TargetState.ReconciliationRequired, snapshot.State);
        Assert.Equal("mutation_outcome_unknown", snapshot.IncidentKind);
    }

    [Fact]
    public void SuccessfulReadDoesNotEraseKnownChangedState()
    {
        var state = new TargetStateMachine();

        state.MarkCompleted(
            MutationClass.ConditionalWrite);

        var changed = state.Snapshot();
        Assert.Equal(
            TargetState.KnownChanged,
            changed.State);

        state.MarkCompleted(
            MutationClass.ReadOnly);

        var afterRead = state.Snapshot();
        Assert.Equal(
            TargetState.KnownChanged,
            afterRead.State);
        Assert.Equal(
            changed.Revision,
            afterRead.Revision);
    }

    [Fact]
    public void AmbiguousReadDoesNotEraseExistingMutationAmbiguity()
    {
        var state = new TargetStateMachine();

        state.MarkAmbiguousExecution(
            MutationClass.ConditionalWrite,
            "mutation_outcome_unknown");

        state.MarkAmbiguousExecution(
            MutationClass.ReadOnly,
            "read_outcome_unknown");

        var snapshot = state.Snapshot();
        Assert.Equal(TargetState.ReconciliationRequired, snapshot.State);
        Assert.Equal("mutation_outcome_unknown", snapshot.IncidentKind);
    }

    [Fact]
    public void ReadWorkerLossDoesNotEraseExistingMutationAmbiguity()
    {
        var state = new TargetStateMachine();

        state.MarkAmbiguousExecution(
            MutationClass.ConditionalWrite,
            "mutation_outcome_unknown");

        state.MarkWorkerLost(
            executionStarted: true,
            MutationClass.ReadOnly);

        var snapshot = state.Snapshot();
        Assert.Equal(TargetState.ReconciliationRequired, snapshot.State);
        Assert.Equal("mutation_outcome_unknown", snapshot.IncidentKind);
    }

    [Fact]
    public void HostUnavailabilityDoesNotEraseExistingMutationAmbiguity()
    {
        var state = new TargetStateMachine();

        state.MarkAmbiguousExecution(
            MutationClass.NonIdempotentWrite,
            "mutation_outcome_unknown");

        state.MarkHostUnavailable("host_exited");

        var snapshot = state.Snapshot();
        Assert.Equal(TargetState.ReconciliationRequired, snapshot.State);
        Assert.Equal("mutation_outcome_unknown", snapshot.IncidentKind);
    }

    [Fact]
    public void WorkerLossBeforeExecutionDoesNotCreateMutationAmbiguity()
    {
        var state = new TargetStateMachine();

        state.MarkWorkerLost(
            executionStarted: false,
            MutationClass.NonIdempotentWrite);

        var snapshot = state.Snapshot();

        Assert.Equal(TargetState.Unavailable, snapshot.State);
        Assert.Equal("worker_loss", snapshot.IncidentKind);
    }

    [Fact]
    public void ReadOnlyWorkerLossDoesNotRequireMutationReconciliation()
    {
        var state = new TargetStateMachine();

        state.MarkWorkerLost(
            executionStarted: true,
            MutationClass.ReadOnly);

        Assert.Equal(TargetState.Unavailable, state.Snapshot().State);
    }

    [Fact]
    public void MutationCompletionMarksKnownChanged()
    {
        var state = new TargetStateMachine();

        state.MarkBusy();
        state.MarkCompleted(MutationClass.ConditionalWrite);

        Assert.Equal(TargetState.KnownChanged, state.Snapshot().State);
    }

    [Fact]
    public void ReadCompletionMarksKnown()
    {
        var state = new TargetStateMachine();

        state.MarkBusy();
        state.MarkCompleted(MutationClass.ReadOnly);

        Assert.Equal(TargetState.Known, state.Snapshot().State);
    }

    [Fact]
    public void DynamicMutationPolicyDefaultsToUnknown()
    {
        using var input = System.Text.Json.JsonDocument.Parse(
            """{"kind":"expression","source":"app.version"}""");

        var definition = new OperationDefinition(
            "script.eval",
            MutationClass.Unknown,
            RequiresTarget: true,
            MutationResolution: MutationResolutionMode.DeclaredOrUnknown);

        var request = new OperationRequest
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = "eval-unknown",
            Target = new TargetRef("illustrator", "illustrator:test"),
            Operation = "script.eval",
            Input = input.RootElement.Clone()
        };

        Assert.Equal(
            MutationClass.Unknown,
            OperationMutationResolver.Resolve(definition, request));
    }

    [Theory]
    [InlineData("idempotent_write", MutationClass.IdempotentWrite)]
    [InlineData("conditional_write", MutationClass.ConditionalWrite)]
    [InlineData("non_idempotent_write", MutationClass.NonIdempotentWrite)]
    [InlineData("document_lifecycle", MutationClass.DocumentLifecycle)]
    [InlineData("external_side_effect", MutationClass.ExternalSideEffect)]
    [InlineData("unknown", MutationClass.Unknown)]
    public void DynamicMutationPolicyAcceptsOnlyConservativeEffectClasses(
        string effects,
        MutationClass expected)
    {
        using var input = System.Text.Json.JsonDocument.Parse(
            $$"""{"kind":"code","source":"return 1;","effects":"{{effects}}"}""");

        var definition = new OperationDefinition(
            "script.eval",
            MutationClass.Unknown,
            RequiresTarget: true,
            MutationResolution: MutationResolutionMode.DeclaredOrUnknown);

        var request = new OperationRequest
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = "eval-effects",
            Target = new TargetRef("illustrator", "illustrator:test"),
            Operation = "script.eval",
            Input = input.RootElement.Clone()
        };

        Assert.Equal(
            expected,
            OperationMutationResolver.Resolve(definition, request));
    }

    [Fact]
    public void ArbitraryScriptCannotSelfCertifyReadOnly()
    {
        using var input = System.Text.Json.JsonDocument.Parse(
            """{"kind":"expression","source":"app.version","effects":"read_only"}""");

        var definition = new OperationDefinition(
            "script.eval",
            MutationClass.Unknown,
            RequiresTarget: true,
            MutationResolution: MutationResolutionMode.DeclaredOrUnknown);

        var request = new OperationRequest
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = "eval-read-only",
            Target = new TargetRef("illustrator", "illustrator:test"),
            Operation = "script.eval",
            Input = input.RootElement.Clone()
        };

        var error = Assert.Throws<OperationMutationPolicyException>(
            () => OperationMutationResolver.Resolve(definition, request));

        Assert.Equal("unproven_read_only_script", error.Kind);
    }

    [Fact]
    public void FixedMutationPolicyIgnoresCallerEffectsField()
    {
        using var input = System.Text.Json.JsonDocument.Parse(
            """{"effects":"non_idempotent_write"}""");

        var definition = new OperationDefinition(
            "core.target.status",
            MutationClass.ReadOnly,
            RequiresTarget: true);

        var request = new OperationRequest
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = "status-fixed",
            Target = new TargetRef("illustrator", "illustrator:test"),
            Operation = "core.target.status",
            Input = input.RootElement.Clone()
        };

        Assert.Equal(
            MutationClass.ReadOnly,
            OperationMutationResolver.Resolve(definition, request));
    }

    [Fact]
    public void IncidentResolutionRequiresExactStateRevision()
    {
        var state = new TargetStateMachine();

        state.MarkAmbiguousExecution(
            MutationClass.Unknown,
            "script_error");

        var observed = state.Snapshot();

        state.MarkReconciled(
            observed.Revision,
            changed: true);

        var resolved = state.Snapshot();

        Assert.Equal(
            TargetState.KnownChanged,
            resolved.State);
        Assert.Null(resolved.IncidentKind);
        Assert.Equal(
            observed.Revision + 1,
            resolved.Revision);
    }

    [Fact]
    public void StaleIncidentResolutionCannotClearNewerState()
    {
        var state = new TargetStateMachine();

        state.MarkAmbiguousExecution(
            MutationClass.Unknown,
            "first");

        var staleRevision =
            state.Snapshot().Revision;

        state.MarkAmbiguousExecution(
            MutationClass.Unknown,
            "newer");

        var error =
            Assert.Throws<TargetStateRevisionException>(
                () => state.MarkReconciled(
                    staleRevision,
                    changed: false));

        Assert.Equal(
            staleRevision,
            error.ExpectedRevision);
        Assert.Equal(
            state.Snapshot().Revision,
            error.ActualRevision);
        Assert.Equal(
            TargetState.ReconciliationRequired,
            state.Snapshot().State);
        Assert.Equal(
            "newer",
            state.Snapshot().IncidentKind);
    }

    [Fact]
    public void IncidentResolutionRequiresActiveIncident()
    {
        var state = new TargetStateMachine();

        var error = Assert.Throws<TargetStateException>(
            () => state.MarkReconciled(
                state.Snapshot().Revision,
                changed: false));

        Assert.Equal(
            "no_reconciliation_incident",
            error.Kind);
        Assert.Equal(
            TargetState.Known,
            state.Snapshot().State);
    }

    [Fact]
    public void OperationCatalogRejectsDuplicates()
    {
        var definitions = new[]
        {
            new OperationDefinition("x", MutationClass.ReadOnly, false),
            new OperationDefinition("x", MutationClass.ReadOnly, false)
        };

        Assert.Throws<ArgumentException>(() => new OperationCatalog(definitions));
    }

    [Fact]
    public void OperationCatalogUsesOrdinalNames()
    {
        var catalog = new OperationCatalog(
        [
            new OperationDefinition("core.status", MutationClass.ReadOnly, false)
        ]);

        Assert.True(catalog.TryGet("core.status", out _));
        Assert.False(catalog.TryGet("CORE.STATUS", out _));
    }

    [Fact]
    public void TargetIdentityIsStableAcrossWorkerRestarts()
    {
        var identity = new HostTargetIdentity
        {
            Host = "illustrator",
            ProcessId = 1234,
            ProcessStartedAt = DateTimeOffset.Parse("2026-09-24T16:00:00Z"),
            ExecutablePath = @"C:\Program Files\Adobe\Illustrator.exe",
            HostVersion = "30.6.0",
            AdapterVersion = "0.1.0",
            EndpointIdentity = "Illustrator.Application"
        };

        var sameHostNewWorker = identity with
        {
            AdapterVersion = "0.1.1"
        };

        Assert.Equal(identity.TargetId, sameHostNewWorker.TargetId);
    }

    [Fact]
    public void TargetIdentityChangesWhenHostProcessGenerationChanges()
    {
        var before = new HostTargetIdentity
        {
            Host = "illustrator",
            ProcessId = 1234,
            ProcessStartedAt = DateTimeOffset.Parse("2026-09-24T16:00:00Z"),
            ExecutablePath = @"C:\Program Files\Adobe\Illustrator.exe",
            HostVersion = "30.6.0",
            AdapterVersion = "0.1.0",
            EndpointIdentity = "Illustrator.Application"
        };

        var afterRestart = before with
        {
            ProcessStartedAt = before.ProcessStartedAt.AddMinutes(10)
        };

        Assert.NotEqual(before.TargetId, afterRestart.TargetId);
    }
}
