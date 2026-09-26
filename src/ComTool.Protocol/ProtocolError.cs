using System.Text.Json.Serialization;

namespace ComTool.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProtocolError
{
    public required string Kind { get; init; }
    public required string Message { get; init; }
    public required bool Retryable { get; init; }
    public required ExecutionState Execution { get; init; }
    public int? HResult { get; init; }
    public string? HResultHex { get; init; }
    public IReadOnlyList<string>? SuggestedActions { get; init; }
}
