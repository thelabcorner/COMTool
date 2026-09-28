namespace ComTool.Migration.Parity;

/// <summary>
/// Mapping-state vocabulary. Parity is a statement about the semantic
/// capability, never about whether a legacy subcommand name and grammar
/// survived.
/// </summary>
internal static class ParityStates
{
    public const string Implemented = "implemented";
    public const string Partial = "partial";
    public const string Superseded = "superseded";
    public const string IntentionalDrop = "intentional_drop";
    public const string Missing = "missing";

    public static readonly string[] All =
    [
        Implemented,
        Partial,
        Superseded,
        IntentionalDrop,
        Missing
    ];

    /// <summary>States in which a named production operation must exist.</summary>
    public static readonly string[] RequireRegisteredOperation =
    [
        Implemented,
        Partial,
        Superseded
    ];
}

internal static class CapabilityDispositions
{
    public const string Retained = "retained";
    public const string ReplacedByStrongerAbstraction = "replaced_by_stronger_abstraction";
    public const string IntentionallyNotRetained = "intentionally_not_retained";

    public static readonly string[] All =
    [
        Retained,
        ReplacedByStrongerAbstraction,
        IntentionallyNotRetained
    ];

    public static readonly string[] NotRetainedStates =
    [
        ParityStates.Superseded,
        ParityStates.IntentionalDrop
    ];
}

internal static class V2OperationKinds
{
    /// <summary>Names a real registered operation; catalog presence is required when production.</summary>
    public const string Operation = "operation";

    /// <summary>Names a registered operation the matrix promised but the catalog does not yet carry.</summary>
    public const string PlannedOperation = "planned_operation";

    /// <summary>Names architecture (facet, policy, type, transport), never a registered operation.</summary>
    public const string Facet = "facet";
    public const string Policy = "policy";
    public const string Type = "type";
    public const string Transport = "transport";

    public static readonly string[] All =
    [
        Operation,
        PlannedOperation,
        Facet,
        Policy,
        Type,
        Transport
    ];

    public static bool IsOperationLike(string kind) =>
        kind is Operation or PlannedOperation;
}

internal sealed record V2OperationRef(
    string Name,
    bool Production,
    string Kind);

internal sealed record EvidenceRef(
    string Kind,
    string Path,
    string? Symbol,
    string? Note);

internal sealed record Mapping(
    string LegacySurface,
    string MatrixClassification,
    string Capability,
    string State,
    string CapabilityDisposition,
    IReadOnlyList<V2OperationRef> V2Operations,
    IReadOnlyList<string> MissingAspects,
    IReadOnlyList<string> IntentionalIncompatibilities,
    IReadOnlyList<string> SemanticFixtures,
    IReadOnlyList<EvidenceRef> Evidence)
{
    public IEnumerable<V2OperationRef> ProductionOperations =>
        V2Operations.Where(operation => operation.Production);

    public IEnumerable<V2OperationRef> OperationLikeReferences =>
        V2Operations.Where(operation =>
            V2OperationKinds.IsOperationLike(operation.Kind));
}

internal sealed record DeclaredDivergence(
    string Field,
    string V1,
    string V2,
    string Reason);

internal sealed record SemanticCase(
    string Name,
    string Surface,
    bool CapabilityEquivalent,
    string Rationale,
    string V1Provenance,
    string V2Provenance,
    bool V1RequestPresent,
    string? V1Operation,
    string? V2Operation,
    System.Text.Json.JsonElement V1Envelope,
    System.Text.Json.JsonElement V2Request,
    System.Text.Json.JsonElement V2Result,
    IReadOnlyList<string> Equivalence,
    IReadOnlyList<DeclaredDivergence> Divergences,
    IReadOnlyList<string> IntentionalIncompatibilities);

internal sealed record Finding(
    string Code,
    string Severity,
    string Subject,
    string Message)
{
    public const string SeverityError = "error";
    public const string SeverityAdvisory = "advisory";

    public bool IsError => Severity == SeverityError;
}
