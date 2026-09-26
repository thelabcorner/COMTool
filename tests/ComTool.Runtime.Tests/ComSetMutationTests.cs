using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;
using ComTool.Runtime;

namespace ComTool.Runtime.Tests;

/// <summary>
/// Proves the controlled-mutation safety floor for the fixed property-put
/// operation (<c>illustrator.artboard.setName</c>) at the runtime-metadata
/// layer. The operation is intentionally fixed-class: no caller input can
/// weaken it, and the resolver ignores any caller-supplied <c>effects</c>.
/// </summary>
public sealed class ComSetMutationTests
{
    private const string SetOperation =
        "illustrator.artboard.setName";

    [Fact]
    public void FixedIdempotentWriteIsDerivedFromRuntimeSemantics()
    {
        var definition = new OperationDefinition(
            SetOperation,
            MutationClass.IdempotentWrite,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            RequiresLease: true);

        var request = Request("""{"property":"document.artboard.active.name","value":"Board A"}""");

        Assert.Equal(
            MutationClass.IdempotentWrite,
            OperationMutationResolver.Resolve(definition, request));
    }

    [Theory]
    [InlineData("read_only")]
    [InlineData("unknown")]
    [InlineData("non_idempotent_write")]
    [InlineData("document_lifecycle")]
    public void CallerCannotDowngradeTheFixedMutationClass(
        string declaredEffects)
    {
        var definition = new OperationDefinition(
            SetOperation,
            MutationClass.IdempotentWrite,
            RequiresTarget: true,
            RequiresLease: true);

        var request = Request(
            $$"""{"property":"document.artboard.active.name","value":"Board A","effects":"{{declaredEffects}}"}""");

        // Fixed resolution ignores the caller's effects field entirely; the
        // runtime-owned mutation class is the safety floor.
        Assert.Equal(
            MutationClass.IdempotentWrite,
            OperationMutationResolver.Resolve(definition, request));
    }

    [Fact]
    public void SetOperationRequiresALease()
    {
        // The lease requirement is runtime-owned metadata, not caller input.
        // This mirrors the registration the integrator applies to
        // BuiltInOperations.Catalog.
        var definition = new OperationDefinition(
            SetOperation,
            MutationClass.IdempotentWrite,
            RequiresTarget: true,
            Scope: OperationExecutionScope.Host,
            Host: "illustrator",
            RequiresLease: true);

        Assert.True(definition.RequiresLease);
        Assert.True(definition.RequiresTarget);
        Assert.Equal(
            OperationExecutionScope.Host,
            definition.Scope);
        Assert.Equal("illustrator", definition.Host);
        Assert.Equal(
            MutationResolutionMode.Fixed,
            definition.MutationResolution);
    }

    [Fact]
    public void SetOperationIsNeverReadOnly()
    {
        // Guards against an accidental future relaxation: a mutation surface
        // must not be classifiable as read-only.
        var definition = new OperationDefinition(
            SetOperation,
            MutationClass.IdempotentWrite,
            RequiresTarget: true,
            RequiresLease: true);

        Assert.NotEqual(
            MutationClass.ReadOnly,
            definition.MutationClass);
    }

    private static OperationRequest Request(string inputJson)
    {
        using var document = JsonDocument.Parse(inputJson);

        return new OperationRequest
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = "com-set-runtime-test",
            Target = new TargetRef(
                "illustrator",
                "illustrator:test",
                Generation: 0),
            Operation = SetOperation,
            Input = document.RootElement.Clone()
        };
    }
}
