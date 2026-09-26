using ComTool.Protocol;

namespace ComTool.Runtime;

public interface IOperationDispatcher
{
    ValueTask<OperationResult> ExecuteAsync(
        OperationRequest request,
        CancellationToken cancellationToken = default);
}
