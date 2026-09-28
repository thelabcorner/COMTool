using System.Text.Json.Serialization;
using ComTool.Hosts.Abstractions;

namespace ComTool.Broker.Protocol;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Default,
    WriteIndented = false)]
[JsonSerializable(typeof(WorkerHello))]
[JsonSerializable(typeof(WorkerHelloAck))]
[JsonSerializable(typeof(BrokerCommand))]
[JsonSerializable(typeof(BrokerResponse))]
[JsonSerializable(typeof(BrokerArtifactChunk))]
[JsonSerializable(typeof(BrokerArtifactTransfer))]
[JsonSerializable(typeof(BrokerReconciliation))]
[JsonSerializable(typeof(BrokerWorkerStatus))]
[JsonSerializable(typeof(HostTargetDescriptor))]
[JsonSerializable(typeof(HostTargetDescriptor[]))]
[JsonSerializable(typeof(HostLaunchSpec))]
[JsonSerializable(typeof(HostLaunchObservation))]
public partial class BrokerJsonContext : JsonSerializerContext;
