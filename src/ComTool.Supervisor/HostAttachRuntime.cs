using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Supervisor;

public sealed class HostAttachException(
    string kind,
    string message,
    bool retryable = false,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Kind { get; } = kind;
    public bool Retryable { get; } = retryable;
}

/// <summary>
/// Runtime policy for explicitly binding one caller-selected strong target
/// generation. Discovery remains authoritative; attach never launches a host,
/// acquires ownership, or creates identity from caller-supplied process data.
/// </summary>
public sealed class HostAttachRuntime
{
    private readonly IReadOnlyCollection<string> _configuredHosts;
    private readonly IHostTargetDiscovery _discovery;

    public HostAttachRuntime(
        IReadOnlyCollection<string> configuredHosts,
        IHostTargetDiscovery discovery)
    {
        ArgumentNullException.ThrowIfNull(configuredHosts);
        ArgumentNullException.ThrowIfNull(discovery);

        _configuredHosts = configuredHosts;
        _discovery = discovery;
    }

    public async ValueTask<HostTargetDescriptor> AttachAsync(
        TargetRef target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (string.IsNullOrWhiteSpace(target.Host) ||
            !_configuredHosts.Contains(
                target.Host,
                StringComparer.OrdinalIgnoreCase))
        {
            throw new HostAttachException(
                "host_family_not_configured",
                $"Host family '{target.Host}' is not configured.");
        }

        if (string.IsNullOrWhiteSpace(target.Id) ||
            target.Id.Length > 512)
        {
            throw new HostAttachException(
                "target_id_invalid",
                "Attach requires a non-empty target id of at most 512 characters.");
        }

        IReadOnlyList<HostTargetDescriptor> discovered;
        try
        {
            discovered = await _discovery
                .DiscoverAsync(target.Host, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new HostAttachException(
                "target_discovery_failed",
                $"Could not rediscover host family '{target.Host}': " +
                ex.Message,
                retryable: true,
                ex);
        }

        var matches = discovered
            .Where(candidate =>
                string.Equals(
                    candidate.Identity.Host,
                    target.Host,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    candidate.Identity.TargetId,
                    target.Id,
                    StringComparison.Ordinal))
            .ToArray();

        if (matches.Length == 0)
        {
            throw new HostAttachException(
                "target_generation_not_found",
                $"The exact target generation '{target.Id}' is not currently " +
                "discoverable. Attach never substitutes another generation.");
        }

        if (matches.Length != 1)
        {
            throw new HostAttachException(
                "target_generation_ambiguous",
                $"Discovery returned {matches.Length} descriptors for exact " +
                $"target generation '{target.Id}'.",
                retryable: false);
        }

        return matches[0];
    }
}
