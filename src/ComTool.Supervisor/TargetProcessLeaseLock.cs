using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ComTool.Supervisor;

/// <summary>
/// Cross-process ownership handle for a mutation lease on one strong target
/// identity. The open FileStream is the lock; the file contents are diagnostic
/// metadata only. Kernel handle teardown releases ownership after process crash.
/// </summary>
internal sealed class TargetProcessLeaseLock : IDisposable
{
    private static readonly JsonSerializerOptions MetadataJson =
        new(JsonSerializerDefaults.Web);

    private readonly FileStream _stream;
    private int _disposed;

    private TargetProcessLeaseLock(
        string path,
        FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    public string Path { get; }

    public static string DefaultDirectory =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "ComToolV2",
            "target-lease-locks");

    public static TargetProcessLeaseLock Acquire(
        string targetId,
        string? directory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        var lockDirectory = string.IsNullOrWhiteSpace(directory)
            ? DefaultDirectory
            : System.IO.Path.GetFullPath(directory);

        Directory.CreateDirectory(lockDirectory);

        var path = System.IO.Path.Combine(
            lockDirectory,
            HashTargetId(targetId) + ".lock");

        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough);
        }
        catch (IOException ex)
        {
            throw new TargetLeaseException(
                "target_leased_external",
                "Another COM Tool runtime currently owns the mutation lease for this target.",
                retryable: true,
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new TargetLeaseException(
                "target_lease_lock_unavailable",
                $"Could not acquire the cross-process target lease lock: {ex.Message}",
                retryable: false,
                ex);
        }

        var instance = new TargetProcessLeaseLock(
            path,
            stream);

        try
        {
            instance.WriteMetadata(targetId);
            return instance;
        }
        catch
        {
            instance.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _stream.Dispose();
    }

    private void WriteMetadata(string targetId)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                targetId,
                processId = Environment.ProcessId,
                processStartedAt = System.Diagnostics.Process
                    .GetCurrentProcess()
                    .StartTime
                    .ToUniversalTime(),
                acquiredAt = DateTimeOffset.UtcNow
            },
            MetadataJson);

        try
        {
            _stream.SetLength(0);
            _stream.Position = 0;
            _stream.Write(payload);
            _stream.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException)
        {
            throw new TargetLeaseException(
                "target_lease_lock_write_failed",
                $"Could not persist target lease lock metadata: {ex.Message}",
                retryable: false,
                ex);
        }
    }

    private static string HashTargetId(string targetId) =>
        Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(targetId)))
            .ToLowerInvariant();
}
