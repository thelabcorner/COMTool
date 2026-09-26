namespace ComTool.Hosts.Abstractions;

public interface IHostAdapter
{
    string Host { get; }
    string AdapterVersion { get; }

    ValueTask<IReadOnlyList<HostTargetDescriptor>> DiscoverAsync(
        CancellationToken cancellationToken = default);

    ValueTask<IHostSession> ConnectAsync(
        HostTargetDescriptor target,
        CancellationToken cancellationToken = default);
}
