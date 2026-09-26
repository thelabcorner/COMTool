using ComTool.Protocol;

namespace ComTool.Hosts.Abstractions;

public sealed record HostTargetDescriptor
{
    public required HostTargetIdentity Identity { get; init; }
    public required TargetRef Target { get; init; }
    public required IReadOnlyList<CapabilityDescriptor> Capabilities { get; init; }
    public required bool Running { get; init; }
}
