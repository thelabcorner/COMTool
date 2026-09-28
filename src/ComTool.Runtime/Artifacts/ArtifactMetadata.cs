using System.Text.Json.Serialization;

namespace ComTool.Runtime.Artifacts;

/// <summary>
/// The durable sidecar record for one artifact. This is the scalar, revisioned
/// half of the store: the bytes it points at are immutable, while expiry and
/// release state may be revised under compare-and-swap.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ArtifactMetadata
{
    public const int CurrentSchemaVersion = 1;

    public required int SchemaVersion { get; init; }
    public required string ArtifactId { get; init; }
    public required string Sha256 { get; init; }
    public required long ByteCount { get; init; }
    public required string MediaType { get; init; }
    public required string Encoding { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required long Revision { get; init; }
}
