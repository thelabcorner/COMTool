using System.Text.Json;

namespace ComTool.Migration.Parity;

/// <summary>
/// Strict readers for the four lane-owned inputs. Every parse failure is a
/// harness failure, never a silent skip, so a renamed field cannot quietly
/// turn into a passing parity report.
/// </summary>
internal static class ParityInputs
{
    public const string MatrixRelativePath = "migration/legacy-feature-matrix.json";
    public const string ManifestRelativePath = "migration/parity/parity-manifest.json";
    public const string FixturesRelativePath = "migration/parity/semantic-fixtures.json";
    public const string RegistryRelativePath = "protocol/operation-registry.json";

    public sealed record MatrixEntry(string LegacySurface, string Classification);

    public sealed record Matrix(
        int SchemaVersion,
        IReadOnlyList<MatrixEntry> Features)
    {
        public IReadOnlyDictionary<string, string> BySurface { get; } =
            Features.ToDictionary(
                entry => entry.LegacySurface,
                entry => entry.Classification,
                StringComparer.Ordinal);
    }

    public sealed record Manifest(
        int SchemaVersion,
        IReadOnlyList<Mapping> Mappings)
    {
        public Mapping? Find(string legacySurface) =>
            Mappings.FirstOrDefault(mapping =>
                string.Equals(
                    mapping.LegacySurface,
                    legacySurface,
                    StringComparison.Ordinal));
    }

    public sealed record FixtureSet(
        int SchemaVersion,
        IReadOnlyList<SemanticCase> Cases)
    {
        public IReadOnlyDictionary<string, SemanticCase> ByName { get; } =
            Cases.ToDictionary(
                entry => entry.Name,
                StringComparer.Ordinal);
    }

    public sealed record Registry(
        int SchemaVersion,
        IReadOnlySet<string> OperationNames)
    {
    }

    public static Matrix ReadMatrix(string root)
    {
        using var document = Parse(root, MatrixRelativePath);
        var json = document.RootElement;

        var features = new List<MatrixEntry>();
        foreach (var feature in RequireArray(json, "features", MatrixRelativePath))
        {
            features.Add(new MatrixEntry(
                RequireString(feature, "legacySurface", MatrixRelativePath),
                RequireString(feature, "classification", MatrixRelativePath)));
        }

        return new Matrix(RequireInt(json, "schemaVersion"), features);
    }

    public static Manifest ReadManifest(string root)
    {
        using var document = Parse(root, ManifestRelativePath);
        var json = document.RootElement;

        var mappings = new List<Mapping>();
        foreach (var entry in RequireArray(json, "mappings", ManifestRelativePath))
        {
            var surface =
                RequireString(entry, "legacySurface", ManifestRelativePath);

            mappings.Add(new Mapping(
                surface,
                RequireString(entry, "matrixClassification", ManifestRelativePath),
                RequireString(entry, "capability", ManifestRelativePath),
                RequireString(entry, "state", ManifestRelativePath),
                RequireString(entry, "capabilityDisposition", ManifestRelativePath),
                ReadOperations(entry, surface),
                ReadStrings(entry, "missingAspects", surface),
                ReadStrings(entry, "intentionalIncompatibilities", surface),
                ReadStrings(entry, "semanticFixtures", surface),
                ReadEvidence(entry, surface)));
        }

        return new Manifest(RequireInt(json, "schemaVersion"), mappings);
    }

    public static FixtureSet ReadFixtures(string root)
    {
        using var document = Parse(root, FixturesRelativePath);
        var json = document.RootElement;

        var cases = new List<SemanticCase>();
        foreach (var entry in RequireArray(json, "cases", FixturesRelativePath))
        {
            var name = RequireString(entry, "name", FixturesRelativePath);
            var v1 = RequireObject(entry, "v1", FixturesRelativePath);
            var v2 = RequireObject(entry, "v2", FixturesRelativePath);
            var provenance = RequireObject(
                entry,
                "provenance",
                FixturesRelativePath);

            var v1Request = OptionalObject(v1, "request");
            var v2Request = RequireObject(v2, "request", FixturesRelativePath);

            cases.Add(new SemanticCase(
                name,
                RequireString(entry, "surface", FixturesRelativePath),
                RequireBool(entry, "capabilityEquivalent", FixturesRelativePath),
                RequireString(entry, "rationale", FixturesRelativePath),
                RequireString(provenance, "v1", FixturesRelativePath),
                RequireString(provenance, "v2", FixturesRelativePath),
                v1Request is not null,
                OptionalOperationName(v1Request),
                RequireString(v2Request, "operation", FixturesRelativePath),
                RequireObject(v1, "envelope", FixturesRelativePath).Clone(),
                v2Request.Clone(),
                RequireObject(v2, "result", FixturesRelativePath).Clone(),
                ReadStrings(entry, "equivalence", name),
                ReadDivergences(entry, name),
                ReadStrings(entry, "intentionalIncompatibilities", name)));
        }

        return new FixtureSet(RequireInt(json, "schemaVersion"), cases);
    }

