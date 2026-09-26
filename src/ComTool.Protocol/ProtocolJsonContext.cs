using System.Text.Json.Serialization;

namespace ComTool.Protocol;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Default,
    WriteIndented = false)]
[JsonSerializable(typeof(OperationRequest))]
[JsonSerializable(typeof(OperationResult))]
[JsonSerializable(typeof(TargetRef))]
[JsonSerializable(typeof(OperationPolicy))]
[JsonSerializable(typeof(OperationCondition))]
[JsonSerializable(typeof(OperationConditionSource))]
[JsonSerializable(typeof(OperationConditionPredicate))]
[JsonSerializable(typeof(ProtocolValue))]
[JsonSerializable(typeof(ProtocolError))]
[JsonSerializable(typeof(OperationTiming))]
[JsonSerializable(typeof(EvidenceItem))]
[JsonSerializable(typeof(CapabilityDescriptor))]
[JsonSerializable(typeof(List<CapabilityDescriptor>))]
[JsonSerializable(typeof(List<OperationCondition>))]
[JsonSerializable(typeof(List<EvidenceItem>))]
public partial class ProtocolJsonContext : JsonSerializerContext;
