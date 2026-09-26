using ComTool.Hosts.Abstractions;
using ComTool.Protocol;
using ComTool.Supervisor;

namespace ComTool.Supervisor.Tests;

public sealed class WorkflowJobStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "comtool-v2-workflow-store-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void CreateOrGetReplaysTheSameWorkflowAndRejectsSemanticIdReuse()
    {
        var store = new WorkflowJobStore(_root);
        var created = store.CreateOrGet(Job("same-id", "fp-a"));
        var replay = store.CreateOrGet(Job("same-id", "fp-a"));
        var conflict = store.CreateOrGet(Job("same-id", "fp-b"));

        Assert.Equal(WorkflowJobCreateDisposition.Created, created.Disposition);
        Assert.Equal(WorkflowJobCreateDisposition.Existing, replay.Disposition);
        Assert.Equal("same-id", replay.Record.JobId);
        Assert.Equal(
            WorkflowJobCreateDisposition.RequestIdConflict,
            conflict.Disposition);
        Assert.Equal("fp-a", conflict.Record.RequestFingerprint);
    }

    [Fact]
    public void RuntimeRestartMarksAnOrphanedRunningJobInterrupted()
    {
        var first = new WorkflowJobStore(_root);
        var record = Job("interrupted", "fingerprint") with
        {
            Status = "running",
            OwnerProcessId = int.MaxValue,
            OwnerProcessStartedAt = DateTimeOffset.UnixEpoch
        };
        Assert.Equal(
            WorkflowJobCreateDisposition.Created,
            first.CreateOrGet(record).Disposition);

        var restarted = new WorkflowJobStore(_root);
        var observed = restarted.Get(record.JobId);

        Assert.NotNull(observed);
        Assert.Equal("interrupted", observed.Status);
        Assert.Equal("runtime_interrupted_workflow", observed.ErrorKind);
        Assert.Equal(0, observed.CurrentStepIndex);
    }

    [Fact]
    public void FileLockAndAtomicRecordPreserveCompleteWorkflowSnapshot()
    {
        var store = new WorkflowJobStore(_root);
        var job = Job("atomic", "fingerprint");

        _ = store.CreateOrGet(job);
        var updated = store.Update(
            job.JobId,
            current => current with
            {
                Status = "completed",
                CurrentStepIndex = current.Steps.Count
            });

        var observed = new WorkflowJobStore(_root).Get(job.JobId);

        Assert.NotNull(updated);
        Assert.NotNull(observed);
        Assert.Equal("completed", observed.Status);
        Assert.Equal(1, observed.Revision);
        Assert.Equal(job.RequestFingerprint, observed.RequestFingerprint);
    }

    [Fact]
    public void RuntimeShutdownCancelsOwnedQueuedJobsWithoutRevisingUnchangedJobs()
    {
        var store = new WorkflowJobStore(_root);
        var processId = Environment.ProcessId;
        var processStartedAt = DateTimeOffset.UtcNow;
        var queued = Job("shutdown-queued", "fp-queued") with
        {
            OwnerProcessId = processId,
            OwnerProcessStartedAt = processStartedAt
        };
        var running = Job("shutdown-running", "fp-running") with
        {
            Status = "running",
            OwnerProcessId = processId,
            OwnerProcessStartedAt = processStartedAt,
            Steps =
            [
                Job("step-source", "fp-step").Steps[0] with
                {
                    Status = "running"
                }
            ]
        };
        var unrelated = Job("shutdown-unrelated", "fp-unrelated") with
        {
            OwnerProcessId = processId,
            OwnerProcessStartedAt = processStartedAt.AddYears(-1)
        };

        _ = store.CreateOrGet(queued);
        _ = store.CreateOrGet(running);
        _ = store.CreateOrGet(unrelated);

        var requested = store.RequestCancelForRuntimeShutdown(
            processId,
            processStartedAt);

        Assert.Equal(2, requested.Count);
        var cancelledQueued = store.Get(queued.JobId)!;
        Assert.Equal("cancelled", cancelledQueued.Status);
        Assert.True(cancelledQueued.CancellationRequested);
        Assert.Equal("skipped", cancelledQueued.Steps[0].Status);
        Assert.Equal(1, cancelledQueued.Revision);

        var cancellationRequestedRunning = store.Get(running.JobId)!;
        Assert.Equal("running", cancellationRequestedRunning.Status);
        Assert.True(cancellationRequestedRunning.CancellationRequested);
        Assert.Equal("running", cancellationRequestedRunning.Steps[0].Status);
        Assert.Equal(1, cancellationRequestedRunning.Revision);

        var untouched = store.Get(unrelated.JobId)!;
        Assert.Equal("queued", untouched.Status);
        Assert.False(untouched.CancellationRequested);
        Assert.Equal(0, untouched.Revision);

        Assert.Empty(store.RequestCancelForRuntimeShutdown(
            processId,
            processStartedAt));
        Assert.Equal(1, store.Get(cancelledQueued.JobId)!.Revision);
        Assert.Equal(1, store.Get(cancellationRequestedRunning.JobId)!.Revision);
        Assert.Equal(0, store.Get(untouched.JobId)!.Revision);
    }

    private static WorkflowJobRecord Job(
        string id,
        string fingerprint)
    {
        using var input = System.Text.Json.JsonDocument.Parse("null");
        var now = DateTimeOffset.UtcNow;
        return new WorkflowJobRecord
        {
            SchemaVersion = WorkflowJobStore.CurrentSchemaVersion,
            JobId = id,
            RequestFingerprint = fingerprint,
            Target = null,
            TargetLease = false,
            OnError = "stop",
            Steps =
            [
                new WorkflowJobStep
                {
                    Definition = new WorkflowStepDefinition
                    {
                        Id = "health",
                        RequestId = "wf-health",
                        Operation = "core.runtime.health",
                        Input = input.RootElement.Clone()
                    },
                    Status = "pending"
                }
            ],
            Status = "queued",
            CurrentStepIndex = 0,
            CancellationRequested = false,
            Revision = 0,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
