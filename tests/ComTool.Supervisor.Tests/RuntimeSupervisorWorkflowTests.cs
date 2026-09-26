using System.Text.Json;
using ComTool.Protocol;
using ComTool.Supervisor;

namespace ComTool.Supervisor.Tests;

public sealed class RuntimeSupervisorWorkflowTests
{
    [Fact]
    public async Task RuntimeOnlyReadWorkflowRunsAndCanBePolledToCompletion()
    {
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "comtool-v2-workflow-runtime-tests",
            Guid.NewGuid().ToString("N"));

        await using var runtime = new RuntimeSupervisor(
            ["illustrator"],
            new WorkerBrokerOptions
            {
                WorkerExecutablePath =
                    Path.Combine(stateDirectory, "unused-worker.exe")
            },
            stateDirectory);

        var submit = await runtime.ExecuteAsync(
            Request(
                "workflow-health-1",
                "core.workflow.submit",
                """
                {
                  "onError":"stop",
                  "targetLease":false,
                  "steps":[
                    {"id":"health","operation":"core.runtime.health","input":null}
                  ]
                }
                """));

        Assert.True(submit.Ok, submit.Error?.Message);
        Assert.Equal("workflow-health-1", ReadValue(submit).GetProperty("jobId").GetString());

        OperationResult? observedResult = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            observedResult = await runtime.ExecuteAsync(
                Request(
                    $"workflow-health-get-{attempt}",
                    "core.workflow.get",
                    """
                    {"jobId":"workflow-health-1"}
                    """));

            Assert.True(observedResult.Ok, observedResult.Error?.Message);
            if (ReadValue(observedResult).GetProperty("status").GetString() ==
                "completed")
                break;

