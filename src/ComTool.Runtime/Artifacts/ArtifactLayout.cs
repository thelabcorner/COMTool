using System.Text.Json;

namespace ComTool.Runtime.Artifacts;

/// <summary>
/// Owns the on-disk shape of the artifact subtree and every path derivation
/// inside it.
/// </summary>
/// <remarks>
/// <para>
/// Layout, rooted at the runtime state root:
/// <code>
/// artifacts/v1/artifact-manifest.json   layout identity, refuses mismatch
/// artifacts/v1/blobs/&lt;aa&gt;/&lt;bb&gt;/&lt;sha256&gt;   immutable content
/// artifacts/v1/meta/&lt;artifactId&gt;.json        revisionable record
/// artifacts/v1/incoming/                 staging + collection claims
/// </code>
/// </para>
/// <para>
/// The version segment is part of the path, so a future layout cannot be
/// misread by this one, and the manifest additionally refuses a directory that
/// claims a different version. No path here is ever built from caller input:
/// blob paths come from a validated content hash and metadata paths from a
/// validated opaque id, and both are re-checked for containment before use.
/// That is why a caller cannot address the filesystem — traversal is not
/// filtered at the end, it is never constructible.
/// </para>
/// </remarks>
public static class ArtifactLayout
{
    public const int CurrentLayoutVersion = 1;

    private const string Format = "comtool-v2-artifacts";
    private const string ManifestFileName = "artifact-manifest.json";
    private const string ArtifactsDirectoryName = "artifacts";
    private const string BlobsDirectoryName = "blobs";
    private const string MetaDirectoryName = "meta";
    private const string IncomingDirectoryName = "incoming";
    private const int Sha256HexLength = 64;
    private const int ShardLength = 2;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    /// <summary>Versioned artifact root beneath a runtime state root.</summary>
    public static string ResolveRoot(string stateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);

        return Path.Combine(
            Path.GetFullPath(stateRoot),
            ArtifactsDirectoryName,
            "v" + CurrentLayoutVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Creates the versioned root if absent and validates any manifest already
    /// present. Throws rather than adopting a directory that claims to be a
    /// different artifact layout.
    /// </summary>
    public static string EnsureRoot(string stateRoot)
    {
        var root = ResolveRoot(stateRoot);

        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(BlobsRoot(root));
            Directory.CreateDirectory(MetaRoot(root));
            Directory.CreateDirectory(IncomingRoot(root));
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            throw new ArtifactStoreException(
                "artifact_root_unavailable",
                $"Could not create the artifact root: {ex.Message}",
                ex);
        }

        foreach (var directory in new[]
                 {
                     BlobsRoot(root),
                     MetaRoot(root),
                     IncomingRoot(root)
                 })
        {
            if (IsReparsePoint(directory))
            {
                throw new ArtifactStoreException(
                    "artifact_root_unsafe",
                    "An artifact store directory is a link or junction. " +
                    "Refusing to adopt a root whose contents could be " +
                    "redirected outside it.");
            }
        }

        var manifestPath = RequireInsideRoot(
            root,
            Path.Combine(root, ManifestFileName));

        if (File.Exists(manifestPath))
        {
            EnsureMatchingManifest(manifestPath);
        }
        else
        {
            WriteManifest(manifestPath);
        }

        return root;
    }

    internal static string BlobsRoot(string root) =>
        Path.Combine(root, BlobsDirectoryName);

    internal static string MetaRoot(string root) =>
        Path.Combine(root, MetaDirectoryName);

    internal static string IncomingRoot(string root) =>
        Path.Combine(root, IncomingDirectoryName);

    /// <summary>
    /// Derives the immutable content path for a validated lowercase hex
    /// SHA-256, sharded two levels to keep any single directory small.
    /// </summary>
    internal static string BlobPath(string root, string sha256)
    {
        var hash = RequireSha256(sha256);
        var directory = RequireInsideRoot(
            root,
            Path.Combine(
                BlobsRoot(root),
                hash[..ShardLength],
                hash[ShardLength..(ShardLength * 2)]));

        Directory.CreateDirectory(directory);
        return RequireInsideRoot(
            root,
            Path.Combine(directory, hash));
    }

    /// <summary>Derives the metadata sidecar path for a validated id.</summary>
    internal static string MetadataPath(string root, string artifactId) =>
        RequireInsideRoot(
            root,
            Path.Combine(
                MetaRoot(root),
                ArtifactId.Require(artifactId) + ".json"));

    internal static string RequireSha256(string value)
    {
        return IsContentAddressName(value)
            ? value
            : throw new ArtifactStoreException(
                "artifact_content_address_invalid",
                "Content address must be exactly " +
                $"{Sha256HexLength} lowercase hex characters.");
    }

    internal static bool IsContentAddressName(string name)
    {
        if (name.Length != Sha256HexLength)
            return false;

        foreach (var c in name)
        {
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }

        return true;
    }

    internal static bool IsShardName(string name)
    {
        if (name.Length != ShardLength)
            return false;

        foreach (var c in name)
        {
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Defense in depth. Path derivation already makes escape impossible; this
    /// asserts it, so a future derivation bug fails loudly instead of quietly
    /// addressing a file outside the artifact root.
    /// </summary>
    internal static string RequireInsideRoot(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root);
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(candidate);

        if (!full.StartsWith(prefix, PathComparison))
        {
            throw new ArtifactStoreException(
                "artifact_path_escape",
                "Refusing to address a path outside the artifact root.");
        }

        return full;
    }

    /// <summary>
    /// True when a path is a link or junction. The store never follows or
    /// deletes one, so a reparse point planted inside the artifact root cannot
    /// redirect a read or a cleanup at content elsewhere on the machine.
    /// </summary>
    internal static bool IsReparsePoint(string path)
    {
        try
        {
            return File
                .GetAttributes(path)
                .HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void EnsureMatchingManifest(string manifestPath)
    {
        LayoutManifest manifest;
        try
        {
            manifest =
                JsonSerializer.Deserialize<LayoutManifest>(
                    File.ReadAllBytes(manifestPath),
                    Json)
                ?? throw new JsonException(
                    "Artifact layout manifest deserialized to null.");
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                JsonException)
        {
            throw new ArtifactStoreException(
                "artifact_layout_manifest_corrupt",
                $"Could not read the artifact layout manifest: {ex.Message}",
                ex);
        }

        if (!string.Equals(
                manifest.Format,
                Format,
                StringComparison.Ordinal))
        {
            throw new ArtifactStoreException(
                "artifact_layout_format_mismatch",
                "The artifact root is not a COM Tool V2 artifact root.");
        }

        if (manifest.LayoutVersion != CurrentLayoutVersion)
        {
            throw new ArtifactStoreException(
                "artifact_layout_version_mismatch",
                $"The artifact root uses layout version " +
                $"{manifest.LayoutVersion}; this runtime supports only " +
                $"version {CurrentLayoutVersion}.");
        }
    }

    private static void WriteManifest(string manifestPath)
    {
        var manifest = new LayoutManifest
        {
            Format = Format,
            LayoutVersion = CurrentLayoutVersion,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var temporary = manifestPath + "." + Guid.NewGuid().ToString("N") +
            ".tmp";

        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
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

            try
            {
                File.Move(temporary, manifestPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(manifestPath))
            {
            }
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record LayoutManifest
    {
        public required string Format { get; init; }
        public required int LayoutVersion { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
    }
}
