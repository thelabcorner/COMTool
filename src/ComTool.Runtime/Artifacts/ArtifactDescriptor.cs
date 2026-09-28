using System.Text.Json.Serialization;

namespace ComTool.Runtime.Artifacts;

/// <summary>
/// Client-facing description of one stored artifact.
/// </summary>
/// <remarks>
/// <para>
/// A descriptor is the ONLY retrieval contract. It intentionally carries no
/// filesystem location, because a path is an unusable and unsafe handle across
/// a process boundary: it discloses the runtime's private layout, it goes
/// stale if the state root moves, and it invites a client to build a path of
/// its own. Clients resolve content exclusively through
/// <see cref="ArtifactId"/>.
/// </para>
/// <para>
/// <see cref="Sha256"/> addresses the immutable data bytes, while
/// <see cref="ArtifactId"/> addresses the revisionable metadata record. Two
/// artifact ids may share one <see cref="Sha256"/>; the data is written once
/// and never rewritten, only referenced and released.
/// </para>
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ArtifactDescriptor
{
    /// <summary>Opaque, runtime-minted handle. Never a path.</summary>
    public required string ArtifactId { get; init; }

    /// <summary>Lowercase hex SHA-256 of the stored bytes.</summary>
    public required string Sha256 { get; init; }

    /// <summary>
    /// Real byte length of the stored content as UTF-8 bytes on disk, not a
    /// character count. See <c>ArtifactResultOffload</c> for why the legacy
    /// character-count accounting was a defect.
    /// </summary>
    public required long ByteCount { get; init; }

    /// <summary>Media type of the stored bytes, e.g. <c>application/json</c>.</summary>
    public required string MediaType { get; init; }

    /// <summary>
    /// Content encoding, e.g. <c>utf-8</c> or <c>binary</c>. Recorded because
    /// <see cref="ByteCount"/> is only meaningful next to the encoding it was
    /// measured under.
    /// </summary>
    public required string Encoding { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// After this instant the artifact is no longer retrievable and the
    /// runtime is free to reclaim it. Reads past this point fail with
    /// <c>artifact_expired</c> rather than serving stale content.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Monotonic revision of the metadata record. Only expiry and release
    /// state advance it; the content fields are immutable. Compare-and-swap on
    /// this value is what makes concurrent retention/release safe.
    /// </summary>
    public required long Revision { get; init; }
}
