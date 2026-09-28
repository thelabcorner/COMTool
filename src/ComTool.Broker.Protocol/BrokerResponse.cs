using System.Text.Json.Serialization;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Broker.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BrokerResponse
{
    public required int BrokerVersion { get; init; }
    public required long Sequence { get; init; }
    public required string RequestId { get; init; }
    public required BrokerResponseKind Kind { get; init; }
    public required bool Ok { get; init; }
    public OperationResult? OperationResult { get; init; }
    public BrokerArtifactChunk? ArtifactChunk { get; init; }
    public BrokerArtifactTransfer? ArtifactTransfer { get; init; }
    public BrokerReconciliation? Reconciliation { get; init; }
    public BrokerWorkerStatus? Worker { get; init; }
    public IReadOnlyList<HostTargetDescriptor>? Targets { get; init; }
    public HostLaunchObservation? Launch { get; init; }
    public ProtocolError? Error { get; init; }
    public BrokerOperationExecutionDisposition?
        OperationExecutionDisposition { get; init; }
}
