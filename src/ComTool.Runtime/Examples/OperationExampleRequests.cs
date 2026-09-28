using ComTool.Protocol;

namespace ComTool.Runtime.Examples;

/// <summary>
/// Builds the concrete v1 request an example stands for and validates it with
/// the real production envelope validator.
/// <para>
/// Target and lease material is DERIVED from the operation definition, not
/// authored per example. That is what makes "this operation needs a lease" an
/// enforced invariant rather than a comment somebody forgot to update.
/// </para>
/// </summary>
public static class OperationExampleRequests
{
    /// <summary>Placeholder request id; a caller substitutes its own.</summary>
    public const string PlaceholderRequestId = "example-request-id";

    /// <summary>
    /// Placeholder target host. Deliberately not a real host claim: a caller
    /// substitutes the host it discovered through <c>core.targets.list</c>.
    /// </summary>
    public const string PlaceholderTargetHost = "example-host";

    /// <summary>Placeholder target identity; a caller substitutes its own.</summary>
    public const string PlaceholderTargetId = "example-target-id";

    /// <summary>Placeholder process generation; a caller substitutes its own.</summary>
    public const long PlaceholderTargetGeneration = 1;

    /// <summary>
    /// Placeholder lease id. The 32-128 character <c>[A-Za-z0-9_-]</c> rule is
    /// enforced by the real validator, not restated here.
    /// </summary>
    public const string PlaceholderLeaseId = "example-lease-id-placeholder-000000";

    /// <summary>
    /// Assembles and validates the request an example represents.
    /// </summary>
    /// <exception cref="OperationExamplesException">
    /// The example cannot be expressed as a valid v1 request under the
    /// target/lease envelope <paramref name="definition"/> requires.
    /// </exception>
    public static OperationRequest Build(
        OperationDefinition definition,
        OperationExample example)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(example);

        var request = new OperationRequest
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = PlaceholderRequestId,
            Operation = definition.Name,
            Input = example.Input,
            Target = definition.RequiresTarget
                ? new TargetRef(
                    definition.Host ?? PlaceholderTargetHost,
                    PlaceholderTargetId,
                    PlaceholderTargetGeneration)
                : null,
            Policy = definition.RequiresLease
                ? new OperationPolicy(LeaseId: PlaceholderLeaseId)
                : null
        };

        // The production envelope validator, not a paraphrase of it.
        ProtocolJson.ValidateRequest(request);
        return request;
    }

    /// <summary>
    /// Projects the JSON request template for an example. The template is
    /// produced by the same builder that validated the example, so the emitted
    /// request is literally the request that was checked.
    /// </summary>
    public static object Template(
        OperationDefinition definition,
        OperationExample example)
    {
        var request = Build(definition, example);

        return new
        {
            protocolVersion = request.ProtocolVersion,
            id = request.Id,
            target = request.Target,
            operation = request.Operation,
            input = request.Input,
            policy = request.Policy
        };
    }
}
