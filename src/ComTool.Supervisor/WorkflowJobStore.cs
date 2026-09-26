using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Supervisor;

internal sealed record WorkflowStepDefinition
{
    public required string Id { get; init; }
    public required string RequestId { get; init; }
    public required string Operation { get; init; }
    public required JsonElement Input { get; init; }
    public IReadOnlyList<OperationCondition>? Preconditions { get; init; }
    public IReadOnlyList<OperationCondition>? Postconditions { get; init; }
}

internal sealed record WorkflowJobStep
{
    public required WorkflowStepDefinition Definition { get; init; }
    public required string Status { get; init; }
    public OperationResult? Result { get; init; }
}

internal sealed record WorkflowJobRecord
{
    public required int SchemaVersion { get; init; }
    public required string JobId { get; init; }
    public required string RequestFingerprint { get; init; }
    public required TargetRef? Target { get; init; }
    public required bool TargetLease { get; init; }
    public required string OnError { get; init; }
    public required IReadOnlyList<WorkflowJobStep> Steps { get; init; }
    public required string Status { get; init; }
    public required int CurrentStepIndex { get; init; }
    public required bool CancellationRequested { get; init; }
    public required long Revision { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public int? OwnerProcessId { get; init; }
    public DateTimeOffset? OwnerProcessStartedAt { get; init; }
    public string? ErrorKind { get; init; }
    public string? ErrorMessage { get; init; }
}

internal enum WorkflowJobCreateDisposition
{
    Created,
    Existing,
    RequestIdConflict
}

internal sealed record WorkflowJobCreateResult(
    WorkflowJobCreateDisposition Disposition,
    WorkflowJobRecord Record);

internal sealed class WorkflowJobStoreException(
    string kind,
    string message,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Kind { get; } = kind;
}

/// <summary>
/// Small durable store for sequential workflow jobs. Job state is separate from
/// mutation intent: every host step still executes through RuntimeSupervisor
/// and its normal target lease and mutation ledger.
/// </summary>
internal sealed class WorkflowJobStore
{
    internal const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };

    private readonly string _root;
    private readonly object _gate = new();

