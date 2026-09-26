using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Supervisor;

internal sealed class MutationLedger
{
    internal const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };

    private readonly string _root;
    private readonly string _recordsDirectory;
    private readonly string _activeDirectory;
    private readonly object _gate = new();

    public MutationLedger(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException(
                "Mutation ledger root must be non-empty.",
                nameof(root));

        _root = Path.GetFullPath(root);
        _recordsDirectory = Path.Combine(_root, "records");
        _activeDirectory = Path.Combine(_root, "active");

        Directory.CreateDirectory(_recordsDirectory);
        Directory.CreateDirectory(_activeDirectory);
    }

    public static string DefaultRoot =>
        Path.Combine(
            RuntimeStateLayout.DefaultRoot,
            "mutation-ledger");

    public MutationLedgerProbeResult Probe(
        HostTargetDescriptor target,
        OperationRequest request,
        MutationClass mutationClass)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);

        if (mutationClass == MutationClass.ReadOnly)
            throw new ArgumentException(
                "Read-only operations must not enter the mutation ledger.",
                nameof(mutationClass));

        lock (_gate)
        {
            var fingerprint =
                CreateRequestFingerprint(request);
            var recordPath = GetRecordPath(
                target.Identity.TargetId,
                request.Id);

            var existing = TryReadRecord(recordPath);
            if (existing is not null)
            {
                if (!string.Equals(
                        existing.RequestFingerprint,
                        fingerprint,
                        StringComparison.Ordinal))
                {
                    return new MutationLedgerProbeResult(
                        MutationLedgerBeginDisposition.RequestIdConflict,
                        existing,
                        null);
                }

                switch (existing.Phase)
                {
                    case MutationLedgerPhase.Completed:
                        return new MutationLedgerProbeResult(
                            MutationLedgerBeginDisposition.ReplayCompleted,
                            existing,
                            DeserializeStoredResult(existing));

                    case MutationLedgerPhase.Prepared:
                    case MutationLedgerPhase.Ambiguous:
                        return new MutationLedgerProbeResult(
                            MutationLedgerBeginDisposition.Unresolved,
                            existing,
                            DeserializeStoredResult(existing));

                    case MutationLedgerPhase.ResolvedChanged:
                    case MutationLedgerPhase.ResolvedUnchanged:
                        return new MutationLedgerProbeResult(
                            MutationLedgerBeginDisposition.Resolved,
                            existing,
                            null);

                    case MutationLedgerPhase.NotStarted:
                        break;

                    default:
                        throw new MutationLedgerException(
                            "mutation_ledger_invalid_phase",
                            $"Unknown mutation ledger phase '{existing.Phase}'.");
                }
            }

            var active = ReadActive(
                target.Identity.TargetId);

            if (active is not null &&
                active.Phase is MutationLedgerPhase.Prepared or
                    MutationLedgerPhase.Ambiguous &&
                !string.Equals(
                    active.RequestId,
                    request.Id,
                    StringComparison.Ordinal))
            {
                return new MutationLedgerProbeResult(
                    MutationLedgerBeginDisposition.TargetHasUnresolvedMutation,
                    active,
                    DeserializeStoredResult(active));
            }

            return new MutationLedgerProbeResult(
                MutationLedgerBeginDisposition.Proceed,
                Record: null,
                StoredResult: null);
        }
    }

    public MutationLedgerBeginResult Begin(
        HostTargetDescriptor target,
        OperationRequest request,
        MutationClass mutationClass)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);

        if (mutationClass == MutationClass.ReadOnly)
            throw new ArgumentException(
                "Read-only operations must not enter the mutation ledger.",
                nameof(mutationClass));

        lock (_gate)
        {
            var fingerprint = CreateRequestFingerprint(request);
            var recordPath = GetRecordPath(
                target.Identity.TargetId,
                request.Id);

            var existing = TryReadRecord(recordPath);
            if (existing is not null)
            {
                if (!string.Equals(
                        existing.RequestFingerprint,
                        fingerprint,
                        StringComparison.Ordinal))
                {
                    return new MutationLedgerBeginResult(
                        MutationLedgerBeginDisposition.RequestIdConflict,
                        existing,
                        null);
                }

                switch (existing.Phase)
                {
                    case MutationLedgerPhase.Completed:
                        return new MutationLedgerBeginResult(
                            MutationLedgerBeginDisposition.ReplayCompleted,
                            existing,
                            DeserializeStoredResult(existing));

                    case MutationLedgerPhase.Prepared:
                    case MutationLedgerPhase.Ambiguous:
                        return new MutationLedgerBeginResult(
                            MutationLedgerBeginDisposition.Unresolved,
                            existing,
                            DeserializeStoredResult(existing));

                    case MutationLedgerPhase.ResolvedChanged:
                    case MutationLedgerPhase.ResolvedUnchanged:
                        return new MutationLedgerBeginResult(
                            MutationLedgerBeginDisposition.Resolved,
                            existing,
                            null);

                    case MutationLedgerPhase.NotStarted:
                        break;

                    default:
                        throw new MutationLedgerException(
                            "mutation_ledger_invalid_phase",
                            $"Unknown mutation ledger phase '{existing.Phase}'.");
                }
            }

            var active = ReadActive(target.Identity.TargetId);
            if (active is not null &&
                active.Phase is MutationLedgerPhase.Prepared or
                    MutationLedgerPhase.Ambiguous &&
                !string.Equals(
                    active.RequestId,
                    request.Id,
                    StringComparison.Ordinal))
            {
                return new MutationLedgerBeginResult(
                    MutationLedgerBeginDisposition.TargetHasUnresolvedMutation,
                    active,
                    DeserializeStoredResult(active));
            }

            var now = DateTimeOffset.UtcNow;
            var prepared = new MutationLedgerRecord
            {
                SchemaVersion = CurrentSchemaVersion,
                TargetId = target.Identity.TargetId,
                Host = target.Identity.Host,
                ProcessId = target.Identity.ProcessId,
                ProcessStartedAt = target.Identity.ProcessStartedAt,
                RequestId = request.Id,
                RequestFingerprint = fingerprint,
                ReconciliationFingerprint =
                    CreateReconciliationFingerprint(
                        request.Postconditions),
                Operation = request.Operation,
                MutationClass = mutationClass,
                Phase = MutationLedgerPhase.Prepared,
                PreparedAt = now,
                UpdatedAt = now,
                IncidentKind = "mutation_prepared"
            };

            // The active marker is written first. A crash between these writes
            // therefore fails closed on restart instead of losing the incident.
            WriteAtomic(
                GetActivePath(target.Identity.TargetId),
                prepared);
            WriteAtomic(recordPath, prepared);

            return new MutationLedgerBeginResult(
                MutationLedgerBeginDisposition.Proceed,
                prepared,
                null);
        }
    }

    public MutationLedgerRecord? GetUnresolvedTarget(
        string targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId))
            throw new ArgumentException(
                "Target id must be non-empty.",
                nameof(targetId));

        lock (_gate)
        {
            var activePath = GetActivePath(targetId);
            if (!File.Exists(activePath))
                return null;

            MutationLedgerRecord active;
            try
            {
                active = ReadRequired(activePath);
            }
            catch (MutationLedgerException)
            {
                return MutationLedgerRecord.CorruptActive(targetId);
            }

            if (!string.Equals(
                    active.TargetId,
                    targetId,
                    StringComparison.Ordinal))
                return MutationLedgerRecord.CorruptActive(targetId);

            var record = TryReadRecord(
                GetRecordPath(targetId, active.RequestId));

            if (record is not null &&
                IsResolvedOrInactive(record.Phase))
            {
                TryDelete(activePath);
                return null;
            }

            if (record is not null)
                active = record;

            return active.Phase is
                    MutationLedgerPhase.Prepared or
                    MutationLedgerPhase.Ambiguous
                ? active
                : null;
        }
    }

    public void Finalize(
        MutationLedgerRecord prepared,
        OperationResult result)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(result);

        lock (_gate)
        {
            var phase = ClassifyTerminalPhase(result);
            var incidentKind =
                phase == MutationLedgerPhase.Ambiguous
                    ? result.Error?.Kind ?? "mutation_outcome_ambiguous"
                    : null;

            var terminal = prepared with
            {
                Phase = phase,
                UpdatedAt = DateTimeOffset.UtcNow,
                IncidentKind = incidentKind,
                ResultJson = ProtocolJson.Serialize(result)
            };

            var recordPath = GetRecordPath(
                terminal.TargetId,
                terminal.RequestId);

            // Persist the terminal outcome before clearing the active marker.
            // A stale active marker is safe because restart recovery consults
            // the terminal request record and ignores completed/not-started work.
            WriteAtomic(recordPath, terminal);

            if (phase == MutationLedgerPhase.Ambiguous)
            {
                WriteAtomic(
                    GetActivePath(terminal.TargetId),
                    terminal);
            }
            else
            {
                TryDeleteIfMatches(
                    GetActivePath(terminal.TargetId),
                    terminal.RequestId);
            }
        }
    }

    public void MarkRuntimeInterrupted(
        MutationLedgerRecord prepared,
        string incidentKind)
    {
        ArgumentNullException.ThrowIfNull(prepared);

        lock (_gate)
        {
            var ambiguous = prepared with
            {
                Phase = MutationLedgerPhase.Ambiguous,
                UpdatedAt = DateTimeOffset.UtcNow,
                IncidentKind = incidentKind
            };

            WriteAtomic(
                GetRecordPath(
                    ambiguous.TargetId,
                    ambiguous.RequestId),
                ambiguous);
            WriteAtomic(
                GetActivePath(ambiguous.TargetId),
                ambiguous);
        }
    }

    public MutationLedgerRecord ResolveIncident(
        string targetId,
        string requestId,
        bool changed,
        string rationale,
        JsonElement? evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rationale);

        lock (_gate)
        {
            var active = ReadActive(targetId)
                ?? throw new MutationLedgerException(
                    "mutation_incident_not_found",
                    "No unresolved mutation incident exists for this target.");

            if (string.Equals(
                    active.RequestId,
                    "corrupt-active-record",
                    StringComparison.Ordinal))
            {
                throw new MutationLedgerException(
                    "mutation_ledger_corrupt",
                    "The active mutation incident record is corrupt and cannot be resolved automatically.");
            }

            if (!string.Equals(
                    active.RequestId,
                    requestId,
                    StringComparison.Ordinal))
            {
                throw new MutationLedgerException(
                    "mutation_incident_request_mismatch",
                    $"Active mutation incident belongs to request '{active.RequestId}', not '{requestId}'.");
            }

            if (active.Phase is not (
                    MutationLedgerPhase.Prepared or
                    MutationLedgerPhase.Ambiguous))
            {
                throw new MutationLedgerException(
                    "mutation_incident_not_unresolved",
                    $"Mutation request '{requestId}' is not in an unresolved phase.");
            }

            var now = DateTimeOffset.UtcNow;
            var resolved = active with
            {
                Phase = changed
                    ? MutationLedgerPhase.ResolvedChanged
                    : MutationLedgerPhase.ResolvedUnchanged,
                UpdatedAt = now,
                IncidentKind = null,
                Resolution = changed
                    ? "known_changed"
                    : "known_unchanged",
                ResolutionRationale = rationale,
                ResolutionEvidenceJson =
                    evidence?.GetRawText(),
                ResolvedAt = now
            };

            var recordPath = GetRecordPath(
                targetId,
                requestId);

            // Persist the terminal resolution before removing the active marker.
            // A stale marker is harmless because ReadActive consults this record
            // and treats resolved phases as terminal.
            WriteAtomic(
                recordPath,
                resolved);

            TryDeleteIfMatches(
                GetActivePath(targetId),
                requestId);

            return resolved;
        }
    }

    public OperationResult? TryGetStoredResult(
        MutationLedgerRecord record) =>
        DeserializeStoredResult(record);

    private MutationLedgerRecord? ReadActive(string targetId)
    {
        var path = GetActivePath(targetId);
        if (!File.Exists(path))
            return null;

        try
        {
            var active = ReadRequired(path);
            if (!string.Equals(
                    active.TargetId,
                    targetId,
                    StringComparison.Ordinal))
                throw new MutationLedgerException(
                    "mutation_ledger_identity_mismatch",
                    "Active mutation ledger record target identity does not match its key.");

            var record = TryReadRecord(
                GetRecordPath(targetId, active.RequestId));

            if (record is not null &&
                IsResolvedOrInactive(record.Phase))
            {
                TryDelete(path);
                return null;
            }

            return record ?? active;
        }
        catch (MutationLedgerException)
        {
            return MutationLedgerRecord.CorruptActive(targetId);
        }
    }

    private MutationLedgerRecord? TryReadRecord(string path)
    {
        if (!File.Exists(path))
            return null;

        return ReadRequired(path);
    }

    private static MutationLedgerRecord ReadRequired(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var record = JsonSerializer.Deserialize<MutationLedgerRecord>(
                bytes,
                Json)
                ?? throw new JsonException(
                    "Mutation ledger record deserialized to null.");

            if (record.SchemaVersion != CurrentSchemaVersion)
            {
                throw new MutationLedgerException(
                    "mutation_ledger_schema_mismatch",
                    $"Unsupported mutation ledger schema version {record.SchemaVersion}.");
            }

            return record;
        }
        catch (MutationLedgerException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                JsonException)
        {
            throw new MutationLedgerException(
                "mutation_ledger_corrupt",
                $"Could not read mutation ledger record '{path}': {ex.Message}",
                ex);
        }
    }

    private void WriteAtomic(
        string path,
        MutationLedgerRecord record)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                "Mutation ledger path has no parent directory."));

        var temporary =
            path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                record,
                Json);

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

            File.Move(
                temporary,
                path,
                overwrite: true);
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException)
        {
            throw new MutationLedgerException(
                "mutation_ledger_write_failed",
                $"Could not durably update mutation ledger '{path}': {ex.Message}",
                ex);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private void TryDeleteIfMatches(
        string path,
        string requestId)
    {
        if (!File.Exists(path))
            return;

        try
        {
            var active = ReadRequired(path);
            if (string.Equals(
                    active.RequestId,
                    requestId,
                    StringComparison.Ordinal))
                TryDelete(path);
        }
        catch (MutationLedgerException)
        {
            // Leave a corrupt marker in place. Recovery must fail closed.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A stale marker is safer than deleting uncertain state. Terminal
            // request records supersede stale active markers during recovery.
        }
    }

    private string GetActivePath(string targetId) =>
        Path.Combine(
            _activeDirectory,
            HashKey(targetId) + ".json");

    private string GetRecordPath(
        string targetId,
        string requestId) =>
        Path.Combine(
            _recordsDirectory,
            HashKey(targetId + "\n" + requestId) + ".json");

    private static string HashKey(string value)
    {
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal static string CreateRequestFingerprint(
        OperationRequest request)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();
        writer.WriteString("host", request.Target?.Host);
        writer.WriteString("targetId", request.Target?.Id);
        writer.WriteString("operation", request.Operation);

        writer.WritePropertyName("input");
        WriteCanonical(writer, request.Input);

        writer.WritePropertyName("preconditions");
        WriteCanonicalCollection(
            writer,
            request.Preconditions);

        writer.WritePropertyName("postconditions");
        WriteCanonicalCollection(
            writer,
            request.Postconditions);

        writer.WriteEndObject();
        writer.Flush();

        return Convert.ToHexString(
                SHA256.HashData(stream.ToArray()))
            .ToLowerInvariant();
    }

    internal static string? CreateReconciliationFingerprint(
        IReadOnlyList<OperationCondition>? postconditions)
    {
        if (postconditions is null || postconditions.Count == 0)
            return null;

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();
        writer.WritePropertyName("postconditions");
        WriteCanonicalCollection(writer, postconditions);
        writer.WriteEndObject();
        writer.Flush();

        return Convert.ToHexString(
                SHA256.HashData(stream.ToArray()))
            .ToLowerInvariant();
    }

    private static void WriteCanonicalCollection(
        Utf8JsonWriter writer,
        IReadOnlyList<OperationCondition>? conditions)
    {
        if (conditions is null)
        {
            writer.WriteNullValue();
            return;
        }

        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(conditions));
        WriteCanonical(writer, document.RootElement);
    }

    private static void WriteCanonical(
        Utf8JsonWriter writer,
        JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value
                             .EnumerateObject()
                             .OrderBy(
                                 static property => property.Name,
                                 StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;

            case JsonValueKind.Number:
                writer.WriteRawValue(
                    value.GetRawText(),
                    skipInputValidation: false);
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;

            default:
                throw new MutationLedgerException(
                    "mutation_ledger_fingerprint_failed",
                    $"Unsupported JSON kind '{value.ValueKind}'.");
        }
    }

    private static OperationResult? DeserializeStoredResult(
        MutationLedgerRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.ResultJson))
            return null;

        try
        {
            return ProtocolJson.DeserializeResult(
                record.ResultJson);
        }
        catch (Exception ex) when (
            ex is JsonException or
                ProtocolValidationException)
        {
            throw new MutationLedgerException(
                "mutation_ledger_result_corrupt",
                "Stored mutation result could not be deserialized.",
                ex);
        }
    }

    private static bool IsResolvedOrInactive(
        MutationLedgerPhase phase) =>
        phase is
            MutationLedgerPhase.Completed or
            MutationLedgerPhase.NotStarted or
            MutationLedgerPhase.ResolvedChanged or
            MutationLedgerPhase.ResolvedUnchanged;

    private static MutationLedgerPhase ClassifyTerminalPhase(
        OperationResult result)
    {
        if (result.Ok ||
            result.Error?.Execution == ExecutionState.Completed)
            return MutationLedgerPhase.Completed;

        if (result.TargetState == TargetState.ReconciliationRequired ||
            result.Error?.Execution is
                ExecutionState.Started or
                ExecutionState.Ambiguous)
            return MutationLedgerPhase.Ambiguous;

        return MutationLedgerPhase.NotStarted;
    }
}

