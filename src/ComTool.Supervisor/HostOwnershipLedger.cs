using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ComTool.Hosts.Abstractions;

namespace ComTool.Supervisor;

/// <summary>
/// Lifecycle state of one durably recorded launch provenance. Only a runtime
/// that actually launched a generation may create an
/// <see cref="Owned"/> record.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<HostOwnershipRecordState>))]
public enum HostOwnershipRecordState
{
    /// <summary>This runtime launched the generation and owns it.</summary>
    [JsonStringEnumMemberName("owned")]
    Owned,

    /// <summary>Ownership was relinquished explicitly; provenance is kept.</summary>
    [JsonStringEnumMemberName("released")]
    Released,

    /// <summary>An owned generation was explicitly quit or terminated.</summary>
    [JsonStringEnumMemberName("quitted")]
    Quitted,

    /// <summary>
    /// A launch was dispatched but its outcome could not be attributed. This
    /// is uncertainty, not ownership, and it never authorizes cleanup.
    /// </summary>
    [JsonStringEnumMemberName("unproven")]
    Unproven
}

/// <summary>
/// Durable, exactly-generation-scoped launch provenance. The stored target id
/// is re-derived from the stored strong identity on every read, so a record
/// whose body disagrees with its own key is treated as corrupt rather than
/// trusted.
/// </summary>
public sealed record HostOwnershipRecord
{
    public const string Format = "comtool-v2-host-ownership";
    public const int CurrentSchemaVersion = 1;

    public required string RecordFormat { get; init; }

    public required int SchemaVersion { get; init; }

    public required string Host { get; init; }

    public required int ProcessId { get; init; }

    public required DateTimeOffset ProcessStartedAt { get; init; }

    public required string ExecutablePath { get; init; }

    public required string HostVersion { get; init; }

    public required string AdapterVersion { get; init; }

    public required string? EndpointIdentity { get; init; }

    public required string TargetId { get; init; }

    public required string ProgId { get; init; }

    public required string LaunchSpecKey { get; init; }

    /// <summary>Identity of the runtime process that launched the generation.</summary>
    public required string RuntimeId { get; init; }

    public required int RuntimeProcessId { get; init; }

    public required string LaunchRequestId { get; init; }

    public required DateTimeOffset LaunchedAt { get; init; }

    public required HostOwnershipRecordState State { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public string? Note { get; init; }

    public HostTargetIdentity ToIdentity() =>
        new()
        {
            Host = Host,
            ProcessId = ProcessId,
            ProcessStartedAt = ProcessStartedAt,
            ExecutablePath = ExecutablePath,
            HostVersion = HostVersion,
            AdapterVersion = AdapterVersion,
            EndpointIdentity = EndpointIdentity
        };

    public static HostOwnershipRecord ForLaunch(
        HostTargetIdentity identity,
        string progId,
        string launchSpecKey,
        string runtimeId,
        int runtimeProcessId,
        string launchRequestId,
        DateTimeOffset launchedAt,
        string? note) =>
        new()
        {
            RecordFormat = Format,
            SchemaVersion = CurrentSchemaVersion,
            Host = identity.Host,
            ProcessId = identity.ProcessId,
            ProcessStartedAt = identity.ProcessStartedAt,
            ExecutablePath = identity.ExecutablePath,
            HostVersion = identity.HostVersion,
            AdapterVersion = identity.AdapterVersion,
            EndpointIdentity = identity.EndpointIdentity,
            TargetId = identity.TargetId,
            ProgId = progId,
            LaunchSpecKey = launchSpecKey,
            RuntimeId = runtimeId,
            RuntimeProcessId = runtimeProcessId,
            LaunchRequestId = launchRequestId,
            LaunchedAt = launchedAt,
            State = HostOwnershipRecordState.Owned,
            UpdatedAt = launchedAt,
            Note = note
        };
}

/// <summary>Terminal outcome of one launch attempt, recorded durably.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HostLaunchAttemptOutcome>))]
public enum HostLaunchAttemptOutcome
{
    [JsonStringEnumMemberName("launched")]
    Launched,

