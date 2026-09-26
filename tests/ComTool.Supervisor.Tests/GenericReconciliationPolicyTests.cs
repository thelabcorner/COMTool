using ComTool.Broker.Protocol;
using ComTool.Protocol;
using ComTool.Runtime;

namespace ComTool.Supervisor.Tests;

public sealed class GenericReconciliationPolicyTests
{
    [Fact]
    public void ReachableHostCannotResolvePriorMutationAmbiguity()
    {
        var before = new TargetStateSnapshot(
            TargetState.ReconciliationRequired,
            Revision: 7,
            IncidentKind: "ambiguous_worker_loss");

        var hostEvidence = new BrokerReconciliation
        {
            Reconciled = true,
            TargetState = TargetState.Known,
            Evidence =
            [
                new EvidenceItem(
                    "host.heartbeat",
                    System.Text.Json.JsonSerializer.SerializeToElement(
                        new { version = "30.6.0" }))
            ]
        };

        var result = GenericReconciliationPolicy.Apply(
            before,
            hostEvidence);

        Assert.False(result.Reconciled);
        Assert.Equal(
            TargetState.ReconciliationRequired,
            result.TargetState);
        Assert.Equal(
            "mutation_outcome_unresolved",
            result.Error?.Kind);
        Assert.Same(hostEvidence.Evidence, result.Evidence);
    }

    [Fact]
    public void UnavailableHostPreservesMutationAmbiguity()
    {
        var before = new TargetStateSnapshot(
            TargetState.ReconciliationRequired,
            Revision: 9,
            IncidentKind: "script_error");

        var hostEvidence = new BrokerReconciliation
        {
            Reconciled = false,
            TargetState = TargetState.Unavailable,
            Error = new ProtocolError
            {
                Kind = "host_unavailable",
                Message = "Illustrator is unavailable.",
                Retryable = true,
                Execution = ExecutionState.NotStarted
            }
        };

        var result = GenericReconciliationPolicy.Apply(
            before,
            hostEvidence);

        Assert.False(result.Reconciled);
        Assert.Equal(
            TargetState.ReconciliationRequired,
            result.TargetState);
        Assert.Equal(
            "mutation_outcome_unresolved_host_unavailable",
            result.Error?.Kind);
        Assert.Contains(
            "restore_or_reconnect_target",
            result.Error?.SuggestedActions ?? []);
    }

    [Fact]
    public void KnownStatePassesThroughHostReconciliationEvidence()
    {
        var before = new TargetStateSnapshot(
            TargetState.Known,
            Revision: 2,
            IncidentKind: null);

        var hostEvidence = new BrokerReconciliation
        {
            Reconciled = true,
            TargetState = TargetState.KnownChanged
        };

        var result = GenericReconciliationPolicy.Apply(
            before,
            hostEvidence);

        Assert.Same(hostEvidence, result);
    }
}