internal enum MutationLedgerPhase
{
    Prepared = 0,
    Completed = 1,
    NotStarted = 2,
    Ambiguous = 3,
    ResolvedChanged = 4,
    ResolvedUnchanged = 5
}

internal enum MutationLedgerBeginDisposition
{
    Proceed = 0,
    ReplayCompleted = 1,
    Unresolved = 2,
    RequestIdConflict = 3,
    TargetHasUnresolvedMutation = 4,
    Resolved = 5
}

internal sealed record MutationLedgerProbeResult(
    MutationLedgerBeginDisposition Disposition,
    MutationLedgerRecord? Record,
    OperationResult? StoredResult);

internal sealed record MutationLedgerBeginResult(
    MutationLedgerBeginDisposition Disposition,
    MutationLedgerRecord Record,
    OperationResult? StoredResult);

internal sealed record MutationLedgerRecord
{
    public required int SchemaVersion { get; init; }
    public required string TargetId { get; init; }
    public required string Host { get; init; }
    public required int ProcessId { get; init; }
    public required DateTimeOffset ProcessStartedAt { get; init; }
    public required string RequestId { get; init; }
    public required string RequestFingerprint { get; init; }
    public string? ReconciliationFingerprint { get; init; }
    public required string Operation { get; init; }
    public required MutationClass MutationClass { get; init; }
    public required MutationLedgerPhase Phase { get; init; }
    public required DateTimeOffset PreparedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public string? IncidentKind { get; init; }
    public string? ResultJson { get; init; }
    public string? Resolution { get; init; }
    public string? ResolutionRationale { get; init; }
    public string? ResolutionEvidenceJson { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }

    public static MutationLedgerRecord CorruptActive(
        string targetId)
    {
        var now = DateTimeOffset.UtcNow;
        return new MutationLedgerRecord
        {
            SchemaVersion = MutationLedger.CurrentSchemaVersion,
            TargetId = targetId,
            Host = "unknown",
            ProcessId = 0,
            ProcessStartedAt = DateTimeOffset.UnixEpoch,
            RequestId = "corrupt-active-record",
            RequestFingerprint = string.Empty,
            Operation = "unknown",
            MutationClass = MutationClass.Unknown,
            Phase = MutationLedgerPhase.Ambiguous,
            PreparedAt = now,
            UpdatedAt = now,
            IncidentKind = "mutation_ledger_corrupt"
        };
    }
}

internal sealed class MutationLedgerException(
    string kind,
    string message,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Kind { get; } = kind;
}
