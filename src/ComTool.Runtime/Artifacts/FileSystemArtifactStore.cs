using System.Security.Cryptography;
using System.Text.Json;

namespace ComTool.Runtime.Artifacts;

/// <summary>
/// Filesystem implementation of <see cref="IArtifactStore"/>.
/// </summary>
/// <remarks>
/// Content is content-addressed and immutable: bytes are streamed to a staging
/// file while being hashed, then linked into
/// <c>blobs/&lt;aa&gt;/&lt;bb&gt;/&lt;sha256&gt;</c> with a no-overwrite move, so
/// identical content is stored once and existing bytes are never rewritten.
/// A separate sidecar record owns expiry and is the only mutable state, which
/// is what lets two artifact ids share one body without either of them owning
/// it.
/// </remarks>
public sealed class FileSystemArtifactStore : IArtifactStore
{
    private const int BufferSize = 64 * 1024;

    private static readonly JsonSerializerOptions MetadataJson =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    private readonly ArtifactStoreOptions _options;

    // In-process only. Linking content, writing its record, and reclaiming
    // unreferenced content must not interleave. Cross-process exclusion is the
    // state root's existing single-owner lock; this store adds no second
    // lock layer and no second ownership claim.
    private readonly object _commitGate = new();

    public FileSystemArtifactStore(ArtifactStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
        Root = ArtifactLayout.EnsureRoot(options.StateRoot);
        LayoutVersion = ArtifactLayout.CurrentLayoutVersion;
    }

    public string Root { get; }

    public int LayoutVersion { get; }

    public ArtifactDescriptor Put(
        ArtifactWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var mediaType =
            ArtifactMediaTypes.RequireMediaType(request.MediaType);
        var encoding = ArtifactMediaTypes.RequireEncoding(
            request.Encoding ??
            ArtifactMediaTypes.DefaultEncodingFor(mediaType));
        var createdAt = Now();
        var expiresAt = createdAt + ResolveTimeToLive(request.TimeToLive);

        var staging = ArtifactLayout.RequireInsideRoot(
            Root,
            Path.Combine(
                ArtifactLayout.IncomingRoot(Root),
                "put-" + Guid.NewGuid().ToString("N") + ".part"));

        try
        {
            var staged = StageContent(
                request.Content,
                staging,
                cancellationToken);

            lock (_commitGate)
            {
                var blobPath = ArtifactLayout.BlobPath(Root, staged.Sha256);
                LinkStagedContent(staging, blobPath);

                var metadata = new ArtifactMetadata
                {
                    SchemaVersion = ArtifactMetadata.CurrentSchemaVersion,
                    ArtifactId = ArtifactId.NewId(),
                    Sha256 = staged.Sha256,
                    ByteCount = staged.ByteCount,
                    MediaType = mediaType,
                    Encoding = encoding,
                    CreatedAt = createdAt,
                    ExpiresAt = expiresAt,
                    Revision = 1
                };

                WriteMetadata(
                    ArtifactLayout.MetadataPath(Root, metadata.ArtifactId),
                    metadata,
                    overwrite: false);

                return ToDescriptor(metadata);
            }
        }
        finally
        {
            ArtifactLayout.TryDelete(staging);
        }
    }

    public ArtifactDescriptor Describe(
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ToDescriptor(RequireRetrievableMetadata(artifactId));
    }

    public ArtifactReadResult GetRange(
        string artifactId,
        ArtifactRange? range = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var metadata = RequireRetrievableMetadata(artifactId);
        var effective =
            range ?? ArtifactRange.FromStart(metadata.ByteCount);

        // A range is materialized into a single array, so the memory bound is
        // additionally capped at what an array can address.
        effective.Validate(
            metadata.ByteCount,
            Math.Min(_options.MaxReadByteCount, int.MaxValue));

        using var handle = OpenVerified(metadata);
        handle.Content.Seek(effective.Offset, SeekOrigin.Begin);

        var buffer = new byte[(int)effective.Length];
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = handle.Content.Read(
                buffer,
                filled,
                buffer.Length - filled);
            if (read <= 0)
                break;
            filled += read;
        }

