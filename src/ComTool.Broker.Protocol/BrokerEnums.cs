using System.Text.Json.Serialization;

namespace ComTool.Broker.Protocol;

[JsonConverter(typeof(JsonStringEnumConverter<BrokerCommandKind>))]
public enum BrokerCommandKind
{
    [JsonStringEnumMemberName("operation")] Operation,
    [JsonStringEnumMemberName("reconcile")] Reconcile,
    [JsonStringEnumMemberName("discover")] Discover,
    [JsonStringEnumMemberName("launch")] Launch,
    [JsonStringEnumMemberName("ping")] Ping,
    [JsonStringEnumMemberName("shutdown")] Shutdown
}

[JsonConverter(typeof(JsonStringEnumConverter<BrokerResponseKind>))]
public enum BrokerResponseKind
{
    [JsonStringEnumMemberName("operation")] Operation,
    [JsonStringEnumMemberName("artifact_chunk")] ArtifactChunk,
    [JsonStringEnumMemberName("reconcile")] Reconcile,
    [JsonStringEnumMemberName("discovery")] Discovery,
    [JsonStringEnumMemberName("launch")] Launch,
    [JsonStringEnumMemberName("pong")] Pong,
    [JsonStringEnumMemberName("shutdown")] Shutdown,
    [JsonStringEnumMemberName("error")] Error
}

[JsonConverter(typeof(JsonStringEnumConverter<BrokerOperationExecutionDisposition>))]
public enum BrokerOperationExecutionDisposition
{
    [JsonStringEnumMemberName("certified_not_started")]
    CertifiedNotStarted,

    [JsonStringEnumMemberName("may_have_started")]
    MayHaveStarted
}

[JsonConverter(typeof(JsonStringEnumConverter<WorkerMode>))]
public enum WorkerMode
{
    [JsonStringEnumMemberName("target")] Target,
    [JsonStringEnumMemberName("discovery")] Discovery,
    [JsonStringEnumMemberName("launch")] Launch
}