    /// <summary>
    /// A generation was already running and was returned to the caller. This
    /// outcome is permanently non-owning: it never authorizes cleanup.
    /// </summary>
    [JsonStringEnumMemberName("preexisting_without_ownership")]
    PreexistingWithoutOwnership,

    [JsonStringEnumMemberName("refused")]
    Refused,

    [JsonStringEnumMemberName("unproven")]
    Unproven,

    [JsonStringEnumMemberName("timeout")]
    Timeout,

    [JsonStringEnumMemberName("cancelled")]
    Cancelled
}

/// <summary>
/// Durable trace of one launch request, including the ones that were refused
/// or left undecided. A launch attempt is always recorded, so "we never
/// dispatched anything" is also a provable fact.
/// </summary>
public sealed record HostLaunchAttemptRecord
{
    public const string Format = "comtool-v2-host-launch-attempt";
    public const int CurrentSchemaVersion = 1;

    public required string RecordFormat { get; init; }

    public required int SchemaVersion { get; init; }

    public required string LaunchRequestId { get; init; }

    public required string Host { get; init; }

    public required string ProgId { get; init; }

    public required string LaunchSpecKey { get; init; }

    public required string RuntimeId { get; init; }

    public required int RuntimeProcessId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    public required double ElapsedMs { get; init; }

    public required HostLaunchAttemptOutcome Outcome { get; init; }

    public string? TargetId { get; init; }

    public string? AmbiguityKind { get; init; }

    public string? Note { get; init; }
}

public sealed class HostOwnershipLedgerException(
    string kind,
    string message,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Kind { get; } = kind;
}

/// <summary>
/// Durable launch provenance, stored under
/// <c>&lt;stateRoot&gt;/host-ownership</c>.
/// <para>
/// Two separate record kinds are kept on purpose. A generation record exists
/// only for a generation this product actually launched, and is keyed by the
/// exact strong target id. An attempt record exists for every launch request,
/// including refused and undecided ones, and is keyed by request id. Nothing
/// in this store can create ownership out of a process observation: only
/// <see cref="RecordLaunched"/> writes an owning record.
/// </para>
/// </summary>
public sealed class HostOwnershipLedger
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    public HostOwnershipLedger(string stateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);

