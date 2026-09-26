using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using ComTool.Broker.Protocol;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;
using ComTool.Runtime;
using ComTool.Transport.Pipe;

namespace ComTool.Supervisor;

/// <summary>
/// Owns one authenticated worker process and one serialized command lane for a
/// specific Adobe target generation.
/// </summary>
public sealed class WorkerBrokerClient : IAsyncDisposable
{
    private const string TokenEnvironmentVariable = "COMTOOL_V2_WORKER_TOKEN";

    private readonly WorkerBrokerOptions _options;
    private readonly HostTargetDescriptor _target;
    private readonly TargetStateMachine _state;
    private readonly Process _process;
    private readonly DateTimeOffset _processStartedAt;
    private readonly NamedPipeServerStream _pipe;
    private readonly LengthPrefixedFramedStream _framed;
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    private long _sequence;
    private int _disposed;

    private WorkerBrokerClient(
        WorkerBrokerOptions options,
        HostTargetDescriptor target,
        TargetStateMachine state,
        Process process,
        DateTimeOffset processStartedAt,
        NamedPipeServerStream pipe,
        LengthPrefixedFramedStream framed,
        BrokerWorkerStatus worker)
    {
        _options = options;
        _target = target;
        _state = state;
        _process = process;
        _processStartedAt = processStartedAt;
        _pipe = pipe;
        _framed = framed;
        Worker = worker;
    }

    public HostTargetDescriptor Target => _target;

    public BrokerWorkerStatus Worker { get; }

    public TargetStateSnapshot State => _state.Snapshot();

