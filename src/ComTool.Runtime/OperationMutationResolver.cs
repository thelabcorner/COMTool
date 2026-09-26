using System.Text.Json;
using ComTool.Protocol;

namespace ComTool.Runtime;

/// <summary>
/// Derives the effective mutation class from runtime-owned operation semantics
/// plus validated request intent. Caller input may never weaken the runtime's
/// safety floor.
/// </summary>
public static class OperationMutationResolver
{
    public static MutationClass Resolve(
        OperationDefinition definition,
        OperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(request);

        return definition.MutationResolution switch
        {
            MutationResolutionMode.Fixed => definition.MutationClass,
            MutationResolutionMode.DeclaredOrUnknown =>
                ResolveDeclaredOrUnknown(request),
            _ => throw new OperationMutationPolicyException(
                "invalid_mutation_resolution",
                $"Operation '{definition.Name}' has an unsupported mutation-resolution mode.")
        };
    }

    private static MutationClass ResolveDeclaredOrUnknown(
        OperationRequest request)
    {
        if (request.Input.ValueKind != JsonValueKind.Object)
        {
            throw new OperationMutationPolicyException(
                "invalid_input",
                $"Operation '{request.Operation}' requires a JSON object input.");
        }

        if (!request.Input.TryGetProperty("effects", out var effects) ||
            effects.ValueKind == JsonValueKind.Null)
            return MutationClass.Unknown;

        if (effects.ValueKind != JsonValueKind.String)
        {
            throw new OperationMutationPolicyException(
                "invalid_effects",
                "'effects' must be a mutation-class string.");
        }

        var value = effects.GetString();
        var mutationClass = value switch
        {
            "unknown" => MutationClass.Unknown,
            "idempotent_write" => MutationClass.IdempotentWrite,
            "conditional_write" => MutationClass.ConditionalWrite,
            "non_idempotent_write" => MutationClass.NonIdempotentWrite,
            "document_lifecycle" => MutationClass.DocumentLifecycle,
            "external_side_effect" => MutationClass.ExternalSideEffect,
            "read_only" => throw new OperationMutationPolicyException(
                "unproven_read_only_script",
                "Arbitrary script execution cannot declare itself read-only. " +
                "Use a runtime-enforced read-only operation instead."),
            _ => throw new OperationMutationPolicyException(
                "invalid_effects",
                $"Unsupported effects value '{value ?? "<null>"}'.")
        };

        return mutationClass;
    }
}

public sealed class OperationMutationPolicyException(
    string kind,
    string message)
    : Exception(message)
{
    public string Kind { get; } = kind;
}
