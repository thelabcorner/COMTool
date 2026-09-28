using System.Security.Cryptography;

namespace ComTool.Runtime.Artifacts;

/// <summary>
/// A verified read-only view of one artifact's content.
/// </summary>
/// <remarks>
/// The stream is opened with a share mode that permits concurrent readers but
/// no writer, and its length is checked against the descriptor before this
/// handle is handed out. Content bytes are immutable, so a mismatch means the
/// store is inconsistent and is reported as such instead of being served as a
/// short read.
/// </remarks>
public sealed class ArtifactReadHandle : IDisposable
{
    private readonly FileStream _stream;
    private int _disposed;

    private ArtifactReadHandle(ArtifactDescriptor descriptor, FileStream stream)
    {
        Descriptor = descriptor;
        _stream = stream;
    }

    public ArtifactDescriptor Descriptor { get; }

    public Stream Content => _stream;

    internal static ArtifactReadHandle Create(
        ArtifactDescriptor descriptor,
        FileStream stream) =>
        new(descriptor, stream);

    /// <summary>
    /// Re-hashes the whole content and compares it to the descriptor. This is
    /// the only way to check integrity of a partial read, since a range cannot
    /// be validated against the full content address.
    /// </summary>
    public void VerifyContentHash()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        if (!_stream.CanSeek)
            throw new ArtifactStoreException(
                "artifact_stream_unseekable",
                "Content stream must be seekable to verify its hash.");

        var position = _stream.Position;
        try
        {
            _stream.Position = 0;
            using var sha = SHA256.Create();
            var actual = Convert.ToHexString(
                    sha.ComputeHash(_stream))
                .ToLowerInvariant();

            if (!string.Equals(
                    actual,
                    Descriptor.Sha256,
                    StringComparison.Ordinal))
            {
                throw new ArtifactStoreException(
                    "artifact_content_corrupt",
                    "Stored content does not match its recorded SHA-256.");
            }
        }
        catch (IOException ex)
        {
            throw new ArtifactStoreException(
                "artifact_read_failed",
                $"Could not read stored content: {ex.Message}",
                ex);
        }
        finally
        {
            _stream.Position = position;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _stream.Dispose();
    }
}
