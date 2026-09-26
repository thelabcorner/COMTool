using System.Text.Json.Serialization;

namespace ComTool.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperationResult
{
    public required int ProtocolVersion { get; init; }
    public required string Id { get; init; }
    public required string Operation { get; init; }
    public required bool Ok { get; init; }
    public required OperationStatus Status { get; init; }
    public required TargetState TargetState { get; init; }
    public ProtocolValue? Result { get; init; }
    public ProtocolError? Error { get; init; }
    public IReadOnlyList<EvidenceItem>? Evidence { get; init; }
    public OperationTiming? Timing { get; init; }
}
