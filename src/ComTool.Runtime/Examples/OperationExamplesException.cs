namespace ComTool.Runtime.Examples;

/// <summary>
/// Raised when a checked-in example contradicts the live operation catalog or
/// the v1 protocol envelope. This is a construction-time authoring defect, not
/// a caller error: it must never be reachable from a valid request path.
/// </summary>
public sealed class OperationExamplesException(string kind, string message)
    : Exception(message)
{
    /// <summary>Stable machine-readable failure kind.</summary>
    public string Kind { get; } = kind;
}
