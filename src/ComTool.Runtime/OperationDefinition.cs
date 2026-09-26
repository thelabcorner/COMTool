using ComTool.Protocol;

namespace ComTool.Runtime;

public sealed record OperationDefinition(
    string Name,
    MutationClass MutationClass,
    bool RequiresTarget,
    OperationExecutionScope Scope = OperationExecutionScope.Host,
    string? Host = null,
    string Version = "1",
    bool RequiresLease = false,
    MutationResolutionMode MutationResolution = MutationResolutionMode.Fixed);
