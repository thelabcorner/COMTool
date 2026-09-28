using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Supervisor;

/// <summary>
/// One host process generation as read from the operating system: the exact
/// three facts that make a target id strong. A bare process id is never an
/// identity.
/// </summary>
public sealed record HostProcessIdentity(
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    string ExecutablePath)
{
    /// <summary>
    /// True only when the observed generation is the same generation the
    /// identity claims. A reused process id with a different start time, or a
    /// different executable, is a different generation.
    /// </summary>
    public bool Matches(HostTargetIdentity identity) =>
        ProcessId == identity.ProcessId &&
        ProcessStartedAt.ToUniversalTime().Ticks ==
            identity.ProcessStartedAt.ToUniversalTime().Ticks &&
        PathsEqual(ExecutablePath, identity.ExecutablePath);

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }
}

/// <summary>
/// Reads strong process generation identity. Returns null when the process is
/// gone or its identity cannot be read; callers must treat null as "unproven",
/// never as "absent".
/// </summary>
public interface IHostProcessIdentityProbe
{
    ValueTask<HostProcessIdentity?> TryReadAsync(
        int processId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The single seam through which a host generation may be launched.
/// <para>
/// There is exactly one production implementation and it belongs to the
/// Worker path: a launch worker is spawned with the existing authenticated
/// named-pipe handshake used by
/// <c>WorkerDiscoveryClient.DiscoverAsync</c>, the worker performs class
/// activation on its own STA thread, and it reports a
/// <see cref="HostLaunchObservation"/> back over that same pipe.
/// </para>
/// <para>
/// The supervisor must never <c>Process.Start</c> an Adobe host itself. A
/// second process-launch path would bypass STA ownership, the broker
/// authentication handshake and the worker's teardown discipline, and would
/// make launch provenance unobservable.
/// </para>
/// </summary>
public interface IHostLaunchExecutor
{
    /// <summary>Host family this executor can launch.</summary>
    string Host { get; }

    ValueTask<HostLaunchObservation> LaunchAsync(
        HostLaunchSpec spec,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Preexisting-generation discovery. The production implementation delegates
/// to the existing <c>WorkerDiscoveryClient</c>, so launch reuses the
/// authoritative discovery path instead of inventing a second one.
/// </summary>
public interface IHostTargetDiscovery
{
    ValueTask<IReadOnlyList<HostTargetDescriptor>> DiscoverAsync(
        string host,
        CancellationToken cancellationToken = default);
}

