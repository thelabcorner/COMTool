using System.Text.Json;

namespace ComTool.Runtime.Examples;

/// <summary>
/// One operation-owned, machine-readable example.
/// <para>
/// An example deliberately carries only what the runtime cannot derive: the
/// request <c>input</c> document, a stable identity, short agent-facing prose,
/// and the ordered prerequisite operations. Safety class, target requirement,
/// lease requirement, execution scope, host, and mutation resolution are NEVER
/// authored here. They are read from the authoritative
/// <see cref="OperationDefinition"/> at projection time, so an example cannot
/// drift away from the operation's real safety semantics.
/// </para>
/// <para>
/// An example is data only. Nothing here dispatches, opens a document, or
/// contacts a host.
/// </para>
/// </summary>
public sealed record OperationExample
{
    /// <summary>Registered operation name this example demonstrates.</summary>
    public required string Operation { get; init; }

    /// <summary>Stable, catalog-unique example identity.</summary>
    public required string Id { get; init; }

    /// <summary>Short imperative title.</summary>
    public required string Title { get; init; }

    /// <summary>One-line description of what the example proves.</summary>
    public required string Summary { get; init; }

    /// <summary>
    /// The example <c>input</c> document, exactly as a caller would send it.
    /// Must be a JSON object.
    /// </summary>
    public required JsonElement Input { get; init; }

    /// <summary>Lowercase tokens for explicit, caller-declared filtering.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Ordered operations an agent should establish first.</summary>
    public IReadOnlyList<OperationExampleReference> Prerequisites { get; init; } = [];

    /// <summary>Safety-relevant caveats the example cannot express in input.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>A prerequisite operation plus the reason it comes first.</summary>
public sealed record OperationExampleReference(
    string Operation,
    string Why);

/// <summary>
/// Safe construction of example <c>input</c> documents. Every document is
/// cloned so a disposed <see cref="JsonDocument"/> can never invalidate a
/// checked-in example at runtime.
/// </summary>
public static class OperationExampleInput
{
    /// <summary>Parses <paramref name="json"/> into a detached input element.</summary>
    public static JsonElement Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

/// <summary>A registered operation that intentionally has no example yet.</summary>
public sealed record UncoveredOperation(
    string Operation,
    string Reason);
