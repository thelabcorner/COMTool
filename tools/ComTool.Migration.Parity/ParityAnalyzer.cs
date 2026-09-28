using System.Text.Json.Nodes;

namespace ComTool.Migration.Parity;

internal sealed record CaseOutcome(
    string Name,
    string Surface,
    bool CapabilityEquivalent,
    bool Passed,
    IReadOnlyList<string> Observed,
    IReadOnlyList<string> Problems);

internal sealed class ParityAnalysis
{
    public required ParityInputs.Matrix Matrix { get; init; }
    public required ParityInputs.Manifest Manifest { get; init; }
    public required ParityInputs.FixtureSet Fixtures { get; init; }
    public required ParityInputs.Registry Registry { get; init; }
    public required IReadOnlySet<string> CatalogOperations { get; init; }
    public required string CatalogAuthority { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }
    public required IReadOnlyList<CaseOutcome> Cases { get; init; }

    public IReadOnlyList<Finding> Errors =>
        Findings.Where(finding => finding.IsError).ToArray();

    public IReadOnlyList<Finding> Advisories =>
        Findings.Where(finding => !finding.IsError).ToArray();

    public bool IsClean => Errors.Count == 0;

    public IReadOnlyDictionary<string, int> StateCounts =>
        Manifest.Mappings
            .GroupBy(mapping => mapping.State, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Count(),
                StringComparer.Ordinal);

    public IReadOnlyDictionary<string, int> DispositionCounts =>
        Manifest.Mappings
            .GroupBy(
                mapping => mapping.CapabilityDisposition,
                StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Count(),
                StringComparer.Ordinal);
}

