using System.Text.Json.Serialization;

namespace ComTool.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TargetRef(
    string Host,
    string? Id = null,
    long? Generation = null);
