namespace ComTool.Migration.Parity.Tests;

/// <summary>
/// The live runtime catalog is the authority for whether a named production
/// operation exists. These gates fail when a mapping declares an implemented
/// or partial capability whose production operation is not registered, and
/// when a mapping calls a capability missing while the operation is already
/// registered.
/// </summary>
public sealed class ParityCatalogPresenceTests
{
    [Fact]
    public void EveryProductionOperationNamedByImplementedMappingIsInTheLiveCatalog()
    {
        var absent = new List<string>();

        foreach (var mapping in Parity.MappingsIn(ParityStates.Implemented))
        {
            foreach (var operation in mapping.ProductionOperations)
            {
                if (!Parity.LiveCatalog.Contains(operation.Name))
                    absent.Add($"{mapping.LegacySurface} -> {operation.Name}");
            }
        }

        Assert.True(
            absent.Count == 0,
            "Implemented mappings naming operations that are not " +
            $"registered: {string.Join("; ", absent)}");
    }

    [Fact]
    public void EveryProductionOperationNamedByAnyMappingIsInTheLiveCatalog()
    {
        var absent = new List<string>();

        foreach (var mapping in Parity.Manifest.Mappings)
        {
            foreach (var operation in mapping.ProductionOperations)
            {
                if (!Parity.LiveCatalog.Contains(operation.Name))
                    absent.Add($"{mapping.LegacySurface} -> {operation.Name}");
            }
        }

        Assert.True(
            absent.Count == 0,
            "Declared production operations that are not registered: " +
            $"{string.Join("; ", absent)}");
    }

    [Fact]
    public void MissingMappingsNameNoRegisteredOperation()
    {
        foreach (var mapping in Parity.MappingsIn(ParityStates.Missing))
        {
            foreach (var operation in mapping.OperationLikeReferences)
            {
                Assert.False(
                    Parity.LiveCatalog.Contains(operation.Name),
                    $"'{mapping.LegacySurface}' is marked missing but " +
                    $"'{operation.Name}' is registered in the live catalog. " +
                    "Promote the state or fix the manifest.");
            }
        }
    }

    [Fact]
    public void ProductionOperationNamesAreRealOperationIdentifiers()
    {
        foreach (var mapping in Parity.Manifest.Mappings)
        {
            foreach (var operation in mapping.ProductionOperations)
            {
                Assert.Matches(@"^[a-z][a-z0-9]*(\.[a-zA-Z][a-zA-Z0-9]*)+$", operation.Name);
                Assert.DoesNotContain(' ', operation.Name);
            }
        }
    }

    [Fact]
    public void TheCatalogEnforcementRuleIsLimitedToOperationLikeKinds()
    {
        // Guards the rule itself: architecture references are never catalog
        // gated, so a facet or policy name must not start being required.
        var architectureNames = Parity.Manifest.Mappings
            .SelectMany(mapping => mapping.V2Operations)
            .Where(operation =>
                !V2OperationKinds.IsOperationLike(operation.Kind))
            .Select(operation => operation.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(architectureNames);

        foreach (var mapping in Parity.Manifest.Mappings)
        {
            foreach (var operation in mapping.ProductionOperations)
                Assert.DoesNotContain(operation.Name, architectureNames);
        }
    }
}
