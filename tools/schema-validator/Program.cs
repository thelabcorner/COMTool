using System.Text.Json;
using Json.Schema;

internal static class Program
{
    private static int Main(string[] args)
    {
        var root = FindV2Root(args.FirstOrDefault());
        var schemaDir = Path.Combine(root, "schemas", "v1-draft");
        var casesPath = Path.Combine(root, "fixtures", "protocol", "cases.json");
        var currentRegistrySchemaPath = Path.Combine(
            root,
            "schemas",
            "v1",
            "operation-registry.schema.json");
        var currentRegistryPath = Path.Combine(
            root,
            "protocol",
            "operation-registry.json");

        Dialect.Default = Dialect.Draft202012;

        var schemas = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(schemaDir, "*.schema.json").Order(StringComparer.Ordinal))
        {
            // FromFile builds and registers the schema. Dynamic network fetching is
            // intentionally not configured; all external refs must resolve locally.
            var schema = JsonSchema.FromFile(file);
            schemas.Add(Path.GetFileName(file), schema);
        }

        using var casesDocument = JsonDocument.Parse(File.ReadAllText(casesPath));
        var rootElement = casesDocument.RootElement;
        var manifestVersion = rootElement.GetProperty("schemaVersion").GetInt32();
        if (manifestVersion != 1)
            throw new InvalidDataException($"Unsupported fixture manifest version {manifestVersion}");

        var evaluationOptions = new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true,
            IncludeApplicatorErrors = true
        };

        var caseResults = new List<object>();
        var total = 0;
        var matched = 0;

        foreach (var testCase in rootElement.GetProperty("cases").EnumerateArray())
        {
            total++;
            var name = testCase.GetProperty("name").GetString()
                ?? throw new InvalidDataException("Fixture name is null");
            var schemaName = testCase.GetProperty("schema").GetString()
                ?? throw new InvalidDataException($"Fixture {name} schema is null");
            var expectedValid = testCase.GetProperty("valid").GetBoolean();
            var instance = testCase.GetProperty("instance");

            if (!schemas.TryGetValue(schemaName, out var schema))
                throw new InvalidDataException($"Fixture {name} references unknown schema {schemaName}");

            var result = schema.Evaluate(instance, evaluationOptions);
            var actualValid = result.IsValid;
            var ok = actualValid == expectedValid;
            if (ok) matched++;

            caseResults.Add(new
            {
                name,
                schema = schemaName,
                expectedValid,
                actualValid,
                ok,
                errors = ok ? null : result.Errors,
                instanceLocation = ok ? null : result.InstanceLocation.ToString(),
                schemaLocation = ok ? null : result.SchemaLocation.ToString()
            });
        }

        var currentRegistrySchema = JsonSchema.FromFile(
            currentRegistrySchemaPath);
        using var currentRegistryDocument = JsonDocument.Parse(
            File.ReadAllText(currentRegistryPath));
        var currentRegistryResult = currentRegistrySchema.Evaluate(
            currentRegistryDocument.RootElement,
            evaluationOptions);
        total++;
        if (currentRegistryResult.IsValid)
            matched++;
        caseResults.Add(new
        {
            name = "current-operation-registry",
            schema = "schemas/v1/operation-registry.schema.json",
            expectedValid = true,
            actualValid = currentRegistryResult.IsValid,
            ok = currentRegistryResult.IsValid,
            errors = currentRegistryResult.IsValid
                ? null
                : currentRegistryResult.Errors,
            instanceLocation = currentRegistryResult.IsValid
                ? null
                : currentRegistryResult.InstanceLocation.ToString(),
            schemaLocation = currentRegistryResult.IsValid
                ? null
                : currentRegistryResult.SchemaLocation.ToString()
        });

        var report = new
        {
            tool = "schema-validator",
            jsonSchema = "2020-12",
            library = "JsonSchema.Net 9.4.0",
            schemasLoaded = schemas.Count,
            total,
            matched,
            mismatched = total - matched,
            ok = matched == total,
            cases = caseResults
        };

        Console.WriteLine(JsonSerializer.Serialize(report));
        return matched == total ? 0 : 1;
    }

    private static string FindV2Root(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
            return Path.GetFullPath(explicitRoot);

        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PHASE0_STATUS.md")) &&
                Directory.Exists(Path.Combine(current.FullName, "schemas")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate comtool-v2 root. Run from inside comtool-v2 or pass the root path.");
    }
}
