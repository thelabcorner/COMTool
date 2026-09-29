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

    [Theory]
    [InlineData("core.operations.list")]
    [InlineData("core.operation.describe")]
    [InlineData("core.adobe.probe")]
    public void RuntimeOperationIntrospectionIsReadOnlyAndTargetIndependent(
        string name)
    {
        var definition = BuiltInOperations.Catalog.GetRequired(name);

        Assert.Equal(MutationClass.ReadOnly, definition.MutationClass);
        Assert.False(definition.RequiresTarget);
        Assert.Equal(OperationExecutionScope.Runtime, definition.Scope);
        Assert.False(definition.RequiresLease);
        Assert.Null(definition.Host);
        Assert.Equal(
            MutationResolutionMode.Fixed,
            definition.MutationResolution);
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
    public void PluginMessageIsLeaseGatedFixedExternalSideEffect()
    {
        var definition = BuiltInOperations.Catalog.GetRequired(
            "plugin.message");

        Assert.Equal(
            MutationClass.ExternalSideEffect,
            definition.MutationClass);
        Assert.True(definition.RequiresTarget);
        Assert.Equal(
            OperationExecutionScope.Host,
            definition.Scope);
        Assert.Equal("illustrator", definition.Host);
        Assert.True(definition.RequiresLease);
        Assert.Equal(
            MutationResolutionMode.Fixed,
            definition.MutationResolution);
    }

    [Theory]
    [InlineData("debug.session.open")]
    [InlineData("debug.session.command")]
    [InlineData("debug.session.close")]
    public void DebuggerSessionOperationsAreLeaseGatedFixedExternalSideEffects(
        string name)
    {
        var definition =
            BuiltInOperations.Catalog.GetRequired(name);

        Assert.Equal(
            MutationClass.ExternalSideEffect,
            definition.MutationClass);
        Assert.True(definition.RequiresTarget);
        Assert.Equal(
            OperationExecutionScope.Host,
            definition.Scope);
        Assert.Equal(
            "illustrator",
            definition.Host);
        Assert.True(definition.RequiresLease);
        Assert.Equal(
            MutationResolutionMode.Fixed,
            definition.MutationResolution);
    }

    [Fact]
    public void DebuggerStatusIsHostScopedReadOnlyAndNeedsNoLease()
    {
        // Observing whether the worker owns a usable debugger session is not
        // debugger use, so it must not demand a lease, must not be classified
        // as a side effect, and must never be replayed as a mutation.
        var definition = BuiltInOperations.Catalog.GetRequired(
            "debug.session.status");

        Assert.Equal(MutationClass.ReadOnly, definition.MutationClass);
        Assert.True(definition.RequiresTarget);
        Assert.Equal(OperationExecutionScope.Host, definition.Scope);
        Assert.Equal("illustrator", definition.Host);
        Assert.False(definition.RequiresLease);
        Assert.Equal(
            MutationResolutionMode.Fixed,
            definition.MutationResolution);
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
    public void NewHostParityOperationsUseFixedExplicitSafetyClasses()
    {
        var expected = new Dictionary<string, (MutationClass Mutation, bool Lease)>
        {
            ["com.set"] = (MutationClass.ExternalSideEffect, true),
            ["com.call"] = (MutationClass.ExternalSideEffect, true),
            ["illustrator.action.run"] =
                (MutationClass.ExternalSideEffect, true),
            ["illustrator.menu.execute"] =
                (MutationClass.ExternalSideEffect, true),
            ["plugin.debug.diagnostics"] =
                (MutationClass.ReadOnly, false),
            ["script.codec.status"] =
                (MutationClass.ReadOnly, false),
            ["plugin.debug.control"] =
                (MutationClass.ExternalSideEffect, true)
        };

        foreach (var (name, policy) in expected)
        {
            var definition = BuiltInOperations.Catalog.GetRequired(name);

            Assert.Equal(policy.Mutation, definition.MutationClass);
            Assert.True(definition.RequiresTarget);
            Assert.Equal(OperationExecutionScope.Host, definition.Scope);
            Assert.Equal("illustrator", definition.Host);
            Assert.Equal(policy.Lease, definition.RequiresLease);
            Assert.Equal(
                MutationResolutionMode.Fixed,
                definition.MutationResolution);
        }
    }

    [Theory]
    [InlineData("illustrator.layer.setName")]
    [InlineData("illustrator.layer.setVisible")]
    [InlineData("illustrator.layer.setLocked")]
    [InlineData("illustrator.layer.setOpacity")]
    [InlineData("illustrator.artboard.setRect")]
    public void TypedIllustratorPropertyMutationsAreFixedLeaseOwnedIdempotentWrites(
        string name)
    {
        var definition = BuiltInOperations.Catalog.GetRequired(name);

        Assert.Equal(
            MutationClass.IdempotentWrite,
            definition.MutationClass);
        Assert.True(definition.RequiresTarget);
        Assert.Equal(
            OperationExecutionScope.Host,
            definition.Scope);
        Assert.Equal("illustrator", definition.Host);
        Assert.True(definition.RequiresLease);
        Assert.Equal(
            MutationResolutionMode.Fixed,
            definition.MutationResolution);
    }

    [Fact]
    public void TargetAttachIsExplicitTargetRuntimeReadWithoutLease()
    {
        var definition = BuiltInOperations.Catalog.GetRequired(
            "core.target.attach");

        Assert.Equal(MutationClass.ReadOnly, definition.MutationClass);
        Assert.True(definition.RequiresTarget);
        Assert.Equal(
            OperationExecutionScope.Runtime,
            definition.Scope);
        Assert.False(definition.RequiresLease);
        Assert.Null(definition.Host);
        Assert.Equal(
            MutationResolutionMode.Fixed,
            definition.MutationResolution);
    }

    [Fact]
    public void TargetLaunchIsTargetFreeRuntimeExternalSideEffect()
    {
        var definition = BuiltInOperations.Catalog.GetRequired(
            "core.target.launch");

        Assert.Equal(
            MutationClass.ExternalSideEffect,
            definition.MutationClass);
        Assert.False(definition.RequiresTarget);
        Assert.Equal(
            OperationExecutionScope.Runtime,
            definition.Scope);
        Assert.False(definition.RequiresLease);
        Assert.Null(definition.Host);
        Assert.Equal(
            MutationResolutionMode.Fixed,
            definition.MutationResolution);
    }

    [Theory]
    [InlineData("core.artifact.describe")]
    [InlineData("core.artifact.read")]
    public void ArtifactRetrievalOperationsAreTargetIndependentRuntimeReads(
        string name)
    {
        var definition = BuiltInOperations.Catalog.GetRequired(name);

        Assert.Equal(MutationClass.ReadOnly, definition.MutationClass);
        Assert.False(definition.RequiresTarget);
        Assert.Equal(OperationExecutionScope.Runtime, definition.Scope);
        Assert.False(definition.RequiresLease);
        Assert.Null(definition.Host);
        Assert.Equal(
            MutationResolutionMode.Fixed,
            definition.MutationResolution);
    }

    [Fact]
    public void OperationExamplesIsTargetIndependentRuntimeRead()
    {
        var definition = BuiltInOperations.Catalog.GetRequired(
            "core.operation.examples");

        Assert.Equal(MutationClass.ReadOnly, definition.MutationClass);
        Assert.False(definition.RequiresTarget);
        Assert.Equal(OperationExecutionScope.Runtime, definition.Scope);
        Assert.False(definition.RequiresLease);
        Assert.Null(definition.Host);
        Assert.Equal(
            MutationResolutionMode.Fixed,
            definition.MutationResolution);
    }

    [Theory]
    [InlineData("knowledge.describe")]
    [InlineData("knowledge.search")]
    [InlineData("knowledge.symbol")]
    [InlineData("knowledge.enum")]
    [InlineData("knowledge.paths")]
    public void KnowledgeOperationsAreTargetIndependentRuntimeReads(
        string name)
    {
        var definition = BuiltInOperations.Catalog.GetRequired(name);

        Assert.Equal(MutationClass.ReadOnly, definition.MutationClass);
        Assert.False(definition.RequiresTarget);
        Assert.Equal(OperationExecutionScope.Runtime, definition.Scope);
        Assert.False(definition.RequiresLease);
        Assert.Null(definition.Host);
        Assert.Equal(
            MutationResolutionMode.Fixed,
            definition.MutationResolution);
    }

    [Fact]
    public void ScriptValidateAndWatchConditionStayRuntimeOwned()
    {
        var validate =
            BuiltInOperations.Catalog.GetRequired("script.validate");
        Assert.Equal(MutationClass.ReadOnly, validate.MutationClass);
        Assert.False(validate.RequiresTarget);
        Assert.Equal(OperationExecutionScope.Runtime, validate.Scope);
        Assert.False(validate.RequiresLease);
        Assert.Null(validate.Host);
        Assert.Equal(
            MutationResolutionMode.Fixed,
            validate.MutationResolution);

        var watch =
            BuiltInOperations.Catalog.GetRequired("watch.condition");
        Assert.Equal(MutationClass.ReadOnly, watch.MutationClass);
        Assert.True(watch.RequiresTarget);
        Assert.Equal(OperationExecutionScope.Runtime, watch.Scope);
        Assert.False(watch.RequiresLease);
        Assert.Null(watch.Host);
        Assert.Equal(
            MutationResolutionMode.Fixed,
            watch.MutationResolution);
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
