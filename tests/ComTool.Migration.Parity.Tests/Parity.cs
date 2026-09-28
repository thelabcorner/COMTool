using ComTool.Migration.Parity;
using ComTool.Runtime;

namespace ComTool.Migration.Parity.Tests;

/// <summary>
/// Shared fixture for the parity gates. The analysis is computed once against
/// the live runtime operation catalog, so these tests fail whenever a declared
/// implemented mapping loses its registered operation or its evidence.
/// </summary>
internal static class Parity
{
    public static string Root => V2RootLocator.Locate();

    public static IReadOnlySet<string> LiveCatalog =>
        BuiltInOperations.Catalog
            .Definitions
            .Select(definition => definition.Name)
            .ToHashSet(StringComparer.Ordinal);

    public static ParityAnalysis Analysis { get; } = ParityAnalyzer.Run(
        Root,
        LiveCatalog,
        "ComTool.Runtime.BuiltInOperations.Catalog");

    public static ParityInputs.Matrix Matrix => Analysis.Matrix;

    public static ParityInputs.Manifest Manifest => Analysis.Manifest;

    public static ParityInputs.FixtureSet Fixtures => Analysis.Fixtures;

    public static IEnumerable<Finding> Errors => Analysis.Errors;

    public static IEnumerable<Mapping> MappingsIn(string state) =>
        Analysis.Manifest.Mappings.Where(mapping =>
            string.Equals(mapping.State, state, StringComparison.Ordinal));

    public static string DescribeErrors() =>
        Analysis.Errors.Count == 0
            ? "(none)"
            : string.Join(
                Environment.NewLine,
                Analysis.Errors.Select(finding =>
                    $"{finding.Code} [{finding.Subject}] {finding.Message}"));
}
