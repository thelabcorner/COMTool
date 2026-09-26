using ComTool.Broker.Protocol;
using ComTool.Protocol;
using ComTool.Runtime;

namespace ComTool.Supervisor;

/// <summary>
/// Interprets generic host reconciliation evidence without allowing a liveness
/// probe to erase uncertainty about a previously dispatched mutation.
/// </summary>
internal static class GenericReconciliationPolicy
{
    public static BrokerReconciliation Apply(
        TargetStateSnapshot stateBefore,
        BrokerReconciliation hostEvidence)
    {
        ArgumentNullException.ThrowIfNull(stateBefore);
        ArgumentNullException.ThrowIfNull(hostEvidence);

        if (stateBefore.State != TargetState.ReconciliationRequired)
            return hostEvidence;

        if (hostEvidence.TargetState == TargetState.Unavailable)
        {
            return hostEvidence with
            {
                Reconciled = false,
                TargetState = TargetState.ReconciliationRequired,
                Error = new ProtocolError
                {
                    Kind = "mutation_outcome_unresolved_host_unavailable",
                    Message =
                        "The prior mutation outcome remains unresolved and the target is currently unavailable.",
                    Retryable = false,
                    Execution = ExecutionState.NotStarted,
                    SuggestedActions =
                    [
                        "restore_or_reconnect_target",
                        "apply_operation_specific_postconditions",
                        "resolve_incident_explicitly"
                    ]
                }
            };
        }

        return hostEvidence with
        {
            Reconciled = false,
            TargetState = TargetState.ReconciliationRequired,
            Error = new ProtocolError
            {
                Kind = "mutation_outcome_unresolved",
                Message =
                    "The target is reachable, but generic host liveness does not prove the outcome of the prior ambiguous mutation.",
                Retryable = false,
                Execution = ExecutionState.NotStarted,
                SuggestedActions =
                [
                    "apply_operation_specific_postconditions",
                    "resolve_incident_explicitly"
                ]
            }
        };
    }
}
