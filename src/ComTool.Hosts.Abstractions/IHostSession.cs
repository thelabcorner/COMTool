using ComTool.Protocol;

namespace ComTool.Hosts.Abstractions;

public interface IHostSession : IAsyncDisposable
{
    HostTargetIdentity Identity { get; }
    IReadOnlyList<CapabilityDescriptor> Capabilities { get; }

    ValueTask<OperationResult> ExecuteAsync(
        OperationRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ReconciliationResult> ReconcileAsync(
        CancellationToken cancellationToken = default);
}

public sealed record ReconciliationResult(
    bool Reconciled,
    TargetState TargetState,
    IReadOnlyList<EvidenceItem>? Evidence = null,
    ProtocolError? Error = null);
