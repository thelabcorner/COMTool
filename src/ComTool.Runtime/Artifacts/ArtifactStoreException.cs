namespace ComTool.Runtime.Artifacts;

/// <summary>
/// Typed failure from the artifact store. <see cref="Kind"/> is the stable,
/// machine-readable reason a caller may branch on; the message is for humans
/// and never carries a filesystem path.
/// </summary>
public sealed class ArtifactStoreException(
    string kind,
    string message,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Kind { get; } = kind;
}