    public bool IsAlive
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0)
                return false;

            try
            {
                return !_process.HasExited && _pipe.IsConnected;
            }
            catch
            {
                return false;
            }
        }
    }

    public static async Task<WorkerBrokerClient> LaunchAsync(
        HostTargetDescriptor target,
        WorkerBrokerOptions options,
        TargetStateMachine? stateMachine = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);

        var workerPath = Path.GetFullPath(options.WorkerExecutablePath);
        if (!File.Exists(workerPath))
            throw new FileNotFoundException("Worker executable was not found.", workerPath);

        if (options.MaxFrameBytes < 256)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxFrameBytes must be at least 256.");

        var state = stateMachine ?? new TargetStateMachine();
        var pipeName = $"comtool-v2-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        Process? process = null;
        LengthPrefixedFramedStream? framed = null;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = workerPath,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = options.CreateNoWindow,
                WorkingDirectory = Path.GetDirectoryName(workerPath)
                    ?? Environment.CurrentDirectory
            };

            startInfo.ArgumentList.Add("--pipe");
            startInfo.ArgumentList.Add(pipeName);
            startInfo.ArgumentList.Add("--host");
            startInfo.ArgumentList.Add(target.Identity.Host);
            startInfo.ArgumentList.Add("--target");
            startInfo.ArgumentList.Add(target.Identity.TargetId);
            startInfo.Environment[TokenEnvironmentVariable] = token;

            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Worker process failed to start.");

            process.Refresh();
            var processStartedAt = new DateTimeOffset(process.StartTime);

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            connectCts.CancelAfter(options.ConnectTimeout);

            await WorkerProcessStartup
                .WaitForConnectionOrExitAsync(
                    pipe,
                    process,
                    connectCts.Token)
                .ConfigureAwait(false);

            framed = new LengthPrefixedFramedStream(
                pipe,
                options.MaxFrameBytes,
                leaveOpen: true);

            using var helloFrame = await framed
                .ReadAsync(connectCts.Token)
                .ConfigureAwait(false);

            var hello = BrokerJson.DeserializeHello(helloFrame.Span);
            var validationError = ValidateHello(
                hello,
                token,
                process,
                processStartedAt,
                target);

            if (validationError is not null)
            {
                await framed.WriteAsync(
                    BrokerJson.Serialize(new WorkerHelloAck
                    {
                        BrokerVersion = BrokerVersion.Current,
                        Accepted = false,
                        Error = validationError
                    }),
                    cancellationToken).ConfigureAwait(false);

                throw new InvalidDataException(validationError);
            }

            await framed.WriteAsync(
                BrokerJson.Serialize(new WorkerHelloAck
                {
                    BrokerVersion = BrokerVersion.Current,
                    Accepted = true
                }),
                cancellationToken).ConfigureAwait(false);

            var worker = new BrokerWorkerStatus
            {
                WorkerId = hello.WorkerId,
                Mode = hello.Mode,
                ProcessId = hello.ProcessId,
                ProcessStartedAt = hello.ProcessStartedAt,
                Apartment = hello.Apartment,
                TargetId = hello.TargetId
            };

            state.MarkReconnected();

            return new WorkerBrokerClient(
                options,
                target,
                state,
                process,
                processStartedAt,
                pipe,
                framed,
                worker);
        }
        catch
        {
            if (process is not null)
                await TerminateOwnedProcessAsync(process, cancellationToken)
                    .ConfigureAwait(false);

            if (framed is not null)
                await framed.DisposeAsync().ConfigureAwait(false);

            await pipe.DisposeAsync().ConfigureAwait(false);
            process?.Dispose();
            throw;
        }
    }

    public async Task<BrokerWorkerStatus> PingAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var requestId = $"ping-{Guid.NewGuid():N}";
        var roundTrip = await SendAsync(
            BrokerCommandKind.Ping,
            requestId,
            mutationClass: null,
            operation: null,
            _options.CommandTimeout,
            cancellationToken).ConfigureAwait(false);

        var response = roundTrip.Response;
        if (!response.Ok ||
            response.Kind != BrokerResponseKind.Pong ||
            response.Worker is null)
        {
            throw CreateBrokerException(response);
        }

        _state.MarkReconnected();
        return response.Worker;
    }

    public async Task<OperationResult> ExecuteAsync(
        OperationRequest request,
        TimeSpan? watchdog = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        var totalClock = Stopwatch.StartNew();

        if (request.ProtocolVersion != ProtocolVersion.Current)
        {
            return InvalidRequest(
                request,
                "unsupported_protocol_version",
                $"Protocol version {request.ProtocolVersion} is not supported.",
                ExecutionState.NotStarted);
        }

        if (request.Preconditions is { Count: > 0 } ||
            request.Postconditions is { Count: > 0 })
        {
            return InvalidRequest(
                request,
                "conditions_require_runtime_supervisor",
                "Preconditions and postconditions must be orchestrated by the runtime supervisor and cannot be dispatched directly to a worker.",
                ExecutionState.NotStarted);
        }

        if (!BuiltInOperations.Catalog.TryGet(request.Operation, out var definition))
        {
            return InvalidRequest(
                request,
                "unsupported_operation",
                $"Operation '{request.Operation}' is not registered.",
                ExecutionState.NotStarted,
                OperationStatus.UnsupportedOperation);
        }

        if (definition.RequiresTarget &&
            (request.Target is null ||
             !string.Equals(
                 request.Target.Id,
                 _target.Identity.TargetId,
                 StringComparison.Ordinal) ||
             !string.Equals(
                 request.Target.Host,
                 _target.Identity.Host,
                 StringComparison.Ordinal)))
        {
            return InvalidRequest(
                request,
                "target_mismatch",
                "Request target does not match this brokered target.",
                ExecutionState.NotStarted);
        }

        MutationClass mutationClass;
        try
        {
            mutationClass = OperationMutationResolver.Resolve(
                definition,
                request);
        }
        catch (OperationMutationPolicyException ex)
        {
            return InvalidRequest(
                request,
                ex.Kind,
                ex.Message,
                ExecutionState.NotStarted);
        }

        try
        {
            _state.EnsureOperationAllowed(mutationClass);
        }
        catch (TargetStateException ex)
        {
            return new OperationResult
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = request.Id,
                Operation = request.Operation,
                Ok = false,
                Status = ex.TargetState == TargetState.ReconciliationRequired
                    ? OperationStatus.ReconciliationRequired
                    : ex.TargetState == TargetState.Unavailable
                        ? OperationStatus.TargetUnavailable
                        : OperationStatus.HostBusy,
                TargetState = ex.TargetState,
                Error = new ProtocolError
                {
                    Kind = ex.Kind,
                    Message = ex.Message,
                    Retryable = ex.Retryable,
                    Execution = ExecutionState.NotStarted,
                    SuggestedActions =
                        ex.TargetState == TargetState.ReconciliationRequired
                            ? ["reconcile_target_before_mutation"]
                            : ["ping_or_reconnect_target"]
                },
                Timing = new OperationTiming(
                    TotalMs: totalClock.Elapsed.TotalMilliseconds)
            };
        }

        BrokerRoundTrip roundTrip;
        try
        {
            roundTrip = await SendAsync(
                BrokerCommandKind.Operation,
                request.Id,
                mutationClass,
                request,
                watchdog ?? _options.CommandTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (WorkerDispatchException ex)
        {
            ApplyDispatchFailure(mutationClass, ex);

            var targetState = _state.Snapshot().State;
            return new OperationResult
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = request.Id,
                Operation = request.Operation,
                Ok = false,
                Status = targetState == TargetState.ReconciliationRequired
                    ? OperationStatus.ReconciliationRequired
                    : OperationStatus.TargetUnavailable,
                TargetState = targetState,
                Error = new ProtocolError
                {
                    Kind = ex.Kind,
                    Message = ex.Message,
                    Retryable = false,
                    Execution = ex.Execution,
                    HResult = ex.HResultCode,
                    HResultHex = ex.HResultCode is null
                        ? null
                        : $"0x{unchecked((uint)ex.HResultCode.Value):X8}",
                    SuggestedActions =
                        targetState == TargetState.ReconciliationRequired
                            ? ["relaunch_worker", "reconcile_target_before_mutation"]
                            : ["relaunch_worker"]
                },
                Timing = new OperationTiming(
                    QueueMs: ex.QueueMs,
                    TotalMs: totalClock.Elapsed.TotalMilliseconds)
            };
        }

        var response = roundTrip.Response;

        if (response.OperationResult is not null)
        {
            var result = response.OperationResult;
            ApplyOperationOutcome(mutationClass, result);

            var state = _state.Snapshot().State;
            var timing = result.Timing ?? new OperationTiming();

            return result with
            {
                TargetState = state,
                Timing = timing with
                {
                    QueueMs = roundTrip.QueueMs,
                    TotalMs = totalClock.Elapsed.TotalMilliseconds
                }
            };
        }

        var brokerError = response.Error ?? new ProtocolError
        {
            Kind = "invalid_worker_response",
            Message = "Worker response did not contain an operation result.",
            Retryable = false,
            Execution = ExecutionState.Ambiguous,
            SuggestedActions = ["relaunch_worker", "reconcile_target_before_mutation"]
        };

        if (brokerError.Execution == ExecutionState.Ambiguous)
            _state.MarkAmbiguousExecution(
                mutationClass,
                brokerError.Kind);

        var brokerState = _state.Snapshot().State;
        return new OperationResult
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = false,
            Status = brokerState == TargetState.ReconciliationRequired
                ? OperationStatus.ReconciliationRequired
                : OperationStatus.Failed,
            TargetState = brokerState,
            Error = brokerError,
            Timing = new OperationTiming(
                QueueMs: roundTrip.QueueMs,
                TotalMs: totalClock.Elapsed.TotalMilliseconds)
        };
    }

    public async Task<BrokerReconciliation> ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var requestId = $"reconcile-{Guid.NewGuid():N}";
        var roundTrip = await SendAsync(
            BrokerCommandKind.Reconcile,
            requestId,
            mutationClass: null,
            operation: null,
            _options.CommandTimeout,
            cancellationToken).ConfigureAwait(false);

        var response = roundTrip.Response;
        if (response.Reconciliation is null)
            throw CreateBrokerException(response);

        // This layer reports host-side reconciliation evidence only.
        // The long-lived TargetSupervisor owns the ambiguity state and decides
        // whether that evidence is sufficient to resolve an incident.
        return response.Reconciliation;
    }

    public async Task ShutdownAsync(
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0 || _process.HasExited)
            return;

        try
        {
            var requestId = $"shutdown-{Guid.NewGuid():N}";
            var roundTrip = await SendAsync(
                BrokerCommandKind.Shutdown,
                requestId,
                mutationClass: null,
                operation: null,
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);

            if (!roundTrip.Response.Ok)
                throw CreateBrokerException(roundTrip.Response);

            using var exitCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            exitCts.CancelAfter(TimeSpan.FromSeconds(5));
            await _process
                .WaitForExitAsync(exitCts.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            await KillOwnedWorkerAsync(CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    await ShutdownCoreAsync().ConfigureAwait(false);
                }
                catch
                {
                    await KillOwnedWorkerAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await _framed.DisposeAsync().ConfigureAwait(false);
            await _pipe.DisposeAsync().ConfigureAwait(false);
            _process.Dispose();
            _commandGate.Dispose();
        }
    }

    private async Task ShutdownCoreAsync()
    {
        var requestId = $"shutdown-{Guid.NewGuid():N}";
        _ = await SendAsync(
            BrokerCommandKind.Shutdown,
            requestId,
            mutationClass: null,
            operation: null,
            TimeSpan.FromSeconds(2),
            CancellationToken.None).ConfigureAwait(false);

        using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _process.WaitForExitAsync(exitCts.Token).ConfigureAwait(false);
    }

    private async Task<BrokerRoundTrip> SendAsync(
        BrokerCommandKind kind,
        string requestId,
        MutationClass? mutationClass,
        OperationRequest? operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var queuedAt = Stopwatch.StartNew();
        await _commandGate
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        var queueMs = queuedAt.Elapsed.TotalMilliseconds;

        var sequence = checked(_sequence + 1);
        var dispatchBegan = false;

        try
        {
            EnsureWorkerAlive();

            var command = new BrokerCommand
            {
                BrokerVersion = BrokerVersion.Current,
                Sequence = sequence,
                RequestId = requestId,
                Kind = kind,
                MutationClass = mutationClass,
                Operation = operation
            };

            using var commandCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            commandCts.CancelAfter(timeout);

            try
            {
                // Once a frame write begins, a mutating command is conservatively
                // considered potentially dispatched until the worker proves otherwise.
                dispatchBegan = true;
                await _framed
                    .WriteAsync(
                        BrokerJson.Serialize(command),
                        commandCts.Token)
                    .ConfigureAwait(false);

                _sequence = sequence;

                using var responseFrame = await _framed
                    .ReadAsync(commandCts.Token)
                    .ConfigureAwait(false);

                var response = BrokerJson.DeserializeResponse(responseFrame.Span);
                if (response.BrokerVersion != BrokerVersion.Current ||
                    response.Sequence != sequence ||
                    !string.Equals(
                        response.RequestId,
                        requestId,
                        StringComparison.Ordinal))
                {
                    throw new WorkerDispatchException(
                        "broker_protocol_violation",
                        "Worker response correlation/version check failed.",
                        ExecutionState.Ambiguous,
                        queueMs: queueMs);
                }

                return new BrokerRoundTrip(response, queueMs);
            }
            catch (OperationCanceledException ex)
                when (dispatchBegan)
            {
                await KillOwnedWorkerAsync(CancellationToken.None)
                    .ConfigureAwait(false);

                throw new WorkerDispatchException(
                    cancellationToken.IsCancellationRequested
                        ? "operation_cancelled_after_dispatch"
                        : "worker_watchdog_timeout",
                    cancellationToken.IsCancellationRequested
                        ? "The caller cancelled after dispatch began; execution outcome is not assumed."
                        : $"Worker did not answer within {timeout.TotalMilliseconds:0} ms and was terminated.",
                    mutationClass is null or MutationClass.ReadOnly
                        ? ExecutionState.Started
                        : ExecutionState.Ambiguous,
                    queueMs: queueMs,
                    innerException: ex);
            }
            catch (Exception ex)
                when (ex is IOException or EndOfStreamException or InvalidDataException)
            {
                await KillOwnedWorkerAsync(CancellationToken.None)
                    .ConfigureAwait(false);

                throw new WorkerDispatchException(
                    "worker_channel_lost",
                    $"Worker channel failed: {ex.Message}",
                    dispatchBegan && mutationClass is not null and not MutationClass.ReadOnly
                        ? ExecutionState.Ambiguous
                        : dispatchBegan
                            ? ExecutionState.Started
                            : ExecutionState.NotStarted,
                    hResult: ex.HResult,
                    queueMs: queueMs,
                    innerException: ex);
            }
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private void ApplyDispatchFailure(
        MutationClass mutationClass,
        WorkerDispatchException error)
    {
        if (error.Execution == ExecutionState.Ambiguous)
        {
            _state.MarkAmbiguousExecution(
                mutationClass,
                error.Kind);
            return;
        }

        _state.MarkWorkerLost(
            executionStarted: error.Execution != ExecutionState.NotStarted,
            mutationClass);
    }

    private void ApplyOperationOutcome(
        MutationClass mutationClass,
        OperationResult result)
    {
        if (result.Ok)
        {
            _state.MarkCompleted(mutationClass);
            return;
        }

        if (result.Error?.Execution == ExecutionState.Ambiguous)
        {
            _state.MarkAmbiguousExecution(
                mutationClass,
                result.Error.Kind);
            return;
        }

        switch (result.TargetState)
        {
            case TargetState.Busy:
                _state.MarkBusy();
                break;
            case TargetState.Unavailable:
                _state.MarkHostUnavailable(result.Error?.Kind ?? "host_unavailable");
                break;
            case TargetState.ReconciliationRequired:
                _state.MarkAmbiguousExecution(
                    mutationClass,
                    result.Error?.Kind ?? "host_reported_ambiguity");
                break;
        }
    }

    private async Task KillOwnedWorkerAsync(CancellationToken cancellationToken)
    {
        if (_process.HasExited)
            return;

        _process.Refresh();

        DateTimeOffset currentStart;
        try
        {
            currentStart = new DateTimeOffset(_process.StartTime);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (currentStart.ToUniversalTime().Ticks !=
            _processStartedAt.ToUniversalTime().Ticks)
        {
            throw new InvalidOperationException(
                "Worker PID identity changed; refusing to terminate an unverified process.");
        }

        _process.Kill(entireProcessTree: true);

        using var killCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        killCts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            await _process
                .WaitForExitAsync(killCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The owned process has already received termination. Do not turn a
            // cleanup timeout into permission to target some other PID.
        }
    }

    private void EnsureWorkerAlive()
    {
        if (_process.HasExited || !_pipe.IsConnected)
            throw new WorkerDispatchException(
                "worker_unavailable",
                "The owned worker is not connected.",
                ExecutionState.NotStarted);
    }

    private static string? ValidateHello(
        WorkerHello hello,
        string expectedToken,
        Process process,
        DateTimeOffset expectedProcessStartedAt,
        HostTargetDescriptor target)
    {
        if (hello.BrokerVersion != BrokerVersion.Current)
            return "Worker broker protocol version mismatch.";

        if (!FixedTimeEquals(hello.Token, expectedToken))
            return "Worker authentication token mismatch.";

        if (hello.ProcessId != process.Id)
            return "Worker process ID does not match the launched child.";

        if (hello.ProcessStartedAt.ToUniversalTime().Ticks !=
            expectedProcessStartedAt.ToUniversalTime().Ticks)
            return "Worker process creation time does not match the launched child.";

        if (!string.Equals(hello.Apartment, "STA", StringComparison.Ordinal))
            return $"Worker apartment must be STA, got '{hello.Apartment}'.";

        if (hello.Mode != WorkerMode.Target)
            return $"Expected target worker, got '{hello.Mode}'.";

        if (!string.Equals(
                hello.Host,
                target.Identity.Host,
                StringComparison.Ordinal) ||
            !string.Equals(
                hello.TargetId,
                target.Identity.TargetId,
                StringComparison.Ordinal))
            return "Worker attached to an unexpected target.";

        return null;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);

        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static OperationResult InvalidRequest(
        OperationRequest request,
        string kind,
        string message,
        ExecutionState execution,
        OperationStatus status = OperationStatus.InvalidRequest) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = false,
            Status = status,
            TargetState = TargetState.Known,
            Error = new ProtocolError
            {
                Kind = kind,
                Message = message,
                Retryable = false,
                Execution = execution,
                SuggestedActions = ["core.target.capabilities"]
            }
        };

    private static Exception CreateBrokerException(BrokerResponse response) =>
        new InvalidOperationException(
            response.Error is null
                ? $"Worker returned broker response '{response.Kind}' without expected payload."
                : $"{response.Error.Kind}: {response.Error.Message}");

    private static async Task TerminateOwnedProcessAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        try
        {
            if (process.HasExited)
                return;

            process.Kill(entireProcessTree: true);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

    private sealed record BrokerRoundTrip(
        BrokerResponse Response,
        double QueueMs);
}

public sealed class WorkerDispatchException : Exception
{
    public WorkerDispatchException(
        string kind,
        string message,
        ExecutionState execution,
        int? hResult = null,
        double? queueMs = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        Execution = execution;
        HResultCode = hResult;
        QueueMs = queueMs;
    }

    public string Kind { get; }

    public ExecutionState Execution { get; }

    public int? HResultCode { get; }

    public double? QueueMs { get; }
}
