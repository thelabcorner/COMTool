namespace ComTool.Migration.Parity.Tests;

/// <summary>
/// Conformance evidence is verified, not trusted. A mapping that claims a
/// capability is implemented must point at a file that exists, and a test
/// reference must point at a symbol that is still present in that file. Test
/// counts are never asserted, because counts drift for legitimate reasons.
/// </summary>
public sealed class ParityEvidenceTests
{
    [Fact]
    public void EveryEvidenceReferenceResolvesToAFileInsideTheRepository()
    {
        var missing = new List<string>();

        foreach (var mapping in Parity.Manifest.Mappings)
        {
            foreach (var reference in mapping.Evidence)
            {
                var normalized = reference.Path.Replace('\\', '/');
                if (normalized.StartsWith('/') || normalized.Contains(".."))
                {
                    missing.Add(
                        $"{mapping.LegacySurface}: escaping path " +
                        $"'{reference.Path}'");
                    continue;
                }

                var full = Path.Combine(
                    Parity.Root,
                    reference.Path.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(full))
                {
                    missing.Add(
                        $"{mapping.LegacySurface}: '{reference.Path}' " +
                        "does not exist");
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            "Unresolvable evidence: " + string.Join("; ", missing));
    }

    [Fact]
    public void EverySymbolBearingEvidenceReferenceStillResolves()
    {
        var stale = new List<string>();

        foreach (var mapping in Parity.Manifest.Mappings)
        {
            foreach (var reference in mapping.Evidence.Where(reference =>
                         !string.IsNullOrWhiteSpace(reference.Symbol)))
            {
                var full = Path.Combine(
                    Parity.Root,
                    reference.Path.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(full))
                    continue;

                var content = File.ReadAllText(full);
                if (!content.Contains(reference.Symbol!, StringComparison.Ordinal))
                {
                    stale.Add(
                        $"{mapping.LegacySurface}: symbol " +
                        $"'{reference.Symbol}' not found in " +
                        $"'{reference.Path}'");
                }
            }
        }

        Assert.True(
            stale.Count == 0,
            "Evidence whose symbol was renamed or removed: " +
            string.Join("; ", stale));
    }

    [Fact]
    public void ImplementedMappingsCiteAtLeastOneTestEvidenceReference()
    {
        foreach (var mapping in Parity.MappingsIn(ParityStates.Implemented))
        {
            Assert.Contains(
                mapping.Evidence,
                reference => string.Equals(
                    reference.Kind,
                    "test",
                    StringComparison.Ordinal));
        }
    }

    [Fact]
    public void EvidenceKindsAreKnown()
    {
        foreach (var mapping in Parity.Manifest.Mappings)
        {
            foreach (var reference in mapping.Evidence)
            {
                Assert.Contains(
                    reference.Kind,
                    new[] { "test", "evidence", "document", "tool" });
            }
        }
    }
}