/// <summary>
/// Compares the legacy feature matrix, the lane-owned parity manifest, the
/// semantic fixtures, the checked-in registry snapshot, and the live runtime
/// operation catalog. The live catalog is the authority for whether a named
/// production operation actually exists.
/// </summary>
internal static class ParityAnalyzer
{
    public static ParityAnalysis Run(
        string root,
        IReadOnlySet<string> catalogOperations,
        string catalogAuthority)
    {
        var matrix = ParityInputs.ReadMatrix(root);
        var manifest = ParityInputs.ReadManifest(root);
        var fixtures = ParityInputs.ReadFixtures(root);
        var registry = ParityInputs.ReadRegistry(root);

        var findings = new List<Finding>();
        var cases = new List<CaseOutcome>();
        var contents = new Dictionary<string, string?>(StringComparer.Ordinal);

        void Add(string code, string severity, string subject, string message) =>
            findings.Add(new Finding(code, severity, subject, message));

        foreach (var group in manifest.Mappings
                     .GroupBy(mapping => mapping.LegacySurface, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            Add(
                "PARITY-MAP-DUPLICATE",
                Finding.SeverityError,
                group.Key,
                $"The manifest declares '{group.Key}' {group.Count()} times.");
        }

        foreach (var feature in matrix.Features)
        {
            if (manifest.Find(feature.LegacySurface) is not { } mapping)
            {
                Add(
                    "PARITY-MAP-MISSING",
                    Finding.SeverityError,
                    feature.LegacySurface,
                    "The matrix declares this surface but the parity manifest " +
                    "has no mapping for it.");
                continue;
            }

            CheckMapping(
                mapping,
                feature.Classification,
                catalogOperations,
                registry,
                fixtures,
                contents,
                Add);
        }

        foreach (var mapping in manifest.Mappings)
        {
            if (!matrix.BySurface.ContainsKey(mapping.LegacySurface))
            {
                Add(
                    "PARITY-MAP-ORPHAN",
                    Finding.SeverityError,
                    mapping.LegacySurface,
                    "The manifest maps a surface the legacy matrix does not " +
                    "declare.");
            }
        }

        foreach (var group in fixtures.Cases
                     .GroupBy(entry => entry.Name, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            Add(
                "PARITY-FIXTURE-DUPLICATE",
                Finding.SeverityError,
                group.Key,
                $"The fixture set declares '{group.Key}' {group.Count()} times.");
        }

        foreach (var entry in fixtures.Cases)
        {
            cases.Add(CheckCase(
                entry,
                manifest,
                matrix,
                catalogOperations,
                Add));
        }

        foreach (var mapping in manifest.Mappings)
        {
            foreach (var reference in mapping.SemanticFixtures)
            {
                if (!fixtures.ByName.ContainsKey(reference))
                {
                    Add(
                        "PARITY-FIXTURE-UNKNOWN",
                        Finding.SeverityError,
                        mapping.LegacySurface,
                        $"The manifest references semantic fixture " +
                        $"'{reference}', which the fixture set does not define.");
                }
            }
        }

        return new ParityAnalysis
        {
            Matrix = matrix,
            Manifest = manifest,
            Fixtures = fixtures,
            Registry = registry,
            CatalogOperations = catalogOperations,
            CatalogAuthority = catalogAuthority,
            Findings = findings,
            Cases = cases
        };
    }

    private static void CheckMapping(
        Mapping mapping,
        string matrixClassification,
        IReadOnlySet<string> catalogOperations,
        ParityInputs.Registry registry,
        ParityInputs.FixtureSet fixtures,
        Dictionary<string, string?> contents,
        Action<string, string, string, string> add)
    {
        var subject = mapping.LegacySurface;

        if (!string.Equals(
                mapping.MatrixClassification,
                matrixClassification,
                StringComparison.Ordinal))
        {
            add(
                "PARITY-CLASS-MISMATCH",
                Finding.SeverityError,
                subject,
                $"The manifest says '{mapping.MatrixClassification}' but the " +
                $"matrix says '{matrixClassification}'.");
        }

        if (!ParityStates.All.Contains(mapping.State, StringComparer.Ordinal))
        {
            add(
                "PARITY-STATE-UNKNOWN",
                Finding.SeverityError,
                subject,
                $"'{mapping.State}' is not one of " +
                $"{string.Join(", ", ParityStates.All)}.");
        }

        if (!CapabilityDispositions.All.Contains(
                mapping.CapabilityDisposition,
                StringComparer.Ordinal))
        {
            add(
                "PARITY-DISPOSITION-UNKNOWN",
                Finding.SeverityError,
                subject,
                $"'{mapping.CapabilityDisposition}' is not one of " +
                $"{string.Join(", ", CapabilityDispositions.All)}.");
        }

        if (mapping.State == ParityStates.IntentionalDrop &&
            mapping.CapabilityDisposition !=
                CapabilityDispositions.IntentionallyNotRetained)
        {
            add(
                "PARITY-STATE-DISPOSITION",
                Finding.SeverityError,
                subject,
                "An intentional_drop must declare capabilityDisposition " +
                $"{CapabilityDispositions.IntentionallyNotRetained}.");
        }

        if (mapping.CapabilityDisposition ==
                CapabilityDispositions.IntentionallyNotRetained &&
            !CapabilityDispositions.NotRetainedStates.Contains(
                mapping.State,
                StringComparer.Ordinal))
        {
            add(
                "PARITY-STATE-DISPOSITION",
                Finding.SeverityError,
                subject,
                "capabilityDisposition " +
                $"{CapabilityDispositions.IntentionallyNotRetained} is only " +
                $"valid for state {ParityStates.Superseded} or " +
                $"{ParityStates.IntentionalDrop}.");
        }

        if (mapping.State == ParityStates.Implemented &&
            mapping.OperationLikeReferences.Any() &&
            !mapping.ProductionOperations.Any())
        {
            add(
                "PARITY-IMPLEMENTED-WITHOUT-OPERATION",
                Finding.SeverityError,
                subject,
                "State implemented must name at least one production " +
                "V2 operation.");
        }

        if (mapping.State == ParityStates.Implemented &&
            mapping.Evidence.Count == 0)
        {
            add(
                "PARITY-IMPLEMENTED-WITHOUT-EVIDENCE",
                Finding.SeverityError,
                subject,
                "State implemented must cite conformance test evidence.");
        }

        if (mapping.State is ParityStates.Partial or ParityStates.Missing &&
            mapping.MissingAspects.Count == 0)
        {
            add(
                "PARITY-GAP-UNDECLARED",
                Finding.SeverityError,
                subject,
                $"State {mapping.State} must list what is missing in " +
                "missingAspects.");
        }

        foreach (var operation in mapping.V2Operations)
        {
            if (!V2OperationKinds.All.Contains(operation.Kind, StringComparer.Ordinal))
            {
                add(
                    "PARITY-KIND-UNKNOWN",
                    Finding.SeverityError,
                    subject,
                    $"'{operation.Kind}' is not one of " +
                    $"{string.Join(", ", V2OperationKinds.All)}.");
            }

            if (!V2OperationKinds.IsOperationLike(operation.Kind) &&
                operation.Production)
            {
                add(
                    "PARITY-KIND-UNKNOWN",
                    Finding.SeverityError,
                    subject,
                    $"'{operation.Name}' is architecture " +
                    $"({operation.Kind}) and cannot be marked production.");
            }

            if (!V2OperationKinds.IsOperationLike(operation.Kind))
                continue;

            var inCatalog = catalogOperations.Contains(operation.Name);
            var inRegistry = registry.OperationNames.Contains(operation.Name);

            if (operation.Production && !inCatalog)
            {
                add(
                    "PARITY-OP-NOT-IN-CATALOG",
                    Finding.SeverityError,
                    subject,
                    $"Declared production operation '{operation.Name}' is " +
                    "absent from the live operation catalog.");
            }

            if (operation.Production && !inRegistry)
            {
                add(
                    "PARITY-OP-NOT-IN-REGISTRY",
                    Finding.SeverityAdvisory,
                    subject,
                    $"Production operation '{operation.Name}' is in the live " +
                    "catalog but missing from " +
                    $"{ParityInputs.RegistryRelativePath}.");
            }

            if (mapping.State == ParityStates.Missing && inCatalog)
            {
                add(
                    "PARITY-MISSING-BUT-REGISTERED",
                    Finding.SeverityError,
                    subject,
                    $"State is missing but '{operation.Name}' is already in " +
                    "the live operation catalog. Promote the state or correct " +
                    "the manifest.");
            }
        }

        foreach (var reference in mapping.Evidence)
        {
            CheckEvidence(mapping, reference, contents, add);
        }

        _ = fixtures;
    }

    private static void CheckEvidence(
        Mapping mapping,
        EvidenceRef reference,
        Dictionary<string, string?> contents,
        Action<string, string, string, string> add)
    {
        var normalized = reference.Path.Replace('\\', '/');
        if (normalized.StartsWith('/') ||
            normalized.Contains("..", StringComparison.Ordinal))
        {
            add(
                "PARITY-EVIDENCE-PATH-ESCAPE",
                Finding.SeverityError,
                mapping.LegacySurface,
                $"Evidence path '{reference.Path}' must be repository-relative " +
                "and must not escape the V2 root.");
            return;
        }

        if (!contents.TryGetValue(reference.Path, out var content))
        {
            var full = Path.Combine(
                V2RootLocator.Root,
                reference.Path.Replace('/', Path.DirectorySeparatorChar));

            content = File.Exists(full) ? File.ReadAllText(full) : null;
            contents[reference.Path] = content;
        }

        if (content is null)
        {
            add(
                "PARITY-EVIDENCE-FILE-MISSING",
                Finding.SeverityError,
                mapping.LegacySurface,
                $"Evidence path '{reference.Path}' does not exist.");
            return;
        }

        if (!string.Equals(reference.Kind, "test", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(reference.Symbol))
        {
            return;
        }

        if (!content.Contains(reference.Symbol, StringComparison.Ordinal))
        {
            add(
                "PARITY-EVIDENCE-SYMBOL-MISSING",
                Finding.SeverityError,
                mapping.LegacySurface,
                $"Test evidence '{reference.Path}' does not contain symbol " +
                $"'{reference.Symbol}'. Renaming or deleting the test must " +
                "fail this gate.");
        }
    }

    private static CaseOutcome CheckCase(
        SemanticCase entry,
        ParityInputs.Manifest manifest,
        ParityInputs.Matrix matrix,
        IReadOnlySet<string> catalogOperations,
        Action<string, string, string, string> add)
    {
        var problems = new List<string>();

        if (!matrix.BySurface.ContainsKey(entry.Surface))
        {
            add(
                "PARITY-FIXTURE-UNKNOWN-SURFACE",
                Finding.SeverityError,
                entry.Name,
                $"'{entry.Surface}' is not a legacy surface declared by the " +
                "migration matrix.");
        }

        var mapping = manifest.Find(entry.Surface);
        var v2Operation = entry.V2Operation ?? string.Empty;

        if (!catalogOperations.Contains(v2Operation))
        {
            add(
                "PARITY-FIXTURE-OP-NOT-IN-CATALOG",
                Finding.SeverityError,
                entry.Name,
                $"The fixture pins V2 operation '{v2Operation}', which is " +
                "absent from the live operation catalog.");
        }

        if (mapping is null ||
            !mapping.ProductionOperations.Any(operation =>
                string.Equals(
                    operation.Name,
                    v2Operation,
                    StringComparison.Ordinal)))
        {
            add(
                "PARITY-FIXTURE-OP-NOT-IN-MAPPING",
                Finding.SeverityError,
                entry.Name,
                $"The fixture pins V2 operation '{v2Operation}', which the " +
                $"manifest does not declare as production for surface " +
                $"'{entry.Surface}'.");
        }

        if (!mapping?.SemanticFixtures.Contains(entry.Name) ?? true)
        {
            add(
                "PARITY-FIXTURE-UNLINKED",
                Finding.SeverityError,
                entry.Name,
                $"The manifest mapping for '{entry.Surface}' does not " +
                $"reference fixture '{entry.Name}'.");
        }

        if (entry.V1RequestPresent)
        {
            add(
                "PARITY-FIXTURE-V1-REQUEST-PRESENT",
                Finding.SeverityAdvisory,
                entry.Name,
                "The legacy side of this fixture carries a request object. " +
                "The legacy tool has no request envelope, so targetIdentity " +
                "will not project to absent.");
        }

        if (!entry.CapabilityEquivalent &&
            entry.Divergences.Count == 0)
        {
            add(
                "PARITY-FIXTURE-UNEQUIVALENT-UNDECLARED",
                Finding.SeverityError,
                entry.Name,
                "capabilityEquivalent is false but the case declares no " +
                "divergence, so it documents no lost property.");
        }

        if (!entry.CapabilityEquivalent &&
            entry.IntentionalIncompatibilities.Count == 0)
        {
            add(
                "PARITY-FIXTURE-UNEQUIVALENT-UNEXPLAINED",
                Finding.SeverityAdvisory,
                entry.Name,
                "capabilityEquivalent is false but the case records no " +
                "intentionalIncompatibilities rationale.");
        }

        var v1Projection = SemanticProjection.FromLegacy(entry.V1Envelope);
        var v2Projection = SemanticProjection.FromV2(
            entry.V2Request,
            entry.V2Result);

        var comparison = SemanticProjection.Compare(
            v1Projection,
            v2Projection,
            entry.Equivalence,
            entry.Divergences);

        foreach (var violation in comparison.Conflicts)
        {
            problems.Add(violation);
            add(
                "PARITY-FIXTURE-FIELD-CONFLICT",
                Finding.SeverityError,
                entry.Name,
                violation);
        }

        foreach (var violation in comparison.EquivalenceViolations)
        {
            problems.Add(violation);
            add(
                "PARITY-FIXTURE-EQUIVALENCE-BROKEN",
                Finding.SeverityError,
                entry.Name,
                violation);
        }

        foreach (var violation in comparison.DivergenceViolations)
        {
            problems.Add(violation);
            add(
                "PARITY-FIXTURE-DIVERGENCE-STALE",
                Finding.SeverityError,
                entry.Name,
                violation);
        }

        foreach (var violation in comparison.UnaccountedFields)
        {
            problems.Add(violation);
            add(
                "PARITY-FIXTURE-FIELD-UNACCOUNTED",
                Finding.SeverityError,
                entry.Name,
                violation);
        }

        return new CaseOutcome(
            entry.Name,
            entry.Surface,
            entry.CapabilityEquivalent,
            problems.Count == 0,
            comparison.Observed,
            problems);
    }

    public static string ToJson(ParityAnalysis analysis)
    {
        var mappings = new JsonArray();
        foreach (var mapping in analysis.Manifest.Mappings
                     .OrderBy(item => item.LegacySurface, StringComparer.Ordinal))
        {
            var operations = new JsonArray();
            foreach (var operation in mapping.V2Operations)
            {
                operations.Add(new JsonObject
                {
                    ["name"] = operation.Name,
                    ["production"] = operation.Production,
                    ["kind"] = operation.Kind,
                    ["inLiveCatalog"] =
                        analysis.CatalogOperations.Contains(operation.Name)
                });
            }

            var evidence = new JsonArray();
            foreach (var reference in mapping.Evidence)
            {
                evidence.Add(new JsonObject
                {
                    ["kind"] = reference.Kind,
                    ["path"] = reference.Path,
                    ["symbol"] = reference.Symbol
                });
            }

            mappings.Add(new JsonObject
            {
                ["legacySurface"] = mapping.LegacySurface,
                ["matrixClassification"] = mapping.MatrixClassification,
                ["state"] = mapping.State,
                ["capabilityDisposition"] = mapping.CapabilityDisposition,
                ["capability"] = mapping.Capability,
                ["v2Operations"] = operations,
                ["missingAspects"] = new JsonArray(
                    mapping.MissingAspects.Select(item => (JsonNode)item!).ToArray()),
                ["intentionalIncompatibilities"] = new JsonArray(
                    mapping
                        .IntentionalIncompatibilities
                        .Select(item => (JsonNode)item!)
                        .ToArray()),
                ["semanticFixtures"] = new JsonArray(
                    mapping
                        .SemanticFixtures
                        .Select(item => (JsonNode)item!)
                        .ToArray()),
                ["evidence"] = evidence
            });
        }

        var cases = new JsonArray();
        foreach (var outcome in analysis.Cases)
        {
            cases.Add(new JsonObject
            {
                ["name"] = outcome.Name,
                ["surface"] = outcome.Surface,
                ["capabilityEquivalent"] = outcome.CapabilityEquivalent,
                ["passed"] = outcome.Passed,
                ["projection"] = new JsonArray(
                    outcome.Observed.Select(item => (JsonNode)item!).ToArray()),
                ["problems"] = new JsonArray(
                    outcome.Problems.Select(item => (JsonNode)item!).ToArray())
            });
        }

        var findings = new JsonArray();
        foreach (var finding in analysis.Findings
                     .OrderBy(item => item.Code, StringComparer.Ordinal)
                     .ThenBy(item => item.Subject, StringComparer.Ordinal))
        {
            findings.Add(new JsonObject
            {
                ["code"] = finding.Code,
                ["severity"] = finding.Severity,
                ["subject"] = finding.Subject,
                ["message"] = finding.Message
            });
        }

        var stateCounts = new JsonObject();
        foreach (var pair in analysis.StateCounts)
            stateCounts[pair.Key] = pair.Value;

        var dispositionCounts = new JsonObject();
        foreach (var pair in analysis.DispositionCounts)
            dispositionCounts[pair.Key] = pair.Value;

        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["generator"] = "ComTool.Migration.Parity",
            ["inputs"] = new JsonObject
            {
                ["matrix"] = ParityInputs.MatrixRelativePath,
                ["manifest"] = ParityInputs.ManifestRelativePath,
                ["semanticFixtures"] = ParityInputs.FixturesRelativePath,
                ["registrySnapshot"] = ParityInputs.RegistryRelativePath,
                ["catalogAuthority"] = analysis.CatalogAuthority
            },
            ["totals"] = new JsonObject
            {
                ["legacySurfaces"] = analysis.Matrix.Features.Count,
                ["mappings"] = analysis.Manifest.Mappings.Count,
                ["productionOperationsDeclared"] =
                    analysis.Manifest.Mappings
                        .SelectMany(mapping => mapping.ProductionOperations)
                        .Count(),
                ["semanticCases"] = analysis.Fixtures.Cases.Count
            },
            ["byState"] = stateCounts,
            ["byCapabilityDisposition"] = dispositionCounts,
            ["status"] = analysis.IsClean ? "clean" : "findings",
            ["errorCount"] = analysis.Errors.Count,
            ["advisoryCount"] = analysis.Advisories.Count,
            ["mappingsDetail"] = mappings,
            ["semanticParity"] = cases,
            ["findings"] = findings
        };

        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    public static string ToMarkdown(ParityAnalysis analysis)
    {
        var lines = new List<string>
        {
            "# V1 to V2 migration parity report",
            "",
            $"Catalog authority: `{analysis.CatalogAuthority}`",
            "",
            $"- Legacy surfaces in matrix: {analysis.Matrix.Features.Count}",
            $"- Parity mappings: {analysis.Manifest.Mappings.Count}",
            $"- Declared production operations: " +
            $"{analysis.Manifest.Mappings.Sum(mapping => mapping.ProductionOperations.Count())}",
            $"- Semantic fixture cases: {analysis.Fixtures.Cases.Count}",
            $"- Status: **{(analysis.IsClean ? "clean" : "findings")}** " +
            $"({analysis.Errors.Count} error(s), " +
            $"{analysis.Advisories.Count} advisory)",
            "",
            "## Mapping state by legacy surface",
            "",
            "| Legacy surface | Class | State | Capability disposition | Production operations |",
            "| --- | --- | --- | --- | --- |"
        };

        foreach (var mapping in analysis.Manifest.Mappings
                     .OrderBy(item => item.State, StringComparer.Ordinal)
                     .ThenBy(item => item.LegacySurface, StringComparer.Ordinal))
        {
            var operations = string.Join(
                ", ",
                mapping.ProductionOperations.Select(item => $"`{item.Name}`"));

            lines.Add(
                $"| {mapping.LegacySurface} | {mapping.MatrixClassification} | " +
                $"{mapping.State} | {mapping.CapabilityDisposition} | " +
                $"{operations} |");
        }

        lines.Add(string.Empty);
        lines.Add("## Semantic parity cases");
        lines.Add(string.Empty);
        lines.Add(
            "| Case | Surface | Capability equivalent | Accounted | v1 provenance | v2 provenance |");
        lines.Add("| --- | --- | --- | --- | --- | --- |");

        foreach (var outcome in analysis.Cases)
        {
            var fixture = analysis.Fixtures.ByName[outcome.Name];
            lines.Add(
                $"| {outcome.Name} | {outcome.Surface} | " +
                $"{outcome.CapabilityEquivalent} | " +
                $"{(outcome.Passed ? "all 9 fields" : "INCOMPLETE")} | " +
                $"{fixture.V1Provenance} | {fixture.V2Provenance} |");
        }

        if (analysis.Findings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("## Findings");
            lines.Add(string.Empty);
            lines.Add("| Code | Severity | Subject | Message |");
            lines.Add("| --- | --- | --- | --- |");

            foreach (var finding in analysis.Findings
                         .OrderBy(item => item.Severity, StringComparer.Ordinal)
                         .ThenBy(item => item.Code, StringComparer.Ordinal)
                         .ThenBy(item => item.Subject, StringComparer.Ordinal))
            {
                lines.Add(
                    $"| {finding.Code} | {finding.Severity} | " +
                    $"{finding.Subject} | {finding.Message} |");
            }
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}

internal static class V2RootLocator
{
    private static string? _root;

    public static string Root => _root ??= Locate();

    public static string Locate(string? startDirectory = null)
    {
        var explicitRoot = Environment.GetEnvironmentVariable("COMTOOL_V2_ROOT");
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            var normalizedExplicitRoot = Path.GetFullPath(explicitRoot);
            if (File.Exists(Path.Combine(normalizedExplicitRoot, "ComTool.V2.slnx")))
                return normalizedExplicitRoot;

            throw new DirectoryNotFoundException(
                $"COMTOOL_V2_ROOT '{normalizedExplicitRoot}' is not a COM Tool V2 root.");
        }

        var primary = startDirectory ?? AppContext.BaseDirectory;
        var located = LocateFrom(primary);
        if (located is not null)
            return located;

        // Test/publish tooling can deliberately relocate assemblies outside the
        // source tree (for example release.ps1's isolated --artifacts-path).
        // In that case AppContext.BaseDirectory cannot lead back to the repo,
        // while the invoking process still has the V2 checkout as its working
        // directory. Keep assembly-relative discovery first, then fall back to
        // the process working directory so the parity gate is location-neutral.
        if (!Path.GetFullPath(primary).Equals(
                Path.GetFullPath(Environment.CurrentDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            located = LocateFrom(Environment.CurrentDirectory);
            if (located is not null)
                return located;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the COM Tool V2 root (ComTool.V2.slnx). " +
            $"Searched from '{Path.GetFullPath(primary)}' and " +
            $"'{Path.GetFullPath(Environment.CurrentDirectory)}'.");
    }

    private static string? LocateFrom(string startDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ComTool.V2.slnx")))
                return current.FullName;

            var nested = Path.Combine(current.FullName, "comtool-v2");

            if (File.Exists(Path.Combine(nested, "ComTool.V2.slnx")))
                return nested;

            current = current.Parent;
        }

        return null;
    }
}