        if (filled != buffer.Length)
        {
            throw new ArtifactStoreException(
                "artifact_read_short",
                $"Artifact '{metadata.ArtifactId}' returned {filled} of " +
                $"{buffer.Length} requested bytes.");
        }

        return new ArtifactReadResult
        {
            Descriptor = handle.Descriptor,
            Offset = effective.Offset,
            Content = buffer
        };
    }

    public ArtifactReadHandle Open(
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return OpenVerified(RequireRetrievableMetadata(artifactId));
    }

    public int DeleteExpired(CancellationToken cancellationToken = default)
    {
        lock (_commitGate)
        {
            return DeleteExpiredCore(cancellationToken).Removed;
        }
    }

    public ArtifactSweepReport Sweep(
        CancellationToken cancellationToken = default)
    {
        ExpiryPass expiry;
        Reclamation reclamation;
        IncomingCleanup incoming;

        lock (_commitGate)
        {
            expiry = DeleteExpiredCore(cancellationToken);
            reclamation = ReclaimUnreferencedContent(cancellationToken);
            incoming = ReclaimStaleIncoming(cancellationToken);
        }

        return new ArtifactSweepReport
        {
            Scanned = expiry.Scanned,
            ExpiredRemoved = expiry.Removed,
            BlobsRemoved = reclamation.BlobsRemoved,
            StagingFilesRemoved = incoming.FilesRemoved,
            StagingBytesReclaimed = incoming.BytesReclaimed,
            BytesReclaimed =
                reclamation.BytesReclaimed +
                incoming.BytesReclaimed,
            SkippedReparsePoints =
                expiry.SkippedReparsePoints +
                reclamation.SkippedReparsePoints +
                incoming.SkippedReparsePoints,
            Incomplete =
                expiry.Truncated ||
                reclamation.Truncated ||
                incoming.Truncated,
            ContentReclamationSkipped = reclamation.Skipped
        };
    }

    private ExpiryPass DeleteExpiredCore(CancellationToken cancellationToken)
    {
        var now = Now();
        var scanned = 0;
        var removed = 0;
        var skipped = 0;
        var truncated = false;

        foreach (var entry in EnumerateMetadataEntries())
        {
            if (scanned >= _options.SweepBatchLimit)
            {
                truncated = true;
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            scanned++;

            if (entry.IsReparsePoint)
            {
                skipped++;
                continue;
            }

            // A file here that is not one of our records is not ours to judge
            // or delete.
            if (entry.ArtifactId is null)
                continue;

            ArtifactMetadata metadata;
            try
            {
                metadata = ReadMetadataFile(entry.Path, entry.ArtifactId);
            }
            catch (ArtifactStoreException)
            {
                // Unreadable or corrupt: leave it in place. Deleting a record
                // we could not parse would destroy the reference that keeps
                // its content alive.
                continue;
            }

            if (metadata.ExpiresAt > now)
                continue;

            ArtifactLayout.TryDelete(entry.Path);
            removed++;
        }

        return new ExpiryPass(scanned, removed, skipped, truncated);
    }

    private Reclamation ReclaimUnreferencedContent(
        CancellationToken cancellationToken)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var examined = 0;
        var skipped = 0;
        var referenceSetComplete = true;

        foreach (var entry in EnumerateMetadataEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (examined >= _options.SweepBatchLimit)
            {
                referenceSetComplete = false;
                break;
            }

            examined++;

            if (entry.IsReparsePoint)
            {
                skipped++;
                continue;
            }

            if (entry.ArtifactId is null)
                continue;

            try
            {
                referenced.Add(
                    ReadMetadataFile(entry.Path, entry.ArtifactId).Sha256);
            }
            catch (ArtifactStoreException)
            {
                // Content whose only possible reference we cannot read is
                // content we cannot prove is unreferenced. Reclaim nothing.
                referenceSetComplete = false;
                break;
            }
        }

        if (!referenceSetComplete)
        {
            return new Reclamation(
                BlobsRemoved: 0,
                BytesReclaimed: 0,
                SkippedReparsePoints: skipped,
                Skipped: true,
                Truncated: examined >= _options.SweepBatchLimit);
        }

        var removed = 0;
        long bytes = 0;
        var contentSeen = 0;
        var truncated = false;

        foreach (var entry in EnumerateBlobEntries())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (contentSeen >= _options.SweepBatchLimit)
            {
                truncated = true;
                break;
            }

            contentSeen++;

            if (entry.IsReparsePoint)
            {
                skipped++;
                continue;
            }

            if (referenced.Contains(entry.Sha256))
                continue;

            var claim = ArtifactLayout.RequireInsideRoot(
                Root,
                Path.Combine(
                    ArtifactLayout.IncomingRoot(Root),
                    "gc-" + Guid.NewGuid().ToString("N") + ".part"));

            try
            {
                // Claim first, delete second. The rename is atomic and takes
                // ownership, so there is no window in which a partially removed
                // file still presents itself as stored content.
                File.Move(entry.Path, claim, overwrite: false);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            ArtifactLayout.TryDelete(claim);
            removed++;
            bytes += entry.Size;
        }

        return new Reclamation(
            removed,
            bytes,
            skipped,
            Skipped: false,
            Truncated: truncated);
    }

    private IncomingCleanup ReclaimStaleIncoming(
        CancellationToken cancellationToken)
    {
        var incomingRoot = ArtifactLayout.IncomingRoot(Root);
        if (!Directory.Exists(incomingRoot) ||
            ArtifactLayout.IsReparsePoint(incomingRoot))
        {
            return new IncomingCleanup(
                FilesRemoved: 0,
                BytesReclaimed: 0,
                SkippedReparsePoints:
                    Directory.Exists(incomingRoot) ? 1 : 0,
                Truncated: false);
        }

        var cutoff = Now() - _options.StaleIncomingMaxAge;
        var examined = 0;
        var removed = 0;
        var skipped = 0;
        long bytes = 0;
        var truncated = false;

        foreach (var path in Directory.EnumerateFiles(
                     incomingRoot,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            if (examined >= _options.SweepBatchLimit)
            {
                truncated = true;
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            examined++;

            if (ArtifactLayout.IsReparsePoint(path))
            {
                skipped++;
                continue;
            }

            var name = Path.GetFileName(path);
            if (!IsStoreOwnedIncomingName(name))
                continue;

            try
            {
                var info = new FileInfo(path);
                if (info.LastWriteTimeUtc >= cutoff.UtcDateTime)
                    continue;

                var length = info.Length;
                File.Delete(path);
                removed++;
                bytes += length;
            }
            catch (Exception ex) when (
                ex is IOException or
                    UnauthorizedAccessException or
                    FileNotFoundException)
            {
                // A concurrently disappearing or unreadable staging claim is
                // not evidence that it is safe to delete. Leave it alone.
            }
        }

        return new IncomingCleanup(
            removed,
            bytes,
            skipped,
            truncated);
    }

    private static bool IsStoreOwnedIncomingName(string name) =>
        name.EndsWith(".part", StringComparison.Ordinal) &&
        (name.StartsWith("put-", StringComparison.Ordinal) ||
         name.StartsWith("gc-", StringComparison.Ordinal));

    private (string Sha256, long ByteCount) StageContent(
        Stream content,
        string staging,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long total = 0;

        try
        {
            using var target = new FileStream(
                staging,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.WriteThrough);

            int read;
            while ((read = content.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                total += read;
                if (total > _options.MaxArtifactByteCount)
                {
                    throw new ArtifactStoreException(
                        "artifact_too_large",
                        $"Artifact exceeds the maximum stored size of " +
                        $"{_options.MaxArtifactByteCount} bytes.");
                }

                hash.AppendData(buffer, 0, read);
                target.Write(buffer, 0, read);
            }

            target.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            throw new ArtifactStoreException(
                "artifact_write_failed",
                $"Could not stage artifact content: {ex.Message}",
                ex);
        }

        // total is bounded by MaxArtifactByteCount, so it cannot overflow.
        return (
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            total);
    }

    private static void LinkStagedContent(string staging, string blobPath)
    {
        try
        {
            File.Move(staging, blobPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(blobPath))
        {
            // This content is already stored. Bytes are immutable, so the
            // existing copy is authoritative and the duplicate is discarded
            // rather than overwriting it.
            ArtifactLayout.TryDelete(staging);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            throw new ArtifactStoreException(
                "artifact_content_commit_failed",
                $"Could not store artifact content: {ex.Message}",
                ex);
        }
    }

    private static void WriteMetadata(
        string metadataPath,
        ArtifactMetadata metadata,
        bool overwrite)
    {
        var temporary =
            metadataPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            var bytes =
                JsonSerializer.SerializeToUtf8Bytes(metadata, MetadataJson);

            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, metadataPath, overwrite);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            throw new ArtifactStoreException(
                "artifact_metadata_write_failed",
                $"Could not write artifact metadata: {ex.Message}",
                ex);
        }
        finally
        {
            ArtifactLayout.TryDelete(temporary);
        }
    }

    private ArtifactMetadata RequireRetrievableMetadata(string artifactId)
    {
        var id = ArtifactId.Require(artifactId);
        var metadata = LoadMetadata(id);

        if (metadata.ExpiresAt <= Now())
        {
            throw new ArtifactStoreException(
                "artifact_expired",
                $"Artifact '{id}' is past its retention window and is no " +
                "longer retrievable.");
        }

        return metadata;
    }

    private ArtifactMetadata LoadMetadata(string artifactId)
    {
        var path = ArtifactLayout.MetadataPath(Root, artifactId);

        if (ArtifactLayout.IsReparsePoint(path))
        {
            throw new ArtifactStoreException(
                "artifact_metadata_unsafe",
                $"Metadata for artifact '{artifactId}' is a link or " +
                "junction and will not be read.");
        }

        return ReadMetadataFile(path, artifactId);
    }

    private static ArtifactMetadata ReadMetadataFile(
        string path,
        string artifactId)
    {
        ArtifactMetadata? metadata;

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            metadata = JsonSerializer.Deserialize<ArtifactMetadata>(
                stream,
                MetadataJson);
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new ArtifactStoreException(
                "artifact_not_found",
                $"No artifact '{artifactId}' exists in this runtime.");
        }
        catch (JsonException ex)
        {
            throw new ArtifactStoreException(
                "artifact_metadata_corrupt",
                $"Metadata for artifact '{artifactId}' is not readable: " +
                ex.Message,
                ex);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            throw new ArtifactStoreException(
                "artifact_metadata_read_failed",
                $"Could not read metadata for artifact '{artifactId}': " +
                ex.Message,
                ex);
        }

        if (metadata is null ||
            metadata.SchemaVersion != ArtifactMetadata.CurrentSchemaVersion ||
            !string.Equals(
                metadata.ArtifactId,
                artifactId,
                StringComparison.Ordinal))
        {
            throw new ArtifactStoreException(
                "artifact_metadata_corrupt",
                $"Metadata for artifact '{artifactId}' is inconsistent with " +
                "the artifact it claims to describe.");
        }

        return metadata;
    }

    private ArtifactReadHandle OpenVerified(ArtifactMetadata metadata)
    {
        var blobPath = ArtifactLayout.BlobPath(Root, metadata.Sha256);

        if (ArtifactLayout.IsReparsePoint(blobPath))
        {
            throw new ArtifactStoreException(
                "artifact_content_unsafe",
                $"Content for artifact '{metadata.ArtifactId}' is a link " +
                "or junction and will not be read.");
        }

        FileStream stream;
        try
        {
            stream = new FileStream(
                blobPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.RandomAccess);
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new ArtifactStoreException(
                "artifact_content_missing",
                $"Content for artifact '{metadata.ArtifactId}' is no longer " +
                "present in the store.");
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            throw new ArtifactStoreException(
                "artifact_content_read_failed",
                $"Could not open content for artifact " +
                $"'{metadata.ArtifactId}': {ex.Message}",
                ex);
        }

        // Content is immutable, so a length mismatch is store corruption, not
        // a short read. Report it instead of handing back partial content.
        if (stream.Length != metadata.ByteCount)
        {
            var actual = stream.Length;
            stream.Dispose();

            throw new ArtifactStoreException(
                "artifact_content_mismatch",
                $"Content for artifact '{metadata.ArtifactId}' holds " +
                $"{actual} bytes but its descriptor records " +
                $"{metadata.ByteCount}.");
        }

        return ArtifactReadHandle.Create(ToDescriptor(metadata), stream);
    }

    private IEnumerable<MetadataEntry> EnumerateMetadataEntries()
    {
        var metaRoot = ArtifactLayout.MetaRoot(Root);

        // Never traverse a linked directory: enumeration must not be able to
        // walk outside the artifact root.
        if (!Directory.Exists(metaRoot) ||
            ArtifactLayout.IsReparsePoint(metaRoot))
            yield break;

        foreach (var path in Directory.EnumerateFiles(
                     metaRoot,
                     "*.json",
                     SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            yield return new MetadataEntry(
                path,
                ArtifactId.IsValid(name) ? name : null,
                ArtifactLayout.IsReparsePoint(path));
        }
    }

    private IEnumerable<BlobEntry> EnumerateBlobEntries()
    {
        var blobsRoot = ArtifactLayout.BlobsRoot(Root);

        if (!Directory.Exists(blobsRoot) ||
            ArtifactLayout.IsReparsePoint(blobsRoot))
            yield break;

        foreach (var shard in Directory.EnumerateDirectories(blobsRoot))
        {
            if (!ArtifactLayout.IsShardName(Path.GetFileName(shard)) ||
                ArtifactLayout.IsReparsePoint(shard))
                continue;

            foreach (var leaf in Directory.EnumerateDirectories(shard))
            {
                if (!ArtifactLayout.IsShardName(Path.GetFileName(leaf)) ||
                    ArtifactLayout.IsReparsePoint(leaf))
                    continue;

                foreach (var path in Directory.EnumerateFiles(
                             leaf,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(path);
                    if (!ArtifactLayout.IsContentAddressName(name))
                        continue;

                    long size = 0;
                    try
                    {
                        size = new FileInfo(path).Length;
                    }
                    catch (Exception ex) when (
                        ex is IOException or
                            UnauthorizedAccessException or
                            FileNotFoundException)
                    {
                    }

                    yield return new BlobEntry(
                        path,
                        name,
                        size,
                        ArtifactLayout.IsReparsePoint(path));
                }
            }
        }
    }

    private TimeSpan ResolveTimeToLive(TimeSpan? requested)
    {
        var timeToLive = requested ?? _options.DefaultTimeToLive;

        if (timeToLive <= TimeSpan.Zero)
        {
            throw new ArtifactStoreException(
                "artifact_ttl_invalid",
                "Requested retention must be positive.");
        }

        if (timeToLive > _options.MaxTimeToLive)
        {
            throw new ArtifactStoreException(
                "artifact_ttl_too_long",
                $"Requested retention of {timeToLive} exceeds the maximum " +
                $"of {_options.MaxTimeToLive}.");
        }

        return timeToLive;
    }

    private static ArtifactDescriptor ToDescriptor(ArtifactMetadata metadata) =>
        new()
        {
            ArtifactId = metadata.ArtifactId,
            Sha256 = metadata.Sha256,
            ByteCount = metadata.ByteCount,
            MediaType = metadata.MediaType,
            Encoding = metadata.Encoding,
            CreatedAt = metadata.CreatedAt,
            ExpiresAt = metadata.ExpiresAt,
            Revision = metadata.Revision
        };

    private DateTimeOffset Now() => _options.Clock();

    private readonly record struct MetadataEntry(
        string Path,
        string? ArtifactId,
        bool IsReparsePoint);

    private readonly record struct BlobEntry(
        string Path,
        string Sha256,
        long Size,
        bool IsReparsePoint);

    private readonly record struct ExpiryPass(
        int Scanned,
        int Removed,
        int SkippedReparsePoints,
        bool Truncated);

    private readonly record struct Reclamation(
        int BlobsRemoved,
        long BytesReclaimed,
        int SkippedReparsePoints,
        bool Skipped,
        bool Truncated);

    private readonly record struct IncomingCleanup(
        int FilesRemoved,
        long BytesReclaimed,
        int SkippedReparsePoints,
        bool Truncated);
}
