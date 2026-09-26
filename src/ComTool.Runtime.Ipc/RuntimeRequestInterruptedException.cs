using ComTool.Protocol;

namespace ComTool.Runtime.Ipc;

public sealed class RuntimeRequestInterruptedException(
    OperationRequest request,
    Exception innerException)
    : IOException(
        $"Runtime request '{request.Id}' for '{request.Operation}' was interrupted after dispatch began; execution outcome is ambiguous.",
        innerException)
{
    public string RequestId { get; } = request.Id;

    public string Operation { get; } = request.Operation;

    public ExecutionState Execution { get; } = ExecutionState.Ambiguous;
}