            await Task.Delay(10);
        }

        Assert.NotNull(observedResult);
        var snapshot = ReadValue(observedResult);
        Assert.Equal("completed", snapshot.GetProperty("status").GetString());
        Assert.Equal(
            "completed",
            snapshot.GetProperty("steps")[0].GetProperty("status").GetString());
        Assert.Equal(
            "core.runtime.health",
            snapshot.GetProperty("steps")[0]
                .GetProperty("result")
                .GetProperty("operation")
                .GetString());
    }

    [Fact]
    public async Task WorkflowSubmitUsesStableIdempotencyAndRejectsChangedInput()
    {
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "comtool-v2-workflow-idempotency-tests",
            Guid.NewGuid().ToString("N"));

        await using var runtime = new RuntimeSupervisor(
            ["illustrator"],
            new WorkerBrokerOptions
            {
                WorkerExecutablePath =
                    Path.Combine(stateDirectory, "unused-worker.exe")
            },
            stateDirectory);

        var first = await runtime.ExecuteAsync(
            Request(
                "workflow-stable-id",
                "core.workflow.submit",
                HealthWorkflow("core.runtime.health")));
        var replay = await runtime.ExecuteAsync(
            Request(
                "workflow-stable-id",
                "core.workflow.submit",
                HealthWorkflow("core.runtime.health")));
        var conflict = await runtime.ExecuteAsync(
            Request(
                "workflow-stable-id",
                "core.workflow.submit",
                HealthWorkflow("core.targets.list")));

        Assert.True(first.Ok, first.Error?.Message);
        Assert.True(replay.Ok, replay.Error?.Message);
        Assert.Equal(
            ReadValue(first).GetProperty("jobId").GetString(),
            ReadValue(replay).GetProperty("jobId").GetString());
        Assert.Equal(OperationStatus.InvalidRequest, conflict.Status);
        Assert.Equal(
            "workflow_request_id_reuse_mismatch",
            conflict.Error?.Kind);
    }

    [Fact]
    public async Task WorkflowRejectsMutationStepsWhenTargetLeaseIsDisabled()
    {
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "comtool-v2-workflow-lease-tests",
            Guid.NewGuid().ToString("N"));

        await using var runtime = new RuntimeSupervisor(
            ["illustrator"],
            new WorkerBrokerOptions
            {
                WorkerExecutablePath =
                    Path.Combine(stateDirectory, "unused-worker.exe")
            },
            stateDirectory);

        var request = Request(
            "workflow-no-lease",
            "core.workflow.submit",
            """
            {
              "onError":"stop",
              "targetLease":false,
              "steps":[
                {"id":"rename","operation":"illustrator.artboard.setName","input":{"property":"document.artboard.active.name","value":"Unsafe"}}
              ]
            }
            """) with
        {
            Target = new TargetRef("illustrator", "test-target", 0)
        };

        var result = await runtime.ExecuteAsync(request);

        Assert.Equal(OperationStatus.InvalidRequest, result.Status);
        Assert.Equal("invalid_workflow_definition", result.Error?.Kind);
        Assert.Contains("targetLease", result.Error?.Message);
    }

    [Fact]
    public async Task WorkflowRejectsCaseVariantFieldsThatDoNotMatchTheSchema()
    {
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "comtool-v2-workflow-field-case-tests",
            Guid.NewGuid().ToString("N"));

        await using var runtime = new RuntimeSupervisor(
            ["illustrator"],
            new WorkerBrokerOptions
            {
                WorkerExecutablePath =
                    Path.Combine(stateDirectory, "unused-worker.exe")
            },
            stateDirectory);

        var result = await runtime.ExecuteAsync(
            Request(
                "workflow-field-case",
                "core.workflow.submit",
                """
                {
                  "onError":"stop",
                  "OnError":"continue",
                  "targetLease":false,
                  "steps":[
                    {"id":"health","operation":"core.runtime.health","input":null}
                  ]
                }
                """));

        Assert.Equal(OperationStatus.InvalidRequest, result.Status);
        Assert.Equal("invalid_workflow_definition", result.Error?.Kind);
    }

    [Fact]
    public async Task InterruptedWorkflowRequiresExplicitResumeAndReusesStepIdentity()
    {
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "comtool-v2-workflow-resume-tests",
            Guid.NewGuid().ToString("N"));
        using var nullInput = JsonDocument.Parse("null");
        var now = DateTimeOffset.UtcNow;
        var orphaned = new WorkflowJobRecord
        {
            SchemaVersion = WorkflowJobStore.CurrentSchemaVersion,
            JobId = "workflow-resume-id",
            RequestFingerprint = "persisted-fingerprint",
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
                        RequestId = "wf-stable-health-step",
                        Operation = "core.runtime.health",
                        Input = nullInput.RootElement.Clone()
                    },
                    Status = "running"
                }
            ],
            Status = "running",
            CurrentStepIndex = 0,
            CancellationRequested = false,
            Revision = 0,
            CreatedAt = now,
            UpdatedAt = now,
            OwnerProcessId = int.MaxValue,
            OwnerProcessStartedAt = DateTimeOffset.UnixEpoch
        };

        _ = new WorkflowJobStore(
                Path.Combine(stateDirectory, "workflows"))
            .CreateOrGet(orphaned);

        await using var runtime = new RuntimeSupervisor(
            ["illustrator"],
            new WorkerBrokerOptions
            {
                WorkerExecutablePath =
                    Path.Combine(stateDirectory, "unused-worker.exe")
            },
            stateDirectory);

        var interrupted = await runtime.ExecuteAsync(
            Request(
                "workflow-resume-get-interrupted",
                "core.workflow.get",
                """
                {"jobId":"workflow-resume-id"}
                """));

        Assert.True(interrupted.Ok, interrupted.Error?.Message);
        var interruptedSnapshot = ReadValue(interrupted);
        Assert.Equal(
            "interrupted",
            interruptedSnapshot.GetProperty("status").GetString());
        var expectedRevision =
            interruptedSnapshot.GetProperty("revision").GetInt64();

        var resumed = await runtime.ExecuteAsync(
            Request(
                "workflow-resume-command",
                "core.workflow.resume",
                $$"""
                {"jobId":"workflow-resume-id","expectedRevision":{{expectedRevision}}}
                """));

        Assert.True(resumed.Ok, resumed.Error?.Message);
        Assert.Equal("running", ReadValue(resumed).GetProperty("status").GetString());

        var completed = await WaitForStatusAsync(
            runtime,
            "workflow-resume-id",
            "workflow-resume-get-completed");
        Assert.Equal("completed", completed.GetProperty("status").GetString());
        Assert.Equal(
            "wf-stable-health-step",
            completed.GetProperty("steps")[0]
                .GetProperty("definition")
                .GetProperty("requestId")
                .GetString());
    }

    [Fact]
    public async Task QueuedWorkflowCanBeCancelledWithCompareAndSetRevision()
    {
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "comtool-v2-workflow-cancel-tests",
            Guid.NewGuid().ToString("N"));
        using var nullInput = JsonDocument.Parse("null");
        var now = DateTimeOffset.UtcNow;
        var queued = new WorkflowJobRecord
        {
            SchemaVersion = WorkflowJobStore.CurrentSchemaVersion,
            JobId = "workflow-cancel-id",
            RequestFingerprint = "queued-fingerprint",
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
                        RequestId = "wf-cancel-health-step",
                        Operation = "core.runtime.health",
                        Input = nullInput.RootElement.Clone()
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

        _ = new WorkflowJobStore(
                Path.Combine(stateDirectory, "workflows"))
            .CreateOrGet(queued);

        await using var runtime = new RuntimeSupervisor(
            ["illustrator"],
            new WorkerBrokerOptions
            {
                WorkerExecutablePath =
                    Path.Combine(stateDirectory, "unused-worker.exe")
            },
            stateDirectory);

        var interrupted = await runtime.ExecuteAsync(
            Request(
                "workflow-cancel-get",
                "core.workflow.get",
                """
                {"jobId":"workflow-cancel-id"}
                """));
        var revision = ReadValue(interrupted)
            .GetProperty("revision")
            .GetInt64();

        var cancelled = await runtime.ExecuteAsync(
            Request(
                "workflow-cancel-command",
                "core.workflow.cancel",
                $$"""
                {"jobId":"workflow-cancel-id","expectedRevision":{{revision}}}
                """));

        Assert.True(cancelled.Ok, cancelled.Error?.Message);
        Assert.Equal("cancelled", ReadValue(cancelled).GetProperty("status").GetString());
        Assert.Equal(
            "skipped",
            ReadValue(cancelled).GetProperty("steps")[0]
                .GetProperty("status")
                .GetString());
    }

    private static string HealthWorkflow(string operation) =>
        $$"""
        {
          "onError":"stop",
          "targetLease":false,
          "steps":[{"id":"step","operation":"{{operation}}","input":null}]
        }
        """;

    private static async Task<JsonElement> WaitForStatusAsync(
        RuntimeSupervisor runtime,
        string jobId,
        string requestIdPrefix)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var result = await runtime.ExecuteAsync(
                Request(
                    $"{requestIdPrefix}-{attempt}",
                    "core.workflow.get",
                    $$"""
                    {"jobId":"{{jobId}}"}
                    """));

            Assert.True(result.Ok, result.Error?.Message);
            var snapshot = ReadValue(result);
            if (snapshot.GetProperty("status").GetString() == "completed")
                return snapshot;

            await Task.Delay(10);
        }

        throw new TimeoutException($"Workflow job '{jobId}' did not complete.");
    }

    private static OperationRequest Request(
        string id,
        string operation,
        string inputJson)
    {
        using var document = JsonDocument.Parse(inputJson);
        return new OperationRequest
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = id,
            Operation = operation,
            Input = document.RootElement.Clone()
        };
    }

    private static JsonElement ReadValue(OperationResult result)
    {
        Assert.NotNull(result.Result);
        Assert.Equal("object", result.Result!.Kind);
        Assert.True(result.Result.Value.HasValue);
        return result.Result.Value.Value;
    }
}
