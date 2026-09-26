using System.Text.Json.Serialization;

namespace ComTool.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperationTiming(
    double? QueueMs = null,
    double? ExecuteMs = null,
    double? VerifyMs = null,
    double? TotalMs = null);
