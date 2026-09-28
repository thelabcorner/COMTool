namespace ComTool.Runtime.Artifacts;

/// <summary>
/// Outcome of one bounded cleanup pass.
/// </summary>
public sealed record ArtifactSweepReport
{
    /// <summary>Metadata records examined during the expiry pass.</summary>
    public required int Scanned { get; init; }

    public required int ExpiredRemoved { get; init; }

    public required int BlobsRemoved { get; init; }

    /// <summary>
    /// Abandoned store-owned staging/GC claim files removed from
    /// <c>incoming/</c>.
    /// </summary>
    public required int StagingFilesRemoved { get; init; }

    public required long StagingBytesReclaimed { get; init; }

    public required long BytesReclaimed { get; init; }

    /// <summary>
    /// Reparse points found and deliberately left alone. A non-zero value means
    /// something inside the artifact root is a link or junction; the store
    /// neither followed nor deleted it.
    /// </summary>
    public required int SkippedReparsePoints { get; init; }

    /// <summary>
    /// True when the pass stopped at its batch limit and more work remains.
    /// Cleanup is bounded by design, so an operator or a later pass must
    /// repeat it; this flag is how that is reported rather than implied.
    /// </summary>
    public required bool Incomplete { get; init; }

    /// <summary>
    /// True when the pass could not prove a content file unreferenced and
    /// therefore declined to reclaim any content at all.
    /// </summary>
    public required bool ContentReclamationSkipped { get; init; }
}
