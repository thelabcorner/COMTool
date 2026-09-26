using System.Text.Json.Serialization;
using ComTool.Protocol;

namespace ComTool.Broker.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BrokerReconciliation
{
    public required bool Reconciled { get; init; }
    public required TargetState TargetState { get; init; }
    public IReadOnlyList<EvidenceItem>? Evidence { get; init; }
    public ProtocolError? Error { get; init; }
}
