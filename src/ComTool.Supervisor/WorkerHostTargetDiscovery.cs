using ComTool.Hosts.Abstractions;

namespace ComTool.Supervisor;

/// <summary>
/// Adapter from the existing authenticated discovery-worker path to the
/// host-lifecycle discovery contract. No second discovery implementation.
/// </summary>
public sealed class WorkerHostTargetDiscovery : IHostTargetDiscovery
{
    private readonly WorkerBrokerOptions _options;

    public WorkerHostTargetDiscovery(WorkerBrokerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public ValueTask<IReadOnlyList<HostTargetDescriptor>> DiscoverAsync(
        string host,
        CancellationToken cancellationToken = default) =>
        new(
            WorkerDiscoveryClient.DiscoverAsync(
                host,
                _options,
                cancellationToken));
}