    public static Registry ReadRegistry(string root)
    {
        using var document = Parse(root, RegistryRelativePath);
        var json = document.RootElement;

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in RequireArray(json, "operations", RegistryRelativePath))
            names.Add(RequireString(operation, "name", RegistryRelativePath));

        return new Registry(RequireInt(json, "schemaVersion"), names);
    }

    private static IReadOnlyList<V2OperationRef> ReadOperations(
        JsonElement mapping,
        string surface)
    {
        var operations = new List<V2OperationRef>();
        foreach (var operation in RequireArray(mapping, "v2Operations", surface))
        {
            operations.Add(new V2OperationRef(
                RequireString(operation, "name", surface),
                RequireBool(operation, "production", surface),
                RequireString(operation, "kind", surface)));
        }

        return operations;
    }

    private static IReadOnlyList<EvidenceRef> ReadEvidence(
        JsonElement mapping,
        string surface)
    {
        var evidence = new List<EvidenceRef>();
        foreach (var item in RequireArray(mapping, "evidence", surface))
        {
            evidence.Add(new EvidenceRef(
                RequireString(item, "kind", surface),
                RequireString(item, "path", surface),
                OptionalString(item, "symbol"),
                OptionalString(item, "note")));
        }

        return evidence;
    }

    private static IReadOnlyList<DeclaredDivergence> ReadDivergences(
        JsonElement entry,
        string caseName)
    {
        var divergences = new List<DeclaredDivergence>();
        foreach (var item in RequireArray(entry, "divergences", caseName))
        {
            divergences.Add(new DeclaredDivergence(
                RequireString(item, "field", caseName),
                RequireString(item, "v1", caseName),
                RequireString(item, "v2", caseName),
                RequireString(item, "reason", caseName)));
        }

        return divergences;
    }

    private static IReadOnlyList<string> ReadStrings(
        JsonElement parent,
        string property,
        string subject)
    {
        if (!parent.TryGetProperty(property, out var array))
            return [];

        if (array.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(
                $"{subject}: '{property}' must be an array.");

        return array.EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .ToArray();
    }

    private static JsonDocument Parse(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Required parity input is missing: {relativePath}",
                path);

        try
        {
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"{relativePath} is not valid JSON: {exception.Message}",
                exception);
        }
    }

    private static JsonElement RequireObject(
        JsonElement parent,
        string property,
        string subject) =>
        Require(parent, property, JsonValueKind.Object, subject);

    private static IEnumerable<JsonElement> RequireArray(
        JsonElement parent,
        string property,
        string subject) =>
        Require(parent, property, JsonValueKind.Array, subject).EnumerateArray();

    private static JsonElement Require(
        JsonElement parent,
        string property,
        JsonValueKind kind,
        string subject)
    {
        if (!parent.TryGetProperty(property, out var value) ||
            value.ValueKind != kind)
        {
            throw new InvalidDataException(
                $"{subject}: '{property}' must be {kind}.");
        }

        return value;
    }

    private static JsonElement? OptionalObject(
        JsonElement parent,
        string property) =>
        parent.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string RequireString(
        JsonElement parent,
        string property,
        string subject)
    {
        var value = Require(parent, property, JsonValueKind.String, subject);
        return value.GetString() ??
            throw new InvalidDataException(
                $"{subject}: '{property}' must not be null.");
    }

    private static string? OptionalString(
        JsonElement parent,
        string property) =>
        parent.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool RequireBool(
        JsonElement parent,
        string property,
        string subject)
    {
        if (!parent.TryGetProperty(property, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException(
                $"{subject}: '{property}' must be a boolean.");
        }

        return value.GetBoolean();
    }

    private static int RequireInt(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : throw new InvalidDataException(
                $"'{property}' must be an integer.");

    private static string? OptionalOperationName(JsonElement? request) =>
        request is { } element &&
        element.TryGetProperty("operation", out var operation) &&
        operation.ValueKind == JsonValueKind.String
            ? operation.GetString()
            : null;
}
