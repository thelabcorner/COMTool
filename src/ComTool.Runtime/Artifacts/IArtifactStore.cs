namespace ComTool.Runtime.Artifacts;

/// <summary>
/// Runtime-owned store for content that is too large to return inline in an
/// operation result.
/// </summary>
/// <remarks>
/// <para>
/// The store is deliberately small: put content, describe it, read a bounded
/// range or a stream, drop expired records, and reclaim unreferenced content.
/// There is no browse-by-path, no arbitrary delete, and no caller-supplied
/// location anywhere in the contract.
/// </para>
/// <para>
/// Artifacts are results, never mutation state. They live in their own
/// versioned subtree beside the mutation ledger, are never referenced by a
/// ledger record, and nothing here can create, clear, or replay an incident.
/// </para>
/// <para>
/// Thread safety: safe for concurrent use within the runtime process. Cross
/// process exclusion is the state root's existing single-owner lock; this
/// store deliberately adds no second lock layer.
/// </para>
/// </remarks>
public interface IArtifactStore
{
    /// <summary>
    /// Versioned artifact root, for runtime diagnostics only. It is never part
    /// of a descriptor and is never a retrieval handle.
    /// </summary>
    string Root { get; }

    int LayoutVersion { get; }

    /// <summary>
    /// Stores content and returns its descriptor. Content is content-addressed
    /// and immutable: storing identical bytes again links the existing content
    /// instead of rewriting it, and mints a distinct artifact id.
    /// </summary>
    ArtifactDescriptor Put(
        ArtifactWriteRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the metadata record for an artifact. Fails with
    /// <c>artifact_not_found</c> or <c>artifact_expired</c> rather than
    /// returning a descriptor for content that is gone or past retention.
    /// </summary>
    ArtifactDescriptor Describe(
        string artifactId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a bounded range. A null range means the whole artifact and is
    /// still subject to the store's memory bound. Range bounds are validated
    /// before any bytes are read, so a short response is never a silent
    /// truncation.
    /// </summary>
    ArtifactReadResult GetRange(
        string artifactId,
        ArtifactRange? range = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a read-only stream for content larger than the memory bound. The
    /// returned handle is verified against the recorded byte count before the
    /// caller sees it.
    /// </summary>
    ArtifactReadHandle Open(
        string artifactId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes metadata records whose retention has elapsed and returns how
    /// many were removed. Content is reclaimed by <see cref="Sweep"/>, so
    /// calling only this trades disk for time.
    /// </summary>
    int DeleteExpired(CancellationToken cancellationToken = default);

    /// <summary>
    /// One bounded cleanup pass: drop expired records, then reclaim content no
    /// surviving record references. Never deletes content it could not prove
    /// unreferenced, and reports when it stopped early.
    /// </summary>
    ArtifactSweepReport Sweep(CancellationToken cancellationToken = default);
}
