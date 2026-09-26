using System.Collections.Frozen;
using ComTool.Hosts.Abstractions;

namespace ComTool.Runtime;

public sealed class TargetRegistry
{
    private readonly FrozenDictionary<string, IHostAdapter> _adapters;

    public TargetRegistry(IEnumerable<IHostAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        var map = new Dictionary<string, IHostAdapter>(StringComparer.Ordinal);
        foreach (var adapter in adapters)
        {
            if (!map.TryAdd(adapter.Host, adapter))
                throw new ArgumentException(
                    $"Duplicate host adapter '{adapter.Host}'.",
                    nameof(adapters));
        }

        _adapters = map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public IReadOnlyCollection<string> Hosts => _adapters.Keys;

    public async ValueTask<IReadOnlyList<HostTargetDescriptor>> DiscoverAllAsync(
        CancellationToken cancellationToken = default)
    {
        var targets = new List<HostTargetDescriptor>();

        // Deliberately sequential. Some host adapters (notably COM-backed
        // adapters) require discovery/attachment on the caller's STA thread.
        // Parallel discovery can be introduced only for adapters that
        // explicitly advertise thread-safe discovery.
        foreach (var adapter in _adapters.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var discovered = await adapter
                .DiscoverAsync(cancellationToken)
                .ConfigureAwait(true);
            targets.AddRange(discovered);
        }

        return targets;
    }

    public IHostAdapter GetAdapter(string host) =>
        _adapters.TryGetValue(host, out var adapter)
            ? adapter
            : throw new KeyNotFoundException(
                $"No host adapter registered for '{host}'.");
}
