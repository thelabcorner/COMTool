namespace ComTool.Runtime.Artifacts;

/// <summary>
/// One bounded slice of artifact content plus the descriptor it came from.
/// </summary>
public sealed record ArtifactReadResult
{
    public required ArtifactDescriptor Descriptor { get; init; }

    public required long Offset { get; init; }

    public required ReadOnlyMemory<byte> Content { get; init; }

    public int ReturnedByteCount => Content.Length;

    /// <summary>
    /// True when the caller received something other than the whole artifact.
    /// A partial read is always an explicit client choice, never a silent
    /// truncation: bounds are validated before any bytes are read.
    /// </summary>
    public bool IsPartial =>
        Offset != 0 || Content.Length != Descriptor.ByteCount;
}
