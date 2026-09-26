using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ComTool.Protocol;
using ComTool.Runtime;

namespace ComTool.Supervisor;

/// <summary>
/// Sequential durable workflow/job control. A workflow is an orchestration
/// record, not a second host dispatcher: every step re-enters ExecuteAsync so
/// the operation catalog, leases, conditions, and mutation ledger remain the
/// authority.
/// </summary>
public sealed partial class RuntimeSupervisor
{
    private const int MaxWorkflowSteps = 64;
    private const int WorkflowLeaseTtlMs = TargetLeaseManager.MaxTtlMs;

    private static readonly JsonSerializerOptions WorkflowInputJson =
        new(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            PropertyNameCaseInsensitive = false
        };

    private readonly WorkflowJobStore _workflowJobStore;
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _workflowTasks =
        new(StringComparer.Ordinal);
    private readonly object _workflowLifecycleGate = new();

    private async Task<OperationResult> SubmitWorkflowAsync(
        OperationRequest request,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        if (request.Policy is not null ||
            request.Preconditions is { Count: > 0 } ||
            request.Postconditions is { Count: > 0 })
        {
            return Failure(
                request,
                OperationStatus.InvalidRequest,
                TargetState.Known,
                "invalid_workflow_request",
                "Workflow submission owns its target lease and step policy; provide conditions on individual steps, not on the submit request.",
                ExecutionState.NotStarted,
                clock.Elapsed.TotalMilliseconds);
        }

        if (!TryParseWorkflowDefinition(
                request,
                out var workflow,
                out var workflowError))
        {
            return Failure(
                request,
                OperationStatus.InvalidRequest,
                TargetState.Known,
                "invalid_workflow_definition",
                workflowError!,
                ExecutionState.NotStarted,
                clock.Elapsed.TotalMilliseconds,
                suggestedActions: ["inspect_workflow_definition"]);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;
        WorkflowJobRecord proposed;
        try
        {
            proposed = new WorkflowJobRecord
            {
                SchemaVersion = WorkflowJobStore.CurrentSchemaVersion,
                JobId = request.Id,
                RequestFingerprint =
                    MutationLedger.CreateRequestFingerprint(request),
                Target = request.Target,
                OwnerProcessId = Environment.ProcessId,
                OwnerProcessStartedAt = GetCurrentProcessStartedAt(),
                TargetLease = workflow!.TargetLease,
                OnError = workflow.OnError,
                Steps = workflow.Steps
                    .Select(step => new WorkflowJobStep
                    {
                        Definition = new WorkflowStepDefinition
                        {
                            Id = step.Id,
                            RequestId = WorkflowStepRequestId(
                                request.Id,
                                step.Id),
                            Operation = step.Operation,
                            Input = step.Input.Clone(),
                            Preconditions = step.Preconditions,
                            Postconditions = step.Postconditions
                        },
                        Status = "pending"
                    })
                    .ToArray(),
                Status = "queued",
                CurrentStepIndex = 0,
                CancellationRequested = false,
                Revision = 0,
                CreatedAt = now,
                UpdatedAt = now
            };
        }
        catch (WorkflowJobStoreException ex)
        {
            return Failure(
                request,
                OperationStatus.Failed,
                TargetState.Known,
                ex.Kind,
                ex.Message,
                ExecutionState.NotStarted,
                clock.Elapsed.TotalMilliseconds,
                suggestedActions: ["inspect_workflow_state_storage"]);
        }

        WorkflowJobCreateResult stored;
        try
        {
            lock (_workflowLifecycleGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return WorkflowRuntimeStoppingFailure(request, clock);

                cancellationToken.ThrowIfCancellationRequested();
                stored = _workflowJobStore.CreateOrGet(proposed);
                if (stored.Disposition == WorkflowJobCreateDisposition.Created ||
                    stored.Record.Status == "queued")
                    StartWorkflowJob(stored.Record.JobId);
            }
        }
        catch (WorkflowJobStoreException ex)
        {
            return Failure(
                request,
                OperationStatus.Failed,
                TargetState.Known,
                ex.Kind,
                ex.Message,
                ExecutionState.NotStarted,
                clock.Elapsed.TotalMilliseconds,
                suggestedActions: ["repair_runtime_state_storage"]);
        }

        if (stored.Disposition ==
            WorkflowJobCreateDisposition.RequestIdConflict)
        {
            return Failure(
                request,
                OperationStatus.InvalidRequest,
                TargetState.Known,
                "workflow_request_id_reuse_mismatch",
                "This workflow request id was already used for different semantic input.",
                ExecutionState.NotStarted,
                clock.Elapsed.TotalMilliseconds,
                suggestedActions:
                [
                    "use_original_workflow_payload",
                    "choose_new_workflow_id_for_new_intent"
                ]);
        }

        var snapshot = _workflowJobStore.Get(stored.Record.JobId)
                       ?? stored.Record;
        return Success(
            request,
            ProtocolValue.From(
                JsonSerializer.SerializeToElement(
                    snapshot,
                    RuntimePayloadJson)),
            TargetState.Known,
            clock.Elapsed.TotalMilliseconds);
    }

    private OperationResult GetWorkflow(
        OperationRequest request,
        Stopwatch clock)
    {
        if (!TryReadWorkflowJobId(request.Input, out var jobId, out var error))
        {
            return Failure(
                request,
                OperationStatus.InvalidRequest,
                TargetState.Known,
                "invalid_workflow_job_id",
                error!,
                ExecutionState.NotStarted,
                clock.Elapsed.TotalMilliseconds);
        }

        try
        {
            var record = _workflowJobStore.Get(jobId!);
            if (record is null)
            {
                return Failure(
                    request,
                    OperationStatus.InvalidRequest,
                    TargetState.Known,
                    "workflow_job_not_found",
                    $"Workflow job '{jobId}' was not found.",
                    ExecutionState.NotStarted,
                    clock.Elapsed.TotalMilliseconds,
                    suggestedActions: ["inspect_workflow_job_id"]);
            }

            return Success(
                request,
                ProtocolValue.From(
                    JsonSerializer.SerializeToElement(
                        record,
                        RuntimePayloadJson)),
                TargetState.Known,
                clock.Elapsed.TotalMilliseconds);
        }
        catch (WorkflowJobStoreException ex)
        {
            return Failure(
                request,
                OperationStatus.Failed,
                TargetState.Known,
                ex.Kind,
                ex.Message,
                ExecutionState.NotStarted,
                clock.Elapsed.TotalMilliseconds,
                suggestedActions: ["inspect_workflow_state_storage"]);
        }
    }

    private OperationResult CancelWorkflow(
        OperationRequest request,
        Stopwatch clock)
    {
        if (!TryReadWorkflowJobControl(
                request.Input,
                out var jobId,
                out var expectedRevision,
                out var error))
        {
            return Failure(
                request,
                OperationStatus.InvalidRequest,
                TargetState.Known,
                "invalid_workflow_cancel",
                error!,
                ExecutionState.NotStarted,
                clock.Elapsed.TotalMilliseconds);
        }

        try
        {
            var updated = _workflowJobStore.Update(
                jobId!,
                current =>
                {
                    if (current.Revision != expectedRevision)
                    {
                        throw new WorkflowJobStateException(
                            "workflow_job_revision_conflict",
                            $"Expected job revision {expectedRevision}, current revision is {current.Revision}.");
                    }

                    if (IsTerminalWorkflowStatus(current.Status))
                    {
                        throw new WorkflowJobStateException(
                            "workflow_job_terminal",
                            $"Workflow job '{jobId}' is already terminal with status '{current.Status}'.");
                    }

                    return current.Status is "queued" or "interrupted"
                        ? current with
                        {
                            Status = "cancelled",
                            CancellationRequested = true,
                            Steps = MarkPendingStepsSkipped(current.Steps)
                        }
                        : current with { CancellationRequested = true };
                });

            if (updated is null)
            {
                return Failure(
                    request,
                    OperationStatus.InvalidRequest,
                    TargetState.Known,
                    "workflow_job_not_found",
                    $"Workflow job '{jobId}' was not found.",
                    ExecutionState.NotStarted,
                    clock.Elapsed.TotalMilliseconds);
            }

            return WorkflowJobSuccess(request, updated, clock);
        }
        catch (WorkflowJobStateException ex)
        {
            return Failure(
                request,
                OperationStatus.InvalidRequest,
                TargetState.Known,
                ex.Kind,
                ex.Message,
                ExecutionState.NotStarted,
                clock.Elapsed.TotalMilliseconds,
                suggestedActions: ["refresh_workflow_job"]);
        }
        catch (WorkflowJobStoreException ex)
        {
            return WorkflowStoreFailure(request, ex, clock);
        }
    }

    private OperationResult ResumeWorkflow(
        OperationRequest request,
        Stopwatch clock)
    {
        if (!TryReadWorkflowJobControl(
                request.Input,
                out var jobId,
                out var expectedRevision,
                out var error))
        {
            return Failure(
                request,
                OperationStatus.InvalidRequest,
                TargetState.Known,
                "invalid_workflow_resume",
                error!,
                ExecutionState.NotStarted,
                clock.Elapsed.TotalMilliseconds);
        }

        WorkflowJobRecord? updated;
        try
        {
            lock (_workflowLifecycleGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return WorkflowRuntimeStoppingFailure(request, clock);

                updated = _workflowJobStore.Update(
                    jobId!,
                    current =>
                    {
                        if (current.Revision != expectedRevision)
                        {
                            throw new WorkflowJobStateException(
                                "workflow_job_revision_conflict",
                                $"Expected job revision {expectedRevision}, current revision is {current.Revision}.");
                        }

                        if (!string.Equals(
                                current.Status,
                                "interrupted",
                                StringComparison.Ordinal))
                        {
                            throw new WorkflowJobStateException(
                                "workflow_job_not_interrupted",
                                "Only an interrupted workflow job can be explicitly resumed.");
                        }

                        return current with
                        {
                            Status = "queued",
                            CancellationRequested = false,
                            OwnerProcessId = Environment.ProcessId,
                            OwnerProcessStartedAt = GetCurrentProcessStartedAt(),
                            ErrorKind = null,
                            ErrorMessage = null
                        };
                    });

                if (updated is not null)
                    StartWorkflowJob(updated.JobId);
            }

            if (updated is null)
            {
                return Failure(
                    request,
                    OperationStatus.InvalidRequest,
                    TargetState.Known,
                    "workflow_job_not_found",
                    $"Workflow job '{jobId}' was not found.",
                    ExecutionState.NotStarted,
                    clock.Elapsed.TotalMilliseconds);
            }

            return WorkflowJobSuccess(
                request,
                _workflowJobStore.Get(updated.JobId) ?? updated,
                clock);
        }
        catch (WorkflowJobStateException ex)
        {
            return Failure(
                request,
                OperationStatus.InvalidRequest,
                TargetState.Known,
                ex.Kind,
                ex.Message,
                ExecutionState.NotStarted,
                clock.Elapsed.TotalMilliseconds,
                suggestedActions: ["inspect_workflow_job_state"]);
        }
        catch (WorkflowJobStoreException ex)
        {
            return WorkflowStoreFailure(request, ex, clock);
        }
    }

    private void StartWorkflowJob(string jobId)
    {
        lock (_workflowLifecycleGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            using var process = Process.GetCurrentProcess();
            var started = new DateTimeOffset(process.StartTime);
            var running = _workflowJobStore.TryStart(
                jobId,
                process.Id,
                started);

            if (running is null)
                return;

            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            if (!_workflowTasks.TryAdd(jobId, completion))
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    try
                    {
                        await RunWorkflowJobAsync(jobId)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            _ = _workflowJobStore.Update(
                                jobId,
                                current => current with
                                {
                                    Status = "failed",
                                    ErrorKind = "workflow_runner_failure",
                                    ErrorMessage = ex.Message
                                });
                        }
                        catch (WorkflowJobStoreException)
                        {
                            // The last durable job record remains the recovery source.
                        }
                    }
                }
                finally
                {
                    _workflowTasks.TryRemove(jobId, out _);
                    completion.TrySetResult();
                }
            });
        }
    }

    private async Task RunWorkflowJobAsync(string jobId)
    {
        TargetSupervisor? targetSupervisor = null;
        string? leaseId = null;
        var job = _workflowJobStore.Get(jobId)
                  ?? throw new WorkflowJobStoreException(
                      "workflow_job_not_found",
                      $"Workflow job '{jobId}' disappeared before execution.");

        try
        {
            if (job.CancellationRequested)
            {
                _ = _workflowJobStore.Update(
                    jobId,
                    current => current with
                    {
                        Status = "cancelled",
                        Steps = MarkPendingStepsSkipped(current.Steps)
                    });
                return;
            }

            if (job.Target is not null)
            {
                var managed = await ResolveLiveTargetAsync(
                        job.Target,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                targetSupervisor = managed.Supervisor;
            }

            if (job.TargetLease)
            {
                if (targetSupervisor is null)
                {
                    throw new WorkflowJobStateException(
                        "workflow_target_required",
                        "This workflow requires a target lease but has no target.");
                }

                var lease = await targetSupervisor.AcquireLeaseAsync(
                        WorkflowLeaseTtlMs,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                leaseId = lease.LeaseId;
            }

            while (true)
            {
                job = _workflowJobStore.Get(jobId)
                      ?? throw new WorkflowJobStoreException(
                          "workflow_job_not_found",
                          $"Workflow job '{jobId}' disappeared during execution.");

                if (job.CancellationRequested)
                {
                    _ = _workflowJobStore.Update(
                        jobId,
                        current => current with
                        {
                            Status = "cancelled",
                            Steps = MarkPendingStepsSkipped(current.Steps)
                        });
                    return;
                }

                if (job.CurrentStepIndex >= job.Steps.Count)
                {
                    var hadFailures = job.Steps.Any(
                        static step => step.Status == "failed");
                    _ = _workflowJobStore.Update(
                        jobId,
                        current => current with
                        {
                            Status = hadFailures
                                ? "completed_with_errors"
                                : "completed",
                            ErrorKind = null,
                            ErrorMessage = null
                        });
                    return;
                }

                if (leaseId is not null && targetSupervisor is not null)
                {
                    var renewed = await targetSupervisor.RenewLeaseAsync(
                            leaseId,
                            WorkflowLeaseTtlMs,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    leaseId = renewed.LeaseId;
                }

                var index = job.CurrentStepIndex;
                var step = job.Steps[index];
                if (step.Status == "completed" ||
                    step.Status == "failed" ||
                    step.Status == "skipped")
                {
                    _ = _workflowJobStore.Update(
                        jobId,
                        current => current with
                        {
                            CurrentStepIndex = index + 1
                        });
                    continue;
                }

                _ = _workflowJobStore.Update(
                    jobId,
                    current => ReplaceWorkflowStep(
                        current with { Status = "running" },
                        index,
                        current.Steps[index] with { Status = "running" }));

                var operationRequest = new OperationRequest
                {
                    ProtocolVersion = ProtocolVersion.Current,
                    Id = step.Definition.RequestId,
                    Target = job.Target,
                    Operation = step.Definition.Operation,
                    Input = step.Definition.Input.Clone(),
                    Policy = leaseId is null
                        ? null
                        : new OperationPolicy(LeaseId: leaseId),
                    Preconditions = step.Definition.Preconditions,
                    Postconditions = step.Definition.Postconditions
                };

                OperationResult result;
                try
                {
                    result = await ExecuteAsync(
                            operationRequest,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var definition = BuiltInOperations.Catalog.GetRequired(
                        step.Definition.Operation);
                    var mutationClass = OperationMutationResolver.Resolve(
                        definition,
                        operationRequest);
                    result = new OperationResult
                    {
                        ProtocolVersion = ProtocolVersion.Current,
                        Id = operationRequest.Id,
                        Operation = operationRequest.Operation,
                        Ok = false,
                        Status = mutationClass == MutationClass.ReadOnly
                            ? OperationStatus.Failed
                            : OperationStatus.ReconciliationRequired,
                        TargetState = mutationClass == MutationClass.ReadOnly
                            ? TargetState.Known
                            : TargetState.ReconciliationRequired,
                        Error = new ProtocolError
                        {
                            Kind = "workflow_step_dispatch_exception",
                            Message = ex.Message,
                            Retryable = false,
                            Execution = mutationClass == MutationClass.ReadOnly
                                ? ExecutionState.NotStarted
                                : ExecutionState.Ambiguous,
                            SuggestedActions = mutationClass == MutationClass.ReadOnly
                                ? ["inspect_workflow_step"]
                                :
                                [
                                    "inspect_mutation_ledger",
                                    "reconcile_target_before_resume"
                                ]
                        }
                    };
                }

                var ambiguous =
                    result.TargetState == TargetState.ReconciliationRequired ||
                    result.Error?.Execution is
                        ExecutionState.Started or
                        ExecutionState.Ambiguous;
                var stepStatus = result.Ok
                    ? "completed"
                    : ambiguous
                        ? "ambiguous"
                        : "failed";

                job = _workflowJobStore.Update(
                          jobId,
                          current =>
                          {
                              var nextIndex = result.Ok ||
                                              (!ambiguous && current.OnError == "continue")
                                  ? index + 1
                                  : index;
                              var nextStatus = ambiguous
                                  ? "reconciliation_required"
                                  : !result.Ok && current.OnError == "stop"
                                      ? "failed"
                                      : nextIndex >= current.Steps.Count
                                          ? current.Steps.Any(
                                              candidate =>
                                                  candidate.Status == "failed") ||
                                            stepStatus == "failed"
                                              ? "completed_with_errors"
                                              : "completed"
                                          : "running";

                              var next = current with
                              {
                                  CurrentStepIndex = nextIndex,
                                  Status = nextStatus,
                                  ErrorKind = result.Ok
                                      ? null
                                      : result.Error?.Kind,
                                  ErrorMessage = result.Ok
                                      ? null
                                      : result.Error?.Message,
                                  Steps = ambiguous ||
                                          (!result.Ok && current.OnError == "stop")
                                      ? MarkPendingStepsSkipped(current.Steps)
                                      : current.Steps
                              };

                              return ReplaceWorkflowStep(
                                  next,
                                  index,
                                  current.Steps[index] with
                                  {
                                      Status = stepStatus,
                                      Result = result
                                  });
                          })
                          ?? throw new WorkflowJobStoreException(
                              "workflow_job_not_found",
                              $"Workflow job '{jobId}' disappeared after a step.");

                if (ambiguous ||
                    (!result.Ok && job.OnError == "stop"))
                    return;
            }
        }
        catch (Exception ex)
        {
            var current = _workflowJobStore.Get(jobId);
            if (current is not null && !IsTerminalWorkflowStatus(current.Status))
            {
                _ = _workflowJobStore.Update(
                    jobId,
                    state => state with
                    {
                        Status = "failed",
                        ErrorKind = ex is TargetLeaseException leaseException
                            ? leaseException.Kind
                            : ex is WorkflowJobStateException workflowException
                                ? workflowException.Kind
                                : "workflow_execution_failure",
                        ErrorMessage = ex.Message
                    });
            }
        }
        finally
        {
            if (leaseId is not null && targetSupervisor is not null)
            {
                try
                {
                    await targetSupervisor.ReleaseLeaseAsync(
                            leaseId,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (TargetLeaseException)
                {
                    // Expiry already released the OS lock; do not conceal the
                    // durable job result with a teardown-only lease error.
                }
            }
        }
    }

    private static WorkflowJobRecord ReplaceWorkflowStep(
        WorkflowJobRecord job,
        int index,
        WorkflowJobStep replacement)
    {
        var steps = job.Steps.ToArray();
        steps[index] = replacement;
        return job with { Steps = steps };
    }

    private static IReadOnlyList<WorkflowJobStep> MarkPendingStepsSkipped(
        IReadOnlyList<WorkflowJobStep> steps) =>
        steps.Select(static step => step.Status == "pending"
                ? step with { Status = "skipped" }
                : step)
            .ToArray();

    private static bool IsTerminalWorkflowStatus(string status) =>
        status is "completed" or "completed_with_errors" or "failed" or
            "reconciliation_required" or "cancelled";

    private static OperationResult WorkflowJobSuccess(
        OperationRequest request,
        WorkflowJobRecord record,
        Stopwatch clock) =>
        Success(
            request,
            ProtocolValue.From(
                JsonSerializer.SerializeToElement(
                    record,
                    RuntimePayloadJson)),
            TargetState.Known,
            clock.Elapsed.TotalMilliseconds);

    private static OperationResult WorkflowStoreFailure(
        OperationRequest request,
        WorkflowJobStoreException exception,
        Stopwatch clock) =>
        Failure(
            request,
            OperationStatus.Failed,
            TargetState.Known,
            exception.Kind,
            exception.Message,
            ExecutionState.NotStarted,
            clock.Elapsed.TotalMilliseconds,
            suggestedActions: ["inspect_workflow_state_storage"]);

    private static OperationResult WorkflowRuntimeStoppingFailure(
        OperationRequest request,
        Stopwatch clock) =>
        Failure(
            request,
            OperationStatus.Failed,
            TargetState.Known,
            "runtime_shutting_down",
            "The runtime is shutting down and cannot accept workflow submissions or resumes.",
            ExecutionState.NotStarted,
            clock.Elapsed.TotalMilliseconds,
            suggestedActions: ["retry_after_runtime_restart"]);

    private static bool TryParseWorkflowDefinition(
        OperationRequest request,
        out WorkflowDefinitionInput? workflow,
        out string? error)
    {
        workflow = null;
        error = null;

        if (request.Input.ValueKind != JsonValueKind.Object)
        {
            error = "Workflow input must be a JSON object.";
            return false;
        }

        if (HasDuplicateWorkflowProperties(request.Input))
        {
            error = "Workflow input contains duplicate JSON object fields.";
            return false;
        }

        try
        {
            workflow = JsonSerializer.Deserialize<WorkflowDefinitionInput>(
                           request.Input,
                           WorkflowInputJson)
                       ?? throw new JsonException(
                           "Workflow input deserialized to null.");
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }

        if (workflow.Steps is null ||
            workflow.Steps.Count is < 1 or > MaxWorkflowSteps)
        {
            error = $"Workflow must contain 1..{MaxWorkflowSteps} steps.";
            return false;
        }

        if (workflow.OnError is not ("stop" or "continue"))
        {
            error = "'onError' must be 'stop' or 'continue'.";
            return false;
        }

        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        var requiresTarget = false;
        var requiresLease = false;

        foreach (var step in workflow.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Id) || step.Id.Length > 128)
            {
                error = "Each workflow step requires a non-empty id of at most 128 characters.";
                return false;
            }

            if (!stepIds.Add(step.Id))
            {
                error = $"Workflow contains duplicate step id '{step.Id}'.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(step.Operation) ||
                step.Operation.Length > 256)
            {
                error = $"Workflow step '{step.Id}' has an invalid operation name.";
                return false;
            }

            if (step.Input.ValueKind == JsonValueKind.Undefined)
            {
                error = $"Workflow step '{step.Id}' is missing input.";
                return false;
            }

            if (step.Operation.StartsWith(
                    "core.workflow.",
                    StringComparison.Ordinal) ||
                step.Operation is
                    "core.target.lease.acquire" or
                    "core.target.lease.renew" or
                    "core.target.lease.release" or
                    "core.target.reconcile" or
                    "core.target.mutation.reconcile" or
                    "core.target.incident.resolve")
            {
                error =
                    $"Workflow step '{step.Id}' uses a runtime control operation that cannot be nested in a workflow.";
                return false;
            }

            if (!BuiltInOperations.Catalog.TryGet(
                    step.Operation,
                    out var operation))
            {
                error =
                    $"Workflow step '{step.Id}' references unregistered operation '{step.Operation}'.";
                return false;
            }

            if (operation.Scope == OperationExecutionScope.Runtime &&
                step.Operation is not (
                    "core.runtime.health" or
                    "core.targets.list" or
                    "core.target.capabilities"))
            {
                error =
                    $"Workflow step '{step.Id}' uses a runtime operation not allowed in a workflow.";
                return false;
            }

            if (operation.Scope == OperationExecutionScope.Host &&
                !operation.RequiresTarget)
            {
                error =
                    $"Workflow host step '{step.Id}' must declare a target requirement.";
                return false;
            }

            requiresTarget |= operation.RequiresTarget;

            var stepRequest = new OperationRequest
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = WorkflowStepRequestId(request.Id, step.Id),
                Target = request.Target,
                Operation = step.Operation,
                Input = step.Input.Clone(),
                Preconditions = step.Preconditions,
                Postconditions = step.Postconditions
            };

            try
            {
                ProtocolJson.ValidateRequest(stepRequest);
                var mutationClass = OperationMutationResolver.Resolve(
                    operation,
                    stepRequest);
                requiresLease |=
                    operation.RequiresLease ||
                    mutationClass != MutationClass.ReadOnly;
            }
            catch (Exception ex) when (
                ex is ProtocolValidationException or
                    OperationMutationPolicyException)
            {
                error = $"Workflow step '{step.Id}' is invalid: {ex.Message}";
                return false;
            }
        }

        if (requiresTarget && request.Target is null)
        {
            error = "This workflow contains host/target operations and requires an explicit target.";
            return false;
        }

        if (requiresLease && !workflow.TargetLease)
        {
            error =
                "A workflow containing mutation-capable steps must set targetLease to true.";
            return false;
        }

        if (workflow.TargetLease && request.Target is null)
        {
            error =
                "A workflow requesting a target lease requires an explicit target.";
            return false;
        }

        return true;
    }

    private static bool HasDuplicateWorkflowProperties(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name) ||
                        HasDuplicateWorkflowProperties(property.Value))
                        return true;
                }

                return false;
            }

            case JsonValueKind.Array:
                return value.EnumerateArray()
                    .Any(HasDuplicateWorkflowProperties);

            default:
                return false;
        }
    }

    private static string WorkflowStepRequestId(
        string jobId,
        string stepId)
    {
        var material = jobId + "\n" + stepId;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return "wf-" + Convert.ToHexString(digest.AsSpan(0, 32))
            .ToLowerInvariant();
    }

    private static bool TryReadWorkflowJobId(
        JsonElement input,
        out string? jobId,
        out string? error)
    {
        jobId = null;
        error = null;

        if (input.ValueKind != JsonValueKind.Object ||
            HasDuplicateWorkflowProperties(input))
        {
            error = "Workflow control input must be an object without duplicate fields.";
            return false;
        }

        foreach (var property in input.EnumerateObject())
        {
            if (property.Name != "jobId" ||
                property.Value.ValueKind != JsonValueKind.String)
            {
                error = $"Unknown or invalid workflow control field '{property.Name}'.";
                return false;
            }

            jobId = property.Value.GetString();
        }

        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 128)
        {
            error = "'jobId' is required and must be at most 128 characters.";
            return false;
        }

        return true;
    }

    private static bool TryReadWorkflowJobControl(
        JsonElement input,
        out string? jobId,
        out long expectedRevision,
        out string? error)
    {
        jobId = null;
        expectedRevision = -1;
        error = null;

        if (input.ValueKind != JsonValueKind.Object ||
            HasDuplicateWorkflowProperties(input))
        {
            error = "Workflow control input must be an object without duplicate fields.";
            return false;
        }

        var hasRevision = false;
        foreach (var property in input.EnumerateObject())
        {
            switch (property.Name)
            {
                case "jobId" when property.Value.ValueKind == JsonValueKind.String:
                    jobId = property.Value.GetString();
                    break;

                case "expectedRevision" when
                    property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetInt64(out expectedRevision) &&
                    expectedRevision >= 0:
                    hasRevision = true;
                    break;

                default:
                    error = $"Unknown or invalid workflow control field '{property.Name}'.";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 128)
        {
            error = "'jobId' is required and must be at most 128 characters.";
            return false;
        }

        if (!hasRevision)
        {
            error = "'expectedRevision' must be a non-negative 64-bit integer.";
            return false;
        }

        return true;
    }

    private async Task StopWorkflowJobsAsync()
    {
        using var process = Process.GetCurrentProcess();
        var processId = process.Id;
        var startedAt = new DateTimeOffset(process.StartTime);
        Task[] tasks;
        lock (_workflowLifecycleGate)
        {
            _ = _workflowJobStore.RequestCancelForRuntimeShutdown(
                processId,
                startedAt);
            tasks = _workflowTasks.Values
                .Select(static source => source.Task)
                .ToArray();
        }

        if (tasks.Length > 0)
            await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static DateTimeOffset GetCurrentProcessStartedAt()
    {
        using var process = Process.GetCurrentProcess();
        return new DateTimeOffset(process.StartTime);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record WorkflowDefinitionInput
{
    public required List<WorkflowStepInput> Steps { get; init; }
    public required string OnError { get; init; }
    public bool TargetLease { get; init; } = true;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record WorkflowStepInput
{
    public required string Id { get; init; }
    public required string Operation { get; init; }
    public required JsonElement Input { get; init; }
    public IReadOnlyList<OperationCondition>? Preconditions { get; init; }
    public IReadOnlyList<OperationCondition>? Postconditions { get; init; }
}

internal sealed class WorkflowJobStateException(
    string kind,
    string message)
    : Exception(message)
{
    public string Kind { get; } = kind;
}
