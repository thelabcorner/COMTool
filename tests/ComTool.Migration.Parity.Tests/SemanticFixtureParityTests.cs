namespace ComTool.Migration.Parity.Tests;

/// <summary>
/// The semantic fixtures are executable comparisons, not documentation. Every
/// case must project both sides onto the nine canonical fields and account for
/// every one of them, either as an observed equivalence or as an explicitly
/// declared divergence with a stated reason.
/// </summary>
public sealed class SemanticFixtureParityTests
{
    [Fact]
    public void EveryFixtureSurfaceIsAKnownLegacySurface()
    {
        foreach (var entry in Parity.Fixtures.Cases)
        {
            Assert.True(
                Parity.Matrix.BySurface.ContainsKey(entry.Surface),
                $"Fixture '{entry.Name}' targets unknown surface " +
                $"'{entry.Surface}'.");
        }
    }

    [Fact]
    public void EveryManifestFixtureReferenceResolvesToACase()
    {
        var unresolved = new List<string>();

        foreach (var mapping in Parity.Manifest.Mappings)
        {
            foreach (var reference in mapping.SemanticFixtures)
            {
                if (!Parity.Fixtures.ByName.ContainsKey(reference))
                    unresolved.Add($"{mapping.LegacySurface} -> {reference}");
            }
        }

        Assert.True(
            unresolved.Count == 0,
            "Unresolved semantic fixture references: " +
            string.Join("; ", unresolved));
    }

    [Fact]
    public void EveryCaseIsLinkedFromItsSurfaceMapping()
    {
        foreach (var entry in Parity.Fixtures.Cases)
        {
            var mapping = Parity.Manifest.Find(entry.Surface);
            Assert.NotNull(mapping);
            Assert.Contains(entry.Name, mapping!.SemanticFixtures);
        }
    }

    [Fact]
    public void EveryCaseProjectsTheSameCanonicalFieldSetOnBothSides()
    {
        foreach (var entry in Parity.Fixtures.Cases)
        {
            var v1 = SemanticProjection.FromLegacy(entry.V1Envelope);
            var v2 = SemanticProjection.FromV2(entry.V2Request, entry.V2Result);

            Assert.Equal(SemanticProjection.Fields.Length, v1.Count);
            Assert.Equal(SemanticProjection.Fields.Length, v2.Count);
        }
    }

    [Fact]
    public void EveryProjectionFieldIsAccountedForInEveryCase()
    {
        var unaccounted = new List<string>();

        foreach (var entry in Parity.Fixtures.Cases)
        {
            var comparison = Compare(entry);
            foreach (var field in comparison.UnaccountedFields)
                unaccounted.Add($"{entry.Name}: {field}");
        }

        Assert.True(
            unaccounted.Count == 0,
            "Projection fields neither declared equivalent nor divergent: " +
            string.Join("; ", unaccounted));
    }

    [Fact]
    public void EveryDeclaredEquivalenceHolds()
    {
        var broken = new List<string>();

        foreach (var entry in Parity.Fixtures.Cases)
        {
            foreach (var violation in Compare(entry).EquivalenceViolations)
                broken.Add($"{entry.Name}: {violation}");
        }

        Assert.True(
            broken.Count == 0,
            "Declared equivalences that do not hold: " +
            string.Join("; ", broken));
    }

    [Fact]
    public void EveryDeclaredDivergenceMatchesTheObservedPair()
    {
        var stale = new List<string>();

        foreach (var entry in Parity.Fixtures.Cases)
        {
            foreach (var violation in Compare(entry).DivergenceViolations)
                stale.Add($"{entry.Name}: {violation}");
        }

        Assert.True(
            stale.Count == 0,
            "Stale or redundant divergence declarations: " +
            string.Join("; ", stale));
    }

    [Fact]
    public void NoCaseDeclaresAFieldBothEquivalentAndDivergent()
    {
        foreach (var entry in Parity.Fixtures.Cases)
        {
            Assert.Empty(Compare(entry).Conflicts);
        }
    }

    [Fact]
    public void EveryCapabilityIncompatibleCaseDeclaresALostProperty()
    {
        foreach (var entry in Parity.Fixtures.Cases.Where(entry =>
                     !entry.CapabilityEquivalent))
        {
            Assert.NotEmpty(entry.Divergences);
            Assert.NotEmpty(entry.IntentionalIncompatibilities);
        }
    }

    [Fact]
    public void EveryFixtureV2OperationIsRegisteredAndIsProductionForItsSurface()
    {
        foreach (var entry in Parity.Fixtures.Cases)
        {
            Assert.True(
                Parity.LiveCatalog.Contains(entry.V2Operation!),
                $"Fixture '{entry.Name}' pins unregistered operation " +
                $"'{entry.V2Operation}'.");

            var mapping = Parity.Manifest.Find(entry.Surface);
            Assert.NotNull(mapping);
            Assert.Contains(
                mapping!.ProductionOperations.Select(operation => operation.Name),
                name => string.Equals(
                    name,
                    entry.V2Operation,
                    StringComparison.Ordinal));
        }
    }

    [Fact]
    public void EveryCaseDeclaresItsProvenance()
    {
        foreach (var entry in Parity.Fixtures.Cases)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.V1Provenance));
            Assert.False(string.IsNullOrWhiteSpace(entry.V2Provenance));
        }
    }

    private static SemanticProjection.Comparison Compare(SemanticCase entry) =>
        SemanticProjection.Compare(
            SemanticProjection.FromLegacy(entry.V1Envelope),
            SemanticProjection.FromV2(entry.V2Request, entry.V2Result),
            entry.Equivalence,
            entry.Divergences);
}
