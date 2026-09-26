using System.Text.Json;
using System.Text.Json.Serialization;

namespace ComTool.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperationCondition
{
    public required string Id { get; init; }
    public required OperationConditionSource Source { get; init; }
    public required OperationConditionPredicate Predicate { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperationConditionSource
{
    public required string Operation { get; init; }
    public required JsonElement Input { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperationConditionPredicate
{
    public required string Kind { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProtocolValue? Expected { get; init; }
}
