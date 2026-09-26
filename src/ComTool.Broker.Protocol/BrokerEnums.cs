using System.Text.Json.Serialization;

namespace ComTool.Broker.Protocol;

[JsonConverter(typeof(JsonStringEnumConverter<BrokerCommandKind>))]
public enum BrokerCommandKind
{
    [JsonStringEnumMemberName("operation")] Operation,
    [JsonStringEnumMemberName("reconcile")] Reconcile,
    [JsonStringEnumMemberName("discover")] Discover,
    [JsonStringEnumMemberName("ping")] Ping,
    [JsonStringEnumMemberName("shutdown")] Shutdown
}

[JsonConverter(typeof(JsonStringEnumConverter<BrokerResponseKind>))]
public enum BrokerResponseKind
{
    [JsonStringEnumMemberName("operation")] Operation,
    [JsonStringEnumMemberName("reconcile")] Reconcile,
    [JsonStringEnumMemberName("discovery")] Discovery,
    [JsonStringEnumMemberName("pong")] Pong,
    [JsonStringEnumMemberName("shutdown")] Shutdown,
    [JsonStringEnumMemberName("error")] Error
}

[JsonConverter(typeof(JsonStringEnumConverter<WorkerMode>))]
public enum WorkerMode
{
    [JsonStringEnumMemberName("target")] Target,
    [JsonStringEnumMemberName("discovery")] Discovery
}
