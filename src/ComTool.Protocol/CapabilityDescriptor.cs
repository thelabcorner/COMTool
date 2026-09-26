using System.Text.Json.Serialization;

namespace ComTool.Protocol;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CapabilityDescriptor
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required MutationClass MutationClass { get; init; }
    public required bool Supported { get; init; }
    public string? Host { get; init; }
    public string? Description { get; init; }
    public string? InputSchemaRef { get; init; }
    public string? OutputSchemaRef { get; init; }
}
