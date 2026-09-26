using System.Runtime.CompilerServices;
using System.Text.Json;
using ComTool.Protocol;
using ComTool.Runtime;

namespace ComTool.Runtime.Tests;

public sealed class OperationCatalogIntegrationTests
{
    [Fact]
    public void CoreOperationsCreateCatalogUsesRuntimeCatalog()
    {
        Assert.Same(
            BuiltInOperations.Catalog,
            CoreOperations.CreateCatalog());
    }

    [Fact]
    public void MutationReconcileIsLeaseGatedRuntimeReadOnlyOperation()
    {
        var definition = BuiltInOperations.Catalog.GetRequired(
            "core.target.mutation.reconcile");

        Assert.Equal(MutationClass.ReadOnly, definition.MutationClass);
        Assert.True(definition.RequiresTarget);
        Assert.Equal(OperationExecutionScope.Runtime, definition.Scope);
        Assert.True(definition.RequiresLease);
        Assert.Null(definition.Host);
        Assert.Equal(
            MutationResolutionMode.Fixed,
            definition.MutationResolution);
    }

    [Fact]
    public void IllustratorWaveBOperationsAreRegisteredWithFixedRuntimeSemantics()
    {
        var expected = new Dictionary<string, (MutationClass Mutation, bool Lease)>
        {
            ["illustrator.artboard.setName"] =
                (MutationClass.IdempotentWrite, true),
            ["illustrator.document.read"] =
                (MutationClass.ReadOnly, false),
            ["illustrator.document.create"] =
                (MutationClass.DocumentLifecycle, true),
            ["illustrator.document.open"] =
                (MutationClass.DocumentLifecycle, true),
            ["illustrator.document.save"] =
                (MutationClass.DocumentLifecycle, true),
            ["illustrator.document.saveAs"] =
                (MutationClass.DocumentLifecycle, true),
            ["illustrator.document.close"] =
                (MutationClass.DocumentLifecycle, true)
        };

        foreach (var (name, expectation) in expected)
        {
            var definition = BuiltInOperations.Catalog.GetRequired(name);

            Assert.Equal(expectation.Mutation, definition.MutationClass);
            Assert.True(definition.RequiresTarget);
            Assert.Equal(OperationExecutionScope.Host, definition.Scope);
            Assert.Equal("illustrator", definition.Host);
            Assert.Equal(expectation.Lease, definition.RequiresLease);
            Assert.Equal(
                MutationResolutionMode.Fixed,
                definition.MutationResolution);
        }
    }

    [Fact]
    public void ScriptRunFileIsLeaseGatedAndUsesDeclaredOrUnknownEffects()
    {
        var definition = BuiltInOperations.Catalog.GetRequired(
            "script.runFile");

        Assert.Equal(
            MutationClass.Unknown,
            definition.MutationClass);
        Assert.True(definition.RequiresTarget);
        Assert.Equal(
            OperationExecutionScope.Host,
            definition.Scope);
        Assert.Equal("illustrator", definition.Host);
        Assert.True(definition.RequiresLease);
        Assert.Equal(
            MutationResolutionMode.DeclaredOrUnknown,
            definition.MutationResolution);
    }

    [Fact]
    public void IllustratorWave4StructureReadsAreRegisteredAsReadOnlyHostOperations()
    {
        foreach (var name in new[]
                 {
                     "illustrator.artboard.read",
                     "illustrator.layer.read"
                 })
        {
            var definition = BuiltInOperations.Catalog.GetRequired(name);

            Assert.Equal(MutationClass.ReadOnly, definition.MutationClass);
            Assert.True(definition.RequiresTarget);
            Assert.Equal(OperationExecutionScope.Host, definition.Scope);
            Assert.Equal("illustrator", definition.Host);
            Assert.False(definition.RequiresLease);
            Assert.Equal(
                MutationResolutionMode.Fixed,
                definition.MutationResolution);
        }
    }

    [Fact]
    public void WorkflowJobControlsAreRuntimeScopedAndDoNotTrustCallerMutationClasses()
    {
        foreach (var name in new[]
                 {
                     "core.workflow.submit",
                     "core.workflow.get",
                     "core.workflow.cancel",
                     "core.workflow.resume"
                 })
        {
            var definition = BuiltInOperations.Catalog.GetRequired(name);
            Assert.Equal(MutationClass.ReadOnly, definition.MutationClass);
            Assert.False(definition.RequiresTarget);
            Assert.Equal(OperationExecutionScope.Runtime, definition.Scope);
            Assert.False(definition.RequiresLease);
            Assert.Equal(
                MutationResolutionMode.Fixed,
                definition.MutationResolution);
        }
    }

