using System.Collections.Frozen;

namespace ComTool.Runtime;

public sealed class OperationCatalog
{
    private readonly FrozenDictionary<string, OperationDefinition> _definitions;

    public OperationCatalog(IEnumerable<OperationDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var map = new Dictionary<string, OperationDefinition>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.Name))
                throw new ArgumentException("Operation names must be non-empty.", nameof(definitions));

            if (!map.TryAdd(definition.Name, definition))
                throw new ArgumentException($"Duplicate operation '{definition.Name}'.", nameof(definitions));
        }

        _definitions = map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public int Count => _definitions.Count;

    public bool TryGet(string name, out OperationDefinition definition) =>
        _definitions.TryGetValue(name, out definition!);

    public OperationDefinition GetRequired(string name) =>
        _definitions.TryGetValue(name, out var definition)
            ? definition
            : throw new KeyNotFoundException($"Unknown operation '{name}'.");

    public IReadOnlyCollection<OperationDefinition> Definitions => _definitions.Values;
}
