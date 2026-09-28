namespace ComTool.Runtime.Artifacts;

/// <summary>
/// A half-open byte range <c>[Offset, Offset + Length)</c> inside one artifact.
/// </summary>
public readonly record struct ArtifactRange(long Offset, long Length)
{
    public static ArtifactRange FromStart(long length) => new(0, length);

    /// <summary>Inclusive end offset. Only meaningful after validation.</summary>
    public long End => Offset + Length;

    /// <summary>
    /// Rejects a range that is negative, empty against non-empty content, past
    /// the end of the artifact, or larger than the store's memory bound.
    /// </summary>
    internal void Validate(long artifactByteCount, long maxByteCount)
    {
        if (Offset < 0)
            throw new ArtifactStoreException(
                "artifact_range_invalid",
                "Range offset must not be negative.");

        if (Length < 0)
            throw new ArtifactStoreException(
                "artifact_range_invalid",
                "Range length must not be negative.");

        // An empty range is only meaningful against empty content; against
        // non-empty content it is far more likely a client bug than intent.
        if (Length == 0 && !(Offset == 0 && artifactByteCount == 0))
            throw new ArtifactStoreException(
                "artifact_range_invalid",
                "Range length must be positive for non-empty content.");

        if (Length > maxByteCount)
            throw new ArtifactStoreException(
                "artifact_range_too_large",
                $"Range length {Length} exceeds the maximum readable size " +
                $"{maxByteCount}. Use 'Open' or request a smaller range.");

        // Compared by subtraction so a huge offset cannot overflow.
        if (Offset > artifactByteCount - Length)
            throw new ArtifactStoreException(
                "artifact_range_out_of_bounds",
                $"Range [{Offset}, {Offset + Length}) is outside the " +
                $"artifact's {artifactByteCount} bytes.");
    }
}