        Root = Path.Combine(
            Path.GetFullPath(stateRoot),
            "host-ownership");
        GenerationsRoot = Path.Combine(Root, "generations");
        AttemptsRoot = Path.Combine(Root, "attempts");
    }

    public string Root { get; }

    public string GenerationsRoot { get; }

    public string AttemptsRoot { get; }

    /// <summary>
    /// Records that this runtime launched the exact generation described by
    /// <paramref name="identity"/>. The durable target id is re-derived from
    /// the identity, never supplied by the caller.
    /// </summary>
    public HostOwnershipRecord RecordLaunched(
        HostTargetIdentity identity,
        string progId,
        string launchSpecKey,
        string runtimeId,
        string launchRequestId,
        DateTimeOffset launchedAt,
        string? note = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(progId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(launchRequestId);

        RequireUsableIdentity(identity);

        var record = HostOwnershipRecord.ForLaunch(
            identity,
            progId,
            launchSpecKey,
            runtimeId,
            Environment.ProcessId,
            launchRequestId,
            launchedAt,
            note);

        WriteAtomic(
            GenerationPath(record.TargetId),
            record);

        return record;
    }

    /// <summary>
    /// Reads the provenance for one exact generation, or null when this
    /// product has no record for it. A generation this user started therefore
    /// reads as null, which is what keeps it out of reach of cleanup.
    /// </summary>
    public HostOwnershipRecord? TryGetGeneration(string targetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        var path = GenerationPath(targetId);
        if (!File.Exists(path))
            return null;

        var record = Read<HostOwnershipRecord>(path);
        ValidateGenerationRecord(record, path);
        return record;
    }

    public IReadOnlyList<HostOwnershipRecord> ListGenerations() =>
        ReadDirectory(GenerationsRoot, "*.json")
            .Select(path => Read<HostOwnershipRecord>(path))
            .Select(record =>
            {
                ValidateGenerationRecord(
                    record,
                    GenerationPath(record.TargetId));
                return record;
            })
            .OrderBy(static record => record.LaunchedAt)
            .ThenBy(static record => record.TargetId, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Moves an existing record to a new lifecycle state. Refuses to invent a
    /// record: ownership provenance is created by a launch, never by a state
    /// change, so an unknown generation can never become owned here.
    /// </summary>
    public HostOwnershipRecord MarkState(
        string targetId,
        HostOwnershipRecordState state,
        DateTimeOffset updatedAt,
        string? note)
    {
        var existing = TryGetGeneration(targetId)
            ?? throw new HostOwnershipLedgerException(
                "host_ownership_not_found",
                $"No launch provenance exists for generation '{targetId}'.");

        var updated = existing with
        {
            State = state,
            UpdatedAt = updatedAt,
            Note = note ?? existing.Note
        };

        WriteAtomic(GenerationPath(targetId), updated);
        return updated;
    }

    public void RecordAttempt(HostLaunchAttemptRecord attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentException.ThrowIfNullOrWhiteSpace(attempt.LaunchRequestId);

        WriteAtomic(
            AttemptPath(attempt.LaunchRequestId),
            attempt);
    }

    public HostLaunchAttemptRecord? TryGetAttempt(string launchRequestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launchRequestId);

        var path = AttemptPath(launchRequestId);
        if (!File.Exists(path))
            return null;

        var record = Read<HostLaunchAttemptRecord>(path);
        ValidateAttemptRecord(
            record,
            path,
            expectedLaunchRequestId: launchRequestId);
        return record;
    }

    public IReadOnlyList<HostLaunchAttemptRecord> ListAttempts() =>
        ReadDirectory(AttemptsRoot, "*.json")
            .Select(path =>
            {
                var record = Read<HostLaunchAttemptRecord>(path);
                ValidateAttemptRecord(
                    record,
                    path,
                    expectedLaunchRequestId: null);
                return record;
            })
            .OrderBy(static attempt => attempt.StartedAt)
            .ThenBy(
                static attempt => attempt.LaunchRequestId,
                StringComparer.Ordinal)
            .ToArray();

    private static void RequireUsableIdentity(HostTargetIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.Host) ||
            identity.ProcessId <= 0 ||
            string.IsNullOrWhiteSpace(identity.ExecutablePath) ||
            string.IsNullOrWhiteSpace(identity.HostVersion) ||
            string.IsNullOrWhiteSpace(identity.AdapterVersion))
        {
            throw new HostOwnershipLedgerException(
                "host_ownership_identity_incomplete",
                "Launch provenance requires a complete strong host identity.");
        }
    }

    private static void ValidateGenerationRecord(
        HostOwnershipRecord record,
        string path)
    {
        if (!string.Equals(
                record.RecordFormat,
                HostOwnershipRecord.Format,
                StringComparison.Ordinal))
        {
            throw new HostOwnershipLedgerException(
                "host_ownership_corrupt",
                $"Ownership record '{path}' has an unexpected format.");
        }

        if (record.SchemaVersion != HostOwnershipRecord.CurrentSchemaVersion)
        {
            throw new HostOwnershipLedgerException(
                "host_ownership_schema_mismatch",
                $"Ownership record '{path}' uses schema version " +
                $"{record.SchemaVersion}; this runtime supports only " +
                $"{HostOwnershipRecord.CurrentSchemaVersion}.");
        }

        string derived;
        try
        {
            derived = record.ToIdentity().TargetId;
        }
        catch (Exception ex) when (
            ex is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            throw new HostOwnershipLedgerException(
                "host_ownership_corrupt",
                $"Ownership record '{path}' holds an unusable identity: " +
                ex.Message,
                ex);
        }

        if (!string.Equals(
                derived,
                record.TargetId,
                StringComparison.Ordinal))
        {
            // The body and the key disagree, so the record cannot be trusted
            // for any exact-generation decision.
            throw new HostOwnershipLedgerException(
                "host_ownership_corrupt",
                $"Ownership record '{path}' claims target '{record.TargetId}' " +
                $"but its strong identity derives '{derived}'.");
        }
    }

    private static void ValidateAttemptRecord(
        HostLaunchAttemptRecord record,
        string path,
        string? expectedLaunchRequestId)
    {
        if (!string.Equals(
                record.RecordFormat,
                HostLaunchAttemptRecord.Format,
                StringComparison.Ordinal))
        {
            throw new HostOwnershipLedgerException(
                "host_ownership_corrupt",
                $"Launch-attempt record '{path}' has an unexpected format.");
        }

        if (record.SchemaVersion !=
            HostLaunchAttemptRecord.CurrentSchemaVersion)
        {
            throw new HostOwnershipLedgerException(
                "host_ownership_schema_mismatch",
                $"Launch-attempt record '{path}' uses schema version " +
                $"{record.SchemaVersion}; this runtime supports only " +
                $"{HostLaunchAttemptRecord.CurrentSchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(record.LaunchRequestId) ||
            string.IsNullOrWhiteSpace(record.Host) ||
            string.IsNullOrWhiteSpace(record.ProgId) ||
            string.IsNullOrWhiteSpace(record.LaunchSpecKey) ||
            string.IsNullOrWhiteSpace(record.RuntimeId))
        {
            throw new HostOwnershipLedgerException(
                "host_ownership_corrupt",
                $"Launch-attempt record '{path}' is missing required provenance.");
        }

        if (expectedLaunchRequestId is not null &&
            !string.Equals(
                record.LaunchRequestId,
                expectedLaunchRequestId,
                StringComparison.Ordinal))
        {
            throw new HostOwnershipLedgerException(
                "host_ownership_corrupt",
                $"Launch-attempt record '{path}' claims request " +
                $"'{record.LaunchRequestId}' but was read as " +
                $"'{expectedLaunchRequestId}'.");
        }
    }

    private string GenerationPath(string targetId) =>
        Path.Combine(GenerationsRoot, FileNameFor(targetId));

    private string AttemptPath(string launchRequestId) =>
        Path.Combine(AttemptsRoot, FileNameFor(launchRequestId));

    private static string FileNameFor(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        // File names are authority keys. Sanitizing caller-controlled ids by
        // replacement is not injective (for example "a/b" and "a_b"), so it
        // cannot be used for no-replay provenance. Hash the exact UTF-8 value
        // instead; body/key agreement is independently validated on read.
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest).ToLowerInvariant() + ".json";
    }

    private static IReadOnlyList<string> ReadDirectory(
        string directory,
        string pattern)
    {
        if (!Directory.Exists(directory))
            return [];

        try
        {
            return Directory.GetFiles(directory, pattern);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            throw new HostOwnershipLedgerException(
                "host_ownership_unavailable",
                $"Could not list ownership records in '{directory}': " +
                ex.Message,
                ex);
        }
    }

    private static T Read<T>(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(
                       File.ReadAllBytes(path),
                       Json)
                ?? throw new JsonException(
                    "Ownership record deserialized to null.");
        }
        catch (HostOwnershipLedgerException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                JsonException)
        {
            throw new HostOwnershipLedgerException(
                "host_ownership_corrupt",
                $"Could not read ownership record '{path}': {ex.Message}",
                ex);
        }
    }

    private static void WriteAtomic<T>(string path, T record)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                "Host ownership path has no parent directory."));

        var temporary =
            path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);

            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 16 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            throw new HostOwnershipLedgerException(
                "host_ownership_write_failed",
                $"Could not durably update host ownership record '{path}': " +
                ex.Message,
                ex);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: a leftover temp file never grants ownership.
            }
        }
    }

    public static string DescribeAttemptOutcome(
        HostLaunchAttemptOutcome outcome) =>
        outcome switch
        {
            HostLaunchAttemptOutcome.Launched => "launched",
            HostLaunchAttemptOutcome.PreexistingWithoutOwnership =>
                "preexisting_without_ownership",
            HostLaunchAttemptOutcome.Refused => "refused",
            HostLaunchAttemptOutcome.Unproven => "unproven",
            HostLaunchAttemptOutcome.Timeout => "timeout",
            HostLaunchAttemptOutcome.Cancelled => "cancelled",
            _ => outcome.ToString().ToLower(CultureInfo.InvariantCulture)
        };
}