    [Fact]
    public void LeaseRenewAndReleaseAdvertiseCallerLeaseRequirement()
    {
        Assert.False(
            BuiltInOperations.Catalog
                .GetRequired("core.target.lease.acquire")
                .RequiresLease);

        foreach (var name in new[]
                 {
                     "core.target.lease.renew",
                     "core.target.lease.release"
                 })
        {
            var definition =
                BuiltInOperations.Catalog.GetRequired(name);
            Assert.Equal(
                OperationExecutionScope.Runtime,
                definition.Scope);
            Assert.True(definition.RequiresTarget);
            Assert.True(definition.RequiresLease);
        }
    }

    [Fact]
    public void WorkflowSubmitInputSchemaIsPresentAndHasSafeBounds()
    {
        var schemaPath = Path.Combine(
            FindV2Root(),
            "schemas",
            "v1",
            "workflow-submit-input.schema.json");

        using var schemaDocument = JsonDocument.Parse(
            File.ReadAllText(schemaPath));
        var schema = schemaDocument.RootElement;

        Assert.Equal(
            "WorkflowSubmitInput",
            schema.GetProperty("title").GetString());
        Assert.Equal(
            64,
            schema.GetProperty("properties")
                .GetProperty("steps")
                .GetProperty("maxItems")
                .GetInt32());
        Assert.Equal(
            "stop",
            schema.GetProperty("properties")
                .GetProperty("onError")
                .GetProperty("enum")[0]
                .GetString());
    }

    [Fact]
    public void CheckedInOperationRegistryMatchesRuntimeCatalog()
    {
        var registryPath = Path.Combine(
            FindV2Root(),
            "protocol",
            "operation-registry.json");

        using var document = JsonDocument.Parse(
            File.ReadAllText(registryPath));
        var root = document.RootElement;

        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            ProtocolVersion.Current,
            root.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(
            "ComTool.Runtime.BuiltInOperations.Catalog",
            root.GetProperty("authority").GetString());

        var entries = root.GetProperty("operations")
            .EnumerateArray()
            .ToDictionary(
                static entry =>
                    entry.GetProperty("name").GetString()
                    ?? throw new InvalidDataException(
                        "Registry operation name is null."),
                static entry => entry.Clone(),
                StringComparer.Ordinal);

        Assert.Equal(BuiltInOperations.Catalog.Count, entries.Count);

        foreach (var definition in BuiltInOperations.Catalog.Definitions)
        {
            Assert.True(
                entries.TryGetValue(definition.Name, out var entry),
                $"Registry is missing '{definition.Name}'.");

            Assert.Equal(
                definition.Version,
                entry.GetProperty("version").GetString());
            Assert.Equal(
                JsonSerializer.Serialize(definition.MutationClass)
                    .Trim('"'),
                entry.GetProperty("mutationClass").GetString());
            Assert.Equal(
                definition.RequiresTarget,
                entry.GetProperty("requiresTarget").GetBoolean());
            Assert.Equal(
                definition.Scope == OperationExecutionScope.Runtime
                    ? "runtime"
                    : "host",
                entry.GetProperty("executionScope").GetString());
            Assert.Equal(
                definition.Host,
                entry.GetProperty("host").ValueKind == JsonValueKind.Null
                    ? null
                    : entry.GetProperty("host").GetString());
            Assert.Equal(
                definition.RequiresLease,
                entry.GetProperty("requiresLease").GetBoolean());
            Assert.Equal(
                definition.MutationResolution ==
                    MutationResolutionMode.Fixed
                    ? "fixed"
                    : "declared_or_unknown",
                entry.GetProperty("mutationResolution").GetString());
        }
    }

    private static string FindV2Root(
        [CallerFilePath] string sourceFile = "")
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFile);
        var current = new DirectoryInfo(
            string.IsNullOrWhiteSpace(sourceDirectory)
                ? Environment.CurrentDirectory
                : sourceDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ComTool.V2.slnx")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the COM Tool V2 root.");
    }
}
