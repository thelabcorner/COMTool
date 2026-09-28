using System.Text.Json.Serialization;

namespace ComTool.Broker.Protocol;

/// <summary>
/// Internal runtime↔worker transfer limits for a large successful host result.
/// Raw chunks are intentionally 512 KiB: their base64 JSON representation plus
/// broker metadata remains comfortably below the 1 MiB framed-pipe ceiling.
/// </summary>
public static class BrokerArtifactTransferLimits
{
    public const int ChunkByteCount = 512 * 1024;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BrokerArtifactChunk
{
    public required string TransferId { get; init; }
    public required int ChunkIndex { get; init; }
    public required byte[] Data { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BrokerArtifactTransfer
{
    public required string TransferId { get; init; }
    public required int ChunkCount { get; init; }
    public required long PayloadByteCount { get; init; }
    public required string PayloadSha256 { get; init; }
    public required string OriginalResultKind { get; init; }
}