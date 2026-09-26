using ComTool.Broker.Protocol;
using ComTool.Protocol;
using ComTool.Runtime;

namespace ComTool.Supervisor;

/// <summary>
/// Interprets operation-specific reconciliation evidence. Unlike generic host
/// reconciliation, this path may clear ambiguity — but only when the caller
/// repeats the exact postcondition batch durably bound to the original mutation
/// request and that batch is verified and passing against live host state. A
/// missing, changed, unbound, unverified, or failing batch can never resolve the
/// incident.
/// </summary>
internal static class OperationSpecificReconciliationPolicy
{
    public static BrokerReconciliation Apply(
        TargetStateSnapshot stateBefore,
        string incidentRequestId,
        ConditionBatchResult? evidence)
    {
        ArgumentNullException.ThrowIfNull(stateBefore);
        ArgumentException.ThrowIfNullOrWhiteSpace(incidentRequestId);

        if (stateBefore.State != TargetState.ReconciliationRequired)
        {
            return new BrokerReconciliation
            {
                Reconciled = false,
                TargetState = stateBefore.State,
                Error = new ProtocolError
                {
                    Kind = "no_reconciliation_incident",
                    Message =
                        "Operation-specific reconciliation is only meaningful while the target has an unresolved mutation incident.",
                    Retryable = false,
                    Execution = ExecutionState.NotStarted,
                    SuggestedActions =
                    [
                        "inspect_target_state"
                    ]
                }
            };
        }

        if (evidence is null)
        {
            return Unresolved(
                incidentRequestId,
                "operation_specific_evidence_missing",
                "No operation-specific postcondition evidence was supplied; host liveness alone cannot resolve the prior mutation.");
        }

        if (!evidence.Verified)
        {
            return Unresolved(
                incidentRequestId,
                "operation_specific_evidence_unverifiable",
                evidence.FailureMessage ??
                "The operation-specific postcondition could not be verified against the target.");
        }

        if (!evidence.Passed)
        {
            return Unresolved(
                incidentRequestId,
                "operation_specific_postcondition_failed",
                evidence.FailureMessage ??
                "The operation-specific postcondition was proven false; the mutation outcome is still unresolved.");
        }

        // Verified AND passing: the caller has proven, with live host evidence,
        // that the postcondition matching the mutation's intent holds now. That
        // is a resolution, not merely availability.
        return new BrokerReconciliation
        {
            Reconciled = true,
            TargetState = TargetState.KnownChanged,
            Evidence = evidence.Evidence is null
                ? null
                : [evidence.Evidence]
        };
    }

    private static BrokerReconciliation Unresolved(
        string incidentRequestId,
        string kind,
        string message) =>
        new()
        {
            Reconciled = false,
            TargetState = TargetState.ReconciliationRequired,
            Error = new ProtocolError
            {
                Kind = kind,
                Message = message,
                Retryable = false,
                Execution = ExecutionState.Ambiguous,
                SuggestedActions =
                [
                    "inspect_mutation_ledger",
                    "supply_operation_specific_postconditions",
                    "resolve_incident_explicitly"
                ]
            }
        };

    public static BrokerReconciliation EvidenceNotBound(
        string incidentRequestId) =>
        Unresolved(
            incidentRequestId,
            "operation_specific_evidence_not_bound",
            "The supplied postconditions were not durably declared with the original mutation request. New evidence cannot be attached after ambiguity.");
}
