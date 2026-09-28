namespace ComTool.Migration.Parity.Tests;

/// <summary>
/// The parity manifest must stay a complete and honest restatement of the
/// legacy matrix. These gates fail when a surface is unmapped, when a
/// classification drifts, or when a state/disposition pair claims something
/// the vocabulary does not allow.
/// </summary>
public sealed class ParityManifestCoverageTests
{
    [Fact]
    public void EveryMatrixSurfaceHasExactlyOneParityMapping()
    {
        var duplicates = Parity.Manifest.Mappings
            .GroupBy(mapping => mapping.LegacySurface, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} x{group.Count()}")
            .ToArray();

        Assert.Empty(duplicates);

        var unmapped = Parity.Matrix.Features
            .Select(feature => feature.LegacySurface)
            .Where(surface => Parity.Manifest.Find(surface) is null)
            .ToArray();

        Assert.True(
            unmapped.Length == 0,
            $"Legacy surfaces with no parity mapping: {string.Join(", ", unmapped)}");
    }

    [Fact]
    public void EveryMappingResolvesToALegacyMatrixSurface()
    {
        var orphaned = Parity.Manifest.Mappings
            .Where(mapping => !Parity.Matrix.BySurface.ContainsKey(
                mapping.LegacySurface))
            .Select(mapping => mapping.LegacySurface)
            .ToArray();

        Assert.True(
            orphaned.Length == 0,
            $"Parity mappings with no legacy surface: {string.Join(", ", orphaned)}");
    }

    [Fact]
    public void MatrixClassificationIsCopiedVerbatim()
    {
        var drifted = new List<string>();

        foreach (var mapping in Parity.Manifest.Mappings)
        {
            if (!Parity.Matrix.BySurface.TryGetValue(
                    mapping.LegacySurface,
                    out var classification))
            {
                continue;
            }

            if (!string.Equals(
                    mapping.MatrixClassification,
                    classification,
                    StringComparison.Ordinal))
            {
                drifted.Add(
                    $"{mapping.LegacySurface}: manifest=" +
                    $"{mapping.MatrixClassification} matrix={classification}");
            }
        }

        Assert.True(
            drifted.Count == 0,
            $"Classification drift: {string.Join("; ", drifted)}");
    }

    [Fact]
    public void EveryMappingUsesTheDeclaredStateAndDispositionVocabulary()
    {
        foreach (var mapping in Parity.Manifest.Mappings)
        {
            Assert.Contains(
                mapping.State,
                ParityStates.All);
            Assert.Contains(
                mapping.CapabilityDisposition,
                CapabilityDispositions.All);
        }
    }

    [Fact]
    public void IntentionalDropRequiresCapabilityToBeExplicitlyNotRetained()
    {
        foreach (var mapping in Parity.MappingsIn(ParityStates.IntentionalDrop))
        {
            Assert.Equal(
                CapabilityDispositions.IntentionallyNotRetained,
                mapping.CapabilityDisposition);
            Assert.NotEmpty(mapping.IntentionalIncompatibilities);
        }
    }

    [Fact]
    public void NotRetainedCapabilityOnlyPairsWithSupersededOrDrop()
    {
        foreach (var mapping in Parity.Manifest.Mappings.Where(mapping =>
                     string.Equals(
                         mapping.CapabilityDisposition,
                         CapabilityDispositions.IntentionallyNotRetained,
                         StringComparison.Ordinal)))
        {
            Assert.Contains(
                mapping.State,
                CapabilityDispositions.NotRetainedStates);
        }
    }

    [Fact]
    public void ImplementedMappingsNameAProductionOperationAndEvidence()
    {
        foreach (var mapping in Parity.MappingsIn(ParityStates.Implemented))
        {
            if (mapping.OperationLikeReferences.Any())
                Assert.NotEmpty(mapping.ProductionOperations);

            Assert.NotEmpty(mapping.Evidence);
        }
    }

    [Fact]
    public void PartialAndMissingMappingsDeclareWhatIsMissing()
    {
        foreach (var mapping in Parity.Manifest.Mappings.Where(mapping =>
                     mapping.State is ParityStates.Partial or ParityStates.Missing))
        {
            Assert.NotEmpty(mapping.MissingAspects);
        }
    }

    [Fact]
    public void ArchitectureReferencesAreNeverMarkedProduction()
    {
        foreach (var mapping in Parity.Manifest.Mappings)
        {
            foreach (var operation in mapping.V2Operations)
            {
                Assert.Contains(operation.Kind, V2OperationKinds.All);

                if (!V2OperationKinds.IsOperationLike(operation.Kind))
                    Assert.False(
                        operation.Production,
                        $"{mapping.LegacySurface}: architecture reference " +
                        $"'{operation.Name}' ({operation.Kind}) cannot be " +
                        "production.");
            }
        }
    }

    [Fact]
    public void TheWholeParityAnalysisIsFreeOfErrorFindings()
    {
        Assert.True(
            Parity.Analysis.IsClean,
            Parity.DescribeErrors());
    }
}
