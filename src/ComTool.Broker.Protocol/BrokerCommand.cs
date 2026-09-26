using System.Text.Json.Serialization;
using ComTool.Protocol;

namespace ComTool.Broker.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BrokerCommand
{
    public required int BrokerVersion { get; init; }
    public required long Sequence { get; init; }
    public required string RequestId { get; init; }
    public required BrokerCommandKind Kind { get; init; }
    public MutationClass? MutationClass { get; init; }
    public OperationRequest? Operation { get; init; }
}
