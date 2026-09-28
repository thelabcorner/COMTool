namespace ComTool.Runtime.Artifacts;

/// <summary>
/// Producer-supplied content for <see cref="IArtifactStore.Put"/>.
/// </summary>
public sealed record ArtifactWriteRequest
{
    /// <summary>
    /// Source bytes. Read to completion; the store measures, hashes, and
    /// bounds them. The caller keeps ownership of the stream.
    /// </summary>
    public required Stream Content { get; init; }

    public required string MediaType { get; init; }

    /// <summary>
    /// Defaults to the encoding implied by the media type. Recorded on the
    /// descriptor because a byte count is only meaningful next to the encoding
    /// it was measured under.
    /// </summary>
    public string? Encoding { get; init; }

    /// <summary>
    /// Requested retention. Clamped to the store's maximum; defaults to the
    /// store's default when omitted.
    /// </summary>
    public TimeSpan? TimeToLive { get; init; }
}
