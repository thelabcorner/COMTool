using System.Text.Json.Serialization;

namespace ComTool.Broker.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkerHelloAck
{
    public required int BrokerVersion { get; init; }
    public required bool Accepted { get; init; }
    public string? Error { get; init; }
}
