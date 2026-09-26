using System.Text.Json;
using System.Text.Json.Serialization;

namespace ComTool.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperationRequest
{
    public required int ProtocolVersion { get; init; }
    public required string Id { get; init; }
    public TargetRef? Target { get; init; }
    public required string Operation { get; init; }
    public required JsonElement Input { get; init; }
    public OperationPolicy? Policy { get; init; }
    public IReadOnlyList<OperationCondition>? Preconditions { get; init; }
    public IReadOnlyList<OperationCondition>? Postconditions { get; init; }
}
