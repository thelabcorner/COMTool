using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ComTool.Runtime.Artifacts;

namespace ComTool.Runtime.Tests;

/// <summary>
/// Proves the artifact kernel: content-addressed immutable bodies, scalar
/// revisionable records, bounded reads, bounded cleanup, and the guarantee that
/// a caller can only ever address an artifact by opaque id — never by path.
/// </summary>
public sealed class ArtifactStoreTests : IDisposable
{
    private const string LayoutManifestName = "artifact-manifest.json";

    private readonly string _stateRoot;
    private readonly FileSystemArtifactStore _store;

    public ArtifactStoreTests()
    {
        _stateRoot = Path.Combine(
            Path.GetTempPath(),
            "comtool-artifact-tests-" + Guid.NewGuid().ToString("N"));

        var origin = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        Now = origin;

        _store = new FileSystemArtifactStore(
            new ArtifactStoreOptions
            {
                StateRoot = _stateRoot,
                Clock = () => Now,
                DefaultTimeToLive = TimeSpan.FromHours(1),
                MaxTimeToLive = TimeSpan.FromHours(2),
                MaxArtifactByteCount = 4096,
                MaxReadByteCount = 512,
                SweepBatchLimit = 64
            });
    }

    private DateTimeOffset Now { get; set; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_stateRoot))
                Directory.Delete(_stateRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---- content identity ------------------------------------------------

    [Fact]
    public void PutRecordsRealByteCountAndContentHash()
    {
        // A payload whose UTF-8 byte length differs from its character count.
        // The legacy tool reported the character count as a byte count, so this
        // is the exact case V2 must get right.
        const string text = "caf\u00e9 na\u00efve \u2014 \u00e9\u00e9\u00e9";
        var expected = Encoding.UTF8.GetBytes(text);
        Assert.NotEqual(expected.Length, text.Length);

        var descriptor = PutBytes(expected, ArtifactMediaTypes.TextPlainUtf8);

        Assert.Equal(expected.Length, descriptor.ByteCount);
        Assert.Equal(Sha256Hex(expected), descriptor.Sha256);
        Assert.Equal(ArtifactMediaTypes.TextPlainUtf8, descriptor.MediaType);
        Assert.Equal(ArtifactMediaTypes.Utf8, descriptor.Encoding);
        Assert.Equal(1, descriptor.Revision);
        Assert.Equal(Now, descriptor.CreatedAt);
        Assert.Equal(Now + TimeSpan.FromHours(1), descriptor.ExpiresAt);
    }

    [Fact]
    public void PutMeasuresBinaryContentInBytes()
    {
        var payload = new byte[1024];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i % 256);

        var descriptor = PutBytes(payload);

        Assert.Equal(1024, descriptor.ByteCount);
        Assert.Equal(Sha256Hex(payload), descriptor.Sha256);
        Assert.Equal(ArtifactMediaTypes.Binary, descriptor.Encoding);
    }

    [Fact]
    public void OpenVerifiesContentAgainstItsRecordedHash()
    {
        var descriptor = PutBytes(Encoding.UTF8.GetBytes("verify me"));

        using var handle = _store.Open(descriptor.ArtifactId);
        handle.VerifyContentHash();

        Assert.Equal(descriptor.Sha256, handle.Descriptor.Sha256);
    }

    [Fact]
    public void IdenticalContentIsStoredOnceUnderDistinctArtifactIds()
    {
        var payload = Encoding.UTF8.GetBytes("shared body");
        var first = PutBytes(payload);
        var second = PutBytes(payload);

        // Content addressing collapses the body; the id addresses the record.
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.NotEqual(first.ArtifactId, second.ArtifactId);
        Assert.NotEqual(first.ArtifactId, first.Sha256);

        Assert.Single(BlobFiles());
        Assert.Equal(2, MetaFiles().Count);
        Assert.Equal(payload.Length, first.ByteCount);
    }

    // ---- bounded reads ---------------------------------------------------

    [Fact]
    public void RangeReadReturnsOnlyTheRequestedBytes()
    {
        var payload = new byte[1000];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i % 251);

        var descriptor = PutBytes(payload);

        var slice = _store.GetRange(
            descriptor.ArtifactId,
            new ArtifactRange(10, 5));

        Assert.Equal(10, slice.Offset);
        Assert.Equal(5, slice.ReturnedByteCount);
        Assert.Equal(payload.Skip(10).Take(5), slice.Content.ToArray());
        Assert.True(slice.IsPartial);
    }

    [Fact]
    public void WholeArtifactReadIsNotPartial()
    {
        var payload = Encoding.UTF8.GetBytes("small enough to inline");
        var descriptor = PutBytes(payload);

        var whole = _store.GetRange(descriptor.ArtifactId);

        Assert.False(whole.IsPartial);
        Assert.Equal(payload, whole.Content.ToArray());
    }

    [Fact]
    public void RangeReadRejectsBoundsViolations()
    {
        var descriptor = PutBytes(new byte[100]);

        AssertKind(
            "artifact_range_out_of_bounds",
            () => _store.GetRange(
                descriptor.ArtifactId,
                new ArtifactRange(95, 10)));

        AssertKind(
            "artifact_range_out_of_bounds",
            () => _store.GetRange(
                descriptor.ArtifactId,
                ArtifactRange.FromStart(101)));

        AssertKind(
            "artifact_range_invalid",
            () => _store.GetRange(
                descriptor.ArtifactId,
                new ArtifactRange(-1, 4)));

        AssertKind(
            "artifact_range_invalid",
            () => _store.GetRange(
                descriptor.ArtifactId,
                new ArtifactRange(0, 0)));
    }

    [Fact]
    public void ReadIsBoundedButStreamOpenIsNot()
    {
        // Larger than MaxReadByteCount (512), so materializing it must be
        // refused rather than quietly truncated.
        var payload = new byte[2048];
        Array.Fill(payload, (byte)7);
        var descriptor = PutBytes(payload);

        AssertKind(
            "artifact_range_too_large",
            () => _store.GetRange(descriptor.ArtifactId));

        using var handle = _store.Open(descriptor.ArtifactId);
        using var copy = new MemoryStream();
        handle.Content.CopyTo(copy);

        Assert.Equal(2048, (int)copy.Length);
        handle.VerifyContentHash();
    }

    [Fact]
    public void OpenRefusesContentWhoseLengthNoLongerMatchesItsRecord()
    {
        var descriptor = PutBytes(new byte[512]);
        var blob = Assert.Single(BlobFiles());

        // Simulate corruption of an immutable body.
        using (var truncated = new FileStream(
                   blob,
                   FileMode.Open,
                   FileAccess.Write,
                   FileShare.None))
        {
            truncated.SetLength(3);
        }

        AssertKind(
            "artifact_content_mismatch",
            () => _store.Open(descriptor.ArtifactId));
    }

    // ---- retention and cleanup -------------------------------------------

    [Fact]
    public void ReadAfterRetentionIsRefusedTruthfully()
    {
        var descriptor = PutBytes(
            Encoding.UTF8.GetBytes("short lived"),
            timeToLive: TimeSpan.FromMinutes(10));

        Now += TimeSpan.FromMinutes(11);

        AssertKind(
            "artifact_expired",
            () => _store.Describe(descriptor.ArtifactId));
        AssertKind(
            "artifact_expired",
            () => _store.GetRange(descriptor.ArtifactId));

        // The record is still on disk: expiry stops retrieval, it does not
        // pretend the artifact never existed.
        Assert.Single(MetaFiles());
    }

    [Fact]
    public void DeleteExpiredRemovesOnlyElapsedRecords()
    {
        var shortLived = PutBytes(
            Encoding.UTF8.GetBytes("gone"),
            timeToLive: TimeSpan.FromMinutes(10));
        var longLived = PutBytes(
            Encoding.UTF8.GetBytes("kept"),
            timeToLive: TimeSpan.FromMinutes(120));

        Now += TimeSpan.FromMinutes(11);

        Assert.Equal(1, _store.DeleteExpired());
        AssertKind(
            "artifact_not_found",
            () => _store.Describe(shortLived.ArtifactId));

        var survivor = _store.Describe(longLived.ArtifactId);
        Assert.Equal(longLived.ArtifactId, survivor.ArtifactId);
    }

    [Fact]
    public void SweepKeepsContentThatIsStillReferenced()
    {
        var descriptor = PutBytes(Encoding.UTF8.GetBytes("live body"));

        var report = _store.Sweep();

        Assert.Equal(0, report.BlobsRemoved);
        Assert.Equal(0, report.ExpiredRemoved);
        Assert.False(report.Incomplete);
        Assert.False(report.ContentReclamationSkipped);
        Assert.Single(BlobFiles());
        Assert.Equal(
            "live body",
            Encoding.UTF8.GetString(
                _store.GetRange(descriptor.ArtifactId).Content.ToArray()));
    }

    [Fact]
    public void SweepReclaimsSharedContentOnlyAfterEveryReferenceIsGone()
    {
        var payload = Encoding.UTF8.GetBytes("shared body");
        var first = PutBytes(payload, timeToLive: TimeSpan.FromMinutes(10));
        var second = PutBytes(payload, timeToLive: TimeSpan.FromMinutes(20));
        Assert.Single(BlobFiles());

        Now += TimeSpan.FromMinutes(11);

        // Both records reference one body; the first expiry pass must not
        // reclaim a body the surviving record still points at.
        var firstPass = _store.Sweep();
        Assert.Equal(1, firstPass.ExpiredRemoved);
        Assert.Equal(0, firstPass.BlobsRemoved);
        Assert.Single(BlobFiles());
        Assert.Equal(
            "shared body",
            Encoding.UTF8.GetString(
                _store.GetRange(second.ArtifactId).Content.ToArray()));

        Now += TimeSpan.FromMinutes(10);

        var secondPass = _store.Sweep();
        Assert.Equal(1, secondPass.ExpiredRemoved);
        Assert.Equal(1, secondPass.BlobsRemoved);
        Assert.Equal(payload.Length, secondPass.BytesReclaimed);
        Assert.Empty(BlobFiles());
        Assert.Empty(MetaFiles());
        Assert.NotEqual(first.ArtifactId, second.ArtifactId);
    }

    [Fact]
    public void SweepDeclinesToReclaimWhenItCannotProveAReferenceSet()
    {
        PutBytes(Encoding.UTF8.GetBytes("a"), timeToLive: TimeSpan.FromMinutes(10));
        PutBytes(Encoding.UTF8.GetBytes("b"), timeToLive: TimeSpan.FromMinutes(10));

        // Rewrite one record so it can no longer be parsed. Its body must not
        // be reclaimed on the strength of a record nobody can read.
        var victim = MetaFiles()[0];
        File.WriteAllText(victim, "{ this is not json");

        Now += TimeSpan.FromMinutes(11);

        var report = _store.Sweep();

        Assert.True(report.ContentReclamationSkipped);
        Assert.Equal(0, report.BlobsRemoved);
        Assert.Equal(2, BlobFiles().Count);
    }

    [Fact]
    public void SweepReportsWhenItStopsAtItsBatchLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            PutBytes(
                Encoding.UTF8.GetBytes("body " + i),
                timeToLive: TimeSpan.FromMinutes(10));
        }

        Now += TimeSpan.FromMinutes(11);

        var limited = new FileSystemArtifactStore(
            new ArtifactStoreOptions
            {
                StateRoot = _stateRoot,
                Clock = () => Now,
                SweepBatchLimit = 2
            });

        var report = limited.Sweep();

        Assert.True(report.Incomplete);
        Assert.Equal(2, report.ExpiredRemoved);
    }

    [Fact]
    public void SweepNeverTouchesStateOutsideTheArtifactRoot()
    {
        // Mutation state lives beside the artifact root. Cleanup must be
        // incapable of reaching it, or an expired result could destroy
        // evidence of an unresolved mutation.
        var ledger = Path.Combine(_stateRoot, "mutation-ledger");
        var workflows = Path.Combine(_stateRoot, "workflows");
        Directory.CreateDirectory(ledger);
        Directory.CreateDirectory(workflows);
        var incident = Path.Combine(ledger, "incident.json");
        var job = Path.Combine(workflows, "job.json");
        File.WriteAllText(incident, "{\"unresolved\":true}");
        File.WriteAllText(job, "{\"jobId\":\"j1\"}");

        PutBytes(
            Encoding.UTF8.GetBytes("expiring"),
            timeToLive: TimeSpan.FromMinutes(10));
        Now += TimeSpan.FromMinutes(11);
        _store.Sweep();

        Assert.Equal("{\"unresolved\":true}", File.ReadAllText(incident));
        Assert.Equal("{\"jobId\":\"j1\"}", File.ReadAllText(job));
        Assert.StartsWith(
            _stateRoot,
            _store.Root,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SweepLeavesForeignFilesInTheStoreDirectoryAlone()
    {
        var foreign = Path.Combine(MetaRoot, "notes.json");
        File.WriteAllText(foreign, "operator notes");

        PutBytes(
            Encoding.UTF8.GetBytes("expiring"),
            timeToLive: TimeSpan.FromMinutes(10));
        Now += TimeSpan.FromMinutes(11);

        _store.Sweep();

        Assert.Equal("operator notes", File.ReadAllText(foreign));
    }

    [Fact]
    public void SweepReclaimsOnlyStaleStoreOwnedIncomingClaims()
    {
        var incoming = Path.Combine(_store.Root, "incoming");
        var stalePut = Path.Combine(
            incoming,
            "put-" + Guid.NewGuid().ToString("N") + ".part");
        var staleGc = Path.Combine(
            incoming,
            "gc-" + Guid.NewGuid().ToString("N") + ".part");
        var freshPut = Path.Combine(
            incoming,
            "put-" + Guid.NewGuid().ToString("N") + ".part");
        var foreign = Path.Combine(incoming, "operator-notes.part");

        File.WriteAllBytes(stalePut, [1, 2, 3]);
        File.WriteAllBytes(staleGc, [4, 5]);
        File.WriteAllBytes(freshPut, [6, 7, 8, 9]);
        File.WriteAllText(foreign, "leave me alone");

        var staleAt = (Now - TimeSpan.FromHours(25)).UtcDateTime;
        File.SetLastWriteTimeUtc(stalePut, staleAt);
        File.SetLastWriteTimeUtc(staleGc, staleAt);
        File.SetLastWriteTimeUtc(foreign, staleAt);
        File.SetLastWriteTimeUtc(
            freshPut,
            (Now - TimeSpan.FromHours(1)).UtcDateTime);

        var report = _store.Sweep();

        Assert.Equal(2, report.StagingFilesRemoved);
        Assert.Equal(5, report.StagingBytesReclaimed);
        Assert.Equal(5, report.BytesReclaimed);
        Assert.False(File.Exists(stalePut));
        Assert.False(File.Exists(staleGc));
        Assert.True(File.Exists(freshPut));
        Assert.Equal("leave me alone", File.ReadAllText(foreign));
    }

    // ---- addressing: ids only, never paths -------------------------------

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32")]
    [InlineData("art_../../../../windows")]
    [InlineData("art_")]
    [InlineData("ART_0123456789abcdef0123456789abcdef")]
    [InlineData("art_0123456789ABCDEF0123456789abcdef")]
    [InlineData("art_0123456789abcdef0123456789abcde")]
    [InlineData("art_0123456789abcdef0123456789abcdefff")]
    [InlineData("art_0123456789abcdef0123456789abcdeg")]
    [InlineData("")]
    [InlineData("C:\\Users\\slooshied\\AppData\\Local\\Temp\\x")]
    public void OnlyOpaqueRuntimeIssuedIdsAreAddressable(string candidate)
    {
        Assert.False(ArtifactId.IsValid(candidate));

        AssertKind(
            "artifact_id_invalid",
            () => _store.Describe(candidate));
        AssertKind(
            "artifact_id_invalid",
            () => _store.GetRange(candidate));
        AssertKind(
            "artifact_id_invalid",
            () => _store.Open(candidate));
    }

    [Fact]
    public void MintedIdsAreOpaqueAndUnique()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 64; i++)
        {
            var id = ArtifactId.NewId();
            Assert.True(ArtifactId.IsValid(id));
            Assert.True(ids.Add(id));
        }
    }

    [Fact]
    public void DescriptorAndReadResultExposeNoFilesystemPath()
    {
        var descriptor = PutBytes(Encoding.UTF8.GetBytes("no paths here"));
        var read = _store.GetRange(descriptor.ArtifactId);

        var propertyNames = typeof(ArtifactDescriptor)
            .GetProperties()
            .Select(static property => property.Name)
            .ToArray();

        Assert.DoesNotContain(
            propertyNames,
            name =>
                name.Contains("path", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("directory", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("location", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("file", StringComparison.OrdinalIgnoreCase));

        var json = JsonSerializer.Serialize(
            new { descriptor, readDescriptor = read.Descriptor },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.DoesNotContain(_stateRoot, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("blobs", json, StringComparison.Ordinal);
        Assert.DoesNotContain("meta", json, StringComparison.Ordinal);
        Assert.Contains(descriptor.ArtifactId, json, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreRootIsAVersionedSubtreeOfTheStateRoot()
    {
        Assert.Equal(
            Path.Combine(
                _stateRoot,
                "artifacts",
                "v" + ArtifactLayout.CurrentLayoutVersion),
            _store.Root);
        Assert.Equal(ArtifactLayout.CurrentLayoutVersion, _store.LayoutVersion);
    }

    [Fact]
    public void RootRefusesADirectoryClaimingAnotherLayoutVersion()
    {
        Directory.CreateDirectory(Path.Combine(_stateRoot, "artifacts"));
        File.WriteAllText(
            Path.Combine(
                _stateRoot,
                "artifacts",
                "v" + ArtifactLayout.CurrentLayoutVersion,
                LayoutManifestName),
            """{"format":"comtool-v2-artifacts","layoutVersion":99,"createdAt":"2026-09-26T12:00:00Z"}""");

        AssertKind(
            "artifact_layout_version_mismatch",
            () => new FileSystemArtifactStore(
                new ArtifactStoreOptions { StateRoot = _stateRoot }));
    }

    [Fact]
    public void RootRefusesADirectoryThatIsNotAnArtifactRoot()
    {
        Directory.CreateDirectory(Path.Combine(_stateRoot, "artifacts"));
        File.WriteAllText(
            Path.Combine(
                _stateRoot,
                "artifacts",
                "v" + ArtifactLayout.CurrentLayoutVersion,
                LayoutManifestName),
            """{"format":"something-else","layoutVersion":1,"createdAt":"2026-09-26T12:00:00Z"}""");

        AssertKind(
            "artifact_layout_format_mismatch",
            () => new FileSystemArtifactStore(
                new ArtifactStoreOptions { StateRoot = _stateRoot }));
    }

    // ---- input validation -------------------------------------------------

    [Fact]
    public void MediaTypeAndEncodingAreValidated()
    {
        AssertKind(
            "artifact_media_type_unsupported",
            () => PutBytes("x"u8.ToArray(), "application/../escape"));

        AssertKind(
            "artifact_encoding_unsupported",
            () => _store.Put(
                new ArtifactWriteRequest
                {
                    Content = new MemoryStream("x"u8.ToArray()),
                    MediaType = ArtifactMediaTypes.ApplicationJson,
                    Encoding = "rot13"
                }));
    }

    [Fact]
    public void RetentionIsBoundedAndValidated()
    {
        AssertKind(
            "artifact_ttl_too_long",
            () => PutBytes("x"u8.ToArray(), timeToLive: TimeSpan.FromHours(3)));

        AssertKind(
            "artifact_ttl_invalid",
            () => PutBytes("x"u8.ToArray(), timeToLive: TimeSpan.Zero));
    }

    [Fact]
    public void ArtifactLargerThanTheStoreBoundIsRefused()
    {
        AssertKind(
            "artifact_too_large",
            () => PutBytes(new byte[5000]));
    }

    [Fact]
    public async Task CancelledWriteLeavesNoContentBehind()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Assert.Throws<OperationCanceledException>(
            () => _store.Put(
                new ArtifactWriteRequest
                {
                    Content = new MemoryStream(new byte[64]),
                    MediaType = ArtifactMediaTypes.ApplicationOctetStream
                },
                cancellation.Token));

        Assert.Empty(BlobFiles());
        Assert.Empty(MetaFiles());
    }

    // ---- helpers ----------------------------------------------------------

    private ArtifactDescriptor PutBytes(
        byte[] payload,
        string mediaType = ArtifactMediaTypes.ApplicationOctetStream,
        TimeSpan? timeToLive = null) =>
        _store.Put(
            new ArtifactWriteRequest
            {
                Content = new MemoryStream(payload),
                MediaType = mediaType,
                TimeToLive = timeToLive
            });

    private string BlobRoot => Path.Combine(_store.Root, "blobs");

    private string MetaRoot => Path.Combine(_store.Root, "meta");

    private IReadOnlyList<string> BlobFiles() =>
        Directory.Exists(BlobRoot)
            ? Directory
                .EnumerateFiles(BlobRoot, "*", SearchOption.AllDirectories)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray()
            : [];

    private IReadOnlyList<string> MetaFiles() =>
        Directory.Exists(MetaRoot)
            ? Directory
                .EnumerateFiles(MetaRoot, "*.json")
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray()
            : [];

    private static string Sha256Hex(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static void AssertKind(string expected, Action action)
    {
        var exception =
            Assert.Throws<ArtifactStoreException>(action);
        Assert.Equal(expected, exception.Kind);
    }
}
