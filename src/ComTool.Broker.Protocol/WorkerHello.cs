using System.Text.Json.Serialization;

namespace ComTool.Broker.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkerHello
{
    public required int BrokerVersion { get; init; }
    public required string Token { get; init; }
    public required WorkerMode Mode { get; init; }
    public required string WorkerId { get; init; }
    public required int ProcessId { get; init; }
    public required DateTimeOffset ProcessStartedAt { get; init; }
    public required string Apartment { get; init; }
    public required string Host { get; init; }
    public string? TargetId { get; init; }
    public string? HostVersion { get; init; }
    public required string AdapterVersion { get; init; }
}
