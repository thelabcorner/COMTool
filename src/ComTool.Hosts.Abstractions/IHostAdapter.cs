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

/// <summary>
/// Optional host-adapter facet for explicit host activation. The caller must
/// still own launch policy/provenance; this facet only performs host-specific
/// activation and returns a truthful observation.
/// </summary>
public interface IHostLaunchAdapter : IHostAdapter
{
    ValueTask<HostLaunchObservation> LaunchAsync(
        HostLaunchSpec spec,
        CancellationToken cancellationToken = default);
}