    public WorkflowJobStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        RecoverOrphanedJobs();
    }

    public static string DefaultRoot =>
        Path.Combine(
            RuntimeStateLayout.DefaultRoot,
            "workflows");

    public WorkflowJobCreateResult CreateOrGet(
        WorkflowJobRecord proposed)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposed.JobId);

        return WithJobLock(proposed.JobId, () =>
        {
            var path = GetPath(proposed.JobId);
            if (File.Exists(path))
            {
                var existing = ReadRequired(path);
                if (!string.Equals(
                        existing.RequestFingerprint,
                        proposed.RequestFingerprint,
                        StringComparison.Ordinal))
                {
                    return new WorkflowJobCreateResult(
                        WorkflowJobCreateDisposition.RequestIdConflict,
                        existing);
                }

                return new WorkflowJobCreateResult(
                    WorkflowJobCreateDisposition.Existing,
                    existing);
            }

            WriteAtomic(path, proposed);
            return new WorkflowJobCreateResult(
                WorkflowJobCreateDisposition.Created,
                proposed);
        });
    }

    public WorkflowJobRecord? Get(string jobId)
    {
        ValidateJobId(jobId);

        return WithJobLock(jobId, () =>
        {
            var path = GetPath(jobId);
            return File.Exists(path)
                ? ReadRequired(path)
                : null;
        });
    }

    public WorkflowJobRecord? TryStart(
        string jobId,
        int processId,
        DateTimeOffset processStartedAt)
    {
        ValidateJobId(jobId);

        return WithJobLock(jobId, () =>
        {
            var path = GetPath(jobId);
            if (!File.Exists(path))
                return null;

            var current = ReadRequired(path);
            if (!string.Equals(
                    current.Status,
                    "queued",
                    StringComparison.Ordinal))
                return null;

            if (current.OwnerProcessId is { } currentOwnerPid &&
                (currentOwnerPid != processId ||
                 current.OwnerProcessStartedAt is not { } currentOwnerStartedAt ||
                 currentOwnerStartedAt.ToUniversalTime().Ticks !=
                     processStartedAt.ToUniversalTime().Ticks))
                return null;

            var next = current with
            {
                Status = "running",
                OwnerProcessId = processId,
                OwnerProcessStartedAt = processStartedAt,
                Revision = current.Revision + 1,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            WriteAtomic(path, next);
            return next;
        });
    }

    public WorkflowJobRecord? Update(
        string jobId,
        Func<WorkflowJobRecord, WorkflowJobRecord> update)
    {
        ValidateJobId(jobId);
        ArgumentNullException.ThrowIfNull(update);

        return WithJobLock(jobId, () =>
        {
            var path = GetPath(jobId);
            if (!File.Exists(path))
                return null;

            var current = ReadRequired(path);
            var proposed = update(current);
            if (ReferenceEquals(proposed, current))
                return current;

            if (!string.Equals(
                    proposed.JobId,
                    current.JobId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    proposed.RequestFingerprint,
                    current.RequestFingerprint,
                    StringComparison.Ordinal))
            {
                throw new WorkflowJobStoreException(
                    "workflow_job_identity_changed",
                    "A workflow state update attempted to change immutable job identity.");
            }

            var next = proposed with
            {
                Revision = current.Revision + 1,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            WriteAtomic(path, next);
            return next;
        });
    }

    public IReadOnlyList<WorkflowJobRecord> RequestCancelForRuntimeShutdown(
        int ownerProcessId,
        DateTimeOffset ownerProcessStartedAt)
    {
        var records = new List<WorkflowJobRecord>();
        foreach (var path in Directory.EnumerateFiles(
                     _root,
                     "*.json",
                     SearchOption.TopDirectoryOnly))
        {
            WorkflowJobRecord? candidate;
            try
            {
                candidate = ReadRequired(path);
            }
            catch (WorkflowJobStoreException)
            {
                continue;
            }

            if (candidate.Status is not ("queued" or "running") ||
                !HasOwner(candidate, ownerProcessId, ownerProcessStartedAt))
                continue;

            var changed = false;
            var updated = Update(
                candidate.JobId,
                current =>
                {
                    if (IsTerminal(current.Status) ||
                        !HasOwner(
                            current,
                            ownerProcessId,
                            ownerProcessStartedAt))
                        return current;

                    if (current.Status == "queued")
                    {
                        changed = true;
                        return current with
                        {
                            Status = "cancelled",
                            CancellationRequested = true,
                            Steps = MarkPendingStepsSkipped(current.Steps)
                        };
                    }

                    if (current.Status == "running" &&
                        !current.CancellationRequested)
                    {
                        changed = true;
                        return current with { CancellationRequested = true };
                    }

                    return current;
                });

            if (changed && updated is not null)
                records.Add(updated);
        }

        return records;
    }

    private void RecoverOrphanedJobs()
    {
        foreach (var path in Directory.EnumerateFiles(
                     _root,
                     "*.json",
                     SearchOption.TopDirectoryOnly))
        {
            WorkflowJobRecord candidate;
            try
            {
                candidate = ReadRequired(path);
            }
            catch (WorkflowJobStoreException)
            {
                // Preserve corrupt records for diagnosis; never reinterpret them
                // as an empty job or silently delete them.
                continue;
            }

            if (candidate.Status is not ("queued" or "running") ||
                IsOwnerAlive(candidate))
                continue;

            _ = Update(
                candidate.JobId,
                current => current.Status is "queued" or "running"
                    ? current with
                    {
                        Status = "interrupted",
                        ErrorKind = "runtime_interrupted_workflow",
                        ErrorMessage =
                            "The owning runtime ended before the workflow reached a terminal state. Inspect the active step before explicitly resuming.",
                        CancellationRequested = false
                    }
                    : current);
        }
    }

    private T WithJobLock<T>(string jobId, Func<T> action)
    {
        lock (_gate)
        {
            var key = HashKey(_root + "\n" + jobId);
            using var mutex = new Mutex(
                initiallyOwned: false,
                $"Local\\ComToolV2Workflow_{key[..40]}");

            var acquired = false;
            try
            {
                try
                {
                    acquired = mutex.WaitOne(TimeSpan.FromSeconds(5));
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                if (!acquired)
                {
                    throw new WorkflowJobStoreException(
                        "workflow_job_store_busy",
                        "Timed out waiting for the durable workflow record lock.");
                }

                return action();
            }
            finally
            {
                if (acquired)
                    mutex.ReleaseMutex();
            }
        }
    }

    private string GetPath(string jobId) =>
        Path.Combine(_root, HashKey(jobId) + ".json");

    private static string HashKey(string value) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static bool IsTerminal(string status) =>
        status is "completed" or "completed_with_errors" or "failed" or
            "reconciliation_required" or "cancelled";

    private static bool HasOwner(
        WorkflowJobRecord record,
        int processId,
        DateTimeOffset processStartedAt) =>
        record.OwnerProcessId == processId &&
        record.OwnerProcessStartedAt is { } ownerStartedAt &&
        ownerStartedAt.ToUniversalTime().Ticks ==
        processStartedAt.ToUniversalTime().Ticks;

    private static IReadOnlyList<WorkflowJobStep> MarkPendingStepsSkipped(
        IReadOnlyList<WorkflowJobStep> steps) =>
        steps.Select(static step => step.Status == "pending"
                ? step with { Status = "skipped" }
                : step)
            .ToArray();

    private static bool IsOwnerAlive(WorkflowJobRecord record)
    {
        if (record.OwnerProcessId is not { } pid ||
            record.OwnerProcessStartedAt is not { } startedAt)
            return false;

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited &&
                   new DateTimeOffset(process.StartTime).ToUniversalTime().Ticks ==
                   startedAt.ToUniversalTime().Ticks;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void ValidateJobId(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        if (jobId.Length > 128)
            throw new ArgumentException(
                "Workflow job id exceeds 128 characters.",
                nameof(jobId));
    }

    private static WorkflowJobRecord ReadRequired(string path)
    {
        try
        {
            var record = JsonSerializer.Deserialize<WorkflowJobRecord>(
                File.ReadAllBytes(path),
                Json);
            if (record is null ||
                record.SchemaVersion != CurrentSchemaVersion)
            {
                throw new WorkflowJobStoreException(
                    "workflow_job_store_schema_mismatch",
                    "Workflow record is missing or uses an unsupported schema version.");
            }

            return record;
        }
        catch (WorkflowJobStoreException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                JsonException)
        {
            throw new WorkflowJobStoreException(
                "workflow_job_store_corrupt",
                $"Could not read workflow record '{path}': {ex.Message}",
                ex);
        }
    }

    private static void WriteAtomic(
        string path,
        WorkflowJobRecord record)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
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
            throw new WorkflowJobStoreException(
                "workflow_job_store_write_failed",
                $"Could not durably update workflow record '{path}': {ex.Message}",
                ex);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
