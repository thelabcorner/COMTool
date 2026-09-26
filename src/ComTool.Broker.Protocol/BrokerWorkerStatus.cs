using System.Text.Json.Serialization;

namespace ComTool.Broker.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BrokerWorkerStatus
{
    public required string WorkerId { get; init; }
    public required WorkerMode Mode { get; init; }
    public required int ProcessId { get; init; }
    public required DateTimeOffset ProcessStartedAt { get; init; }
    public required string Apartment { get; init; }
    public string? TargetId { get; init; }
}
