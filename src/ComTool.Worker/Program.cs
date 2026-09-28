using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using ComTool.Broker.Protocol;
using ComTool.Hosts.Abstractions;
using ComTool.Hosts.Illustrator;
using ComTool.Protocol;
using ComTool.Runtime;
using ComTool.Runtime.Artifacts;
using ComTool.Transport.Pipe;

internal static class Program
{
    private const string TokenEnvironmentVariable = "COMTOOL_V2_WORKER_TOKEN";
    private static readonly JsonSerializerOptions ArtifactPayloadJson =
        new(JsonSerializerDefaults.Web);

    [STAThread]
    private static int Main(string[] args)
    {
        var pipeName = GetRequiredArg(args, "--pipe");
        var host = GetRequiredArg(args, "--host");
        var expectedTargetId = GetOptionalArg(args, "--target");
        var discoveryMode = HasFlag(args, "--discover");
        var launchMode = HasFlag(args, "--launch");
        var maxFrameBytes = GetOptionalPositiveIntArg(
            args,
            "--max-frame-bytes",
            LengthPrefixedFramedStream.DefaultMaxFrameBytes);
        var token = Environment.GetEnvironmentVariable(TokenEnvironmentVariable);

        // Exactly one worker mode must be selected.
        var selectedModes =
            (expectedTargetId is null ? 0 : 1) +
            (discoveryMode ? 1 : 0) +
            (launchMode ? 1 : 0);
        if (selectedModes != 1 ||
            string.IsNullOrWhiteSpace(token))
        {
            WriteStartupError(
                "worker_invalid_bootstrap",
                "Worker requires exactly one of --discover, --launch, or " +
                "--target and an authenticated bootstrap token.",
                exitCode: 2);
            return 2;
        }

        // Minimize accidental token exposure inside the child after bootstrap.
        Environment.SetEnvironmentVariable(TokenEnvironmentVariable, null);

        IHostSession? session = null;
        NamedPipeClientStream? pipe = null;
        LengthPrefixedFramedStream? framed = null;

        try
        {
            var adapter = CreateAdapter(host);
            var mode = expectedTargetId is not null
                ? WorkerMode.Target
                : launchMode
                    ? WorkerMode.Launch
                    : WorkerMode.Discovery;

            if (mode == WorkerMode.Target)
            {
                var target = DiscoverExpectedTarget(adapter, expectedTargetId!);
                session = adapter
                    .ConnectAsync(target)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }

            pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.None);

            pipe.Connect(10_000);
            framed = new LengthPrefixedFramedStream(
                pipe,
                maxFrameBytes,
                leaveOpen: true);

            using var process = Process.GetCurrentProcess();
            var workerId = $"worker-{Guid.NewGuid():N}";
            var status = new BrokerWorkerStatus
            {
                WorkerId = workerId,
                Mode = mode,
                ProcessId = Environment.ProcessId,
                ProcessStartedAt = new DateTimeOffset(process.StartTime),
                Apartment = Thread.CurrentThread.GetApartmentState().ToString(),
                TargetId = session?.Identity.TargetId
            };

            var hello = new WorkerHello
            {
                BrokerVersion = BrokerVersion.Current,
                Token = token,
                Mode = mode,
                WorkerId = workerId,
                ProcessId = status.ProcessId,
                ProcessStartedAt = status.ProcessStartedAt,
                Apartment = status.Apartment,
                Host = adapter.Host,
                TargetId = session?.Identity.TargetId,
                HostVersion = session?.Identity.HostVersion,
                AdapterVersion = adapter.AdapterVersion
            };

            framed.Write(BrokerJson.Serialize(hello));

            using (var ackFrame = framed.Read())
            {
                var ack = BrokerJson.DeserializeHelloAck(ackFrame.Span);
                if (ack.BrokerVersion != BrokerVersion.Current || !ack.Accepted)
                    return 3;
            }

            return RunCommandLoop(
                framed,
                adapter,
                session,
                status);
        }
        catch (Exception ex)
        {
            WriteStartupError(
                "worker_start_failed",
                ex.Message,
                exitCode: 1);
            return 1;
        }
        finally
        {
            if (session is not null)
            {
                session.DisposeAsync()
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }

            if (framed is not null)
            {
                framed.DisposeAsync()
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }

            pipe?.Dispose();
        }
    }

    private static void WriteStartupError(
        string kind,
        string message,
        int exitCode)
    {
        Console.Error.WriteLine(
            JsonSerializer.Serialize(new
            {
                ok = false,
                component = "ComTool.Worker",
                exitCode,
                error = new
                {
                    kind,
                    message
                }
            }));
    }

    private static int RunCommandLoop(
        LengthPrefixedFramedStream framed,
        IHostAdapter adapter,
        IHostSession? session,
        BrokerWorkerStatus workerStatus)
    {
        long lastSequence = 0;

        while (true)
        {
            BrokerCommand command;
            try
            {
                using var commandFrame = framed.Read();
                command = BrokerJson.DeserializeCommand(commandFrame.Span);
            }
            catch (EndOfStreamException)
            {
                return 0;
            }
            catch (IOException)
            {
                return 0;
            }

            if (command.BrokerVersion != BrokerVersion.Current ||
                command.Sequence != lastSequence + 1 ||
                string.IsNullOrWhiteSpace(command.RequestId))
            {
                return 4;
            }

            lastSequence = command.Sequence;

            BrokerResponse response;
            try
            {
                response = command.Kind switch
                {
                    BrokerCommandKind.Ping =>
                        Pong(command, workerStatus),

                    BrokerCommandKind.Discover =>
                        Discover(command, adapter, workerStatus.Mode),

                    BrokerCommandKind.Launch =>
                        Launch(command, adapter, workerStatus.Mode),

                    BrokerCommandKind.Operation when session is not null =>
                        ExecuteOperation(command, session),

                    BrokerCommandKind.Reconcile when session is not null =>
                        Reconcile(command, session),

                    BrokerCommandKind.Operation or BrokerCommandKind.Reconcile =>
                        Error(
                            command,
                            "worker_mode_mismatch",
                            "This command requires a target worker.",
                            ExecutionState.NotStarted),

                    BrokerCommandKind.Shutdown =>
                        Shutdown(command),

                    _ =>
                        Error(
                            command,
                            "unsupported_broker_command",
                            "Unsupported broker command.",
                            ExecutionState.NotStarted)
                };
            }
            catch (Exception ex)
            {
                var mutationClass =
                    command.MutationClass ?? MutationClass.Unknown;

                var execution =
                    command.Kind == BrokerCommandKind.Launch ||
                    command.Kind == BrokerCommandKind.Operation &&
                    mutationClass != MutationClass.ReadOnly
                        ? ExecutionState.Ambiguous
                        : ExecutionState.Started;

                response = Error(
                    command,
                    "worker_command_failure",
                    FormatUnhandledException(ex),
                    execution,
                    ex.HResult);
            }

            WriteResponse(framed, command, response);

            if (command.Kind == BrokerCommandKind.Shutdown)
                return 0;
        }
    }

    private static BrokerResponse Discover(
        BrokerCommand command,
        IHostAdapter adapter,
        WorkerMode mode)
    {
        if (mode != WorkerMode.Discovery)
        {
            return Error(
                command,
                "worker_mode_mismatch",
                "Discovery requires a discovery worker.",
                ExecutionState.NotStarted);
        }

        var targets = adapter
            .DiscoverAsync()
            .AsTask()
            .GetAwaiter()
            .GetResult();

        return new BrokerResponse
        {
            BrokerVersion = BrokerVersion.Current,
            Sequence = command.Sequence,
            RequestId = command.RequestId,
            Kind = BrokerResponseKind.Discovery,
            Ok = true,
            Targets = targets
        };
    }

    private static BrokerResponse Launch(
        BrokerCommand command,
        IHostAdapter adapter,
        WorkerMode mode)
    {
        if (mode != WorkerMode.Launch)
        {
            return Error(
                command,
                "worker_mode_mismatch",
                "Launch requires a launch worker.",
                ExecutionState.NotStarted);
        }

        if (command.Launch is null)
        {
            return Error(
                command,
                "invalid_broker_command",
                "Launch command requires launch specification.",
                ExecutionState.NotStarted);
        }

        if (adapter is not IHostLaunchAdapter launchAdapter)
        {
            return Error(
                command,
                "host_launch_unsupported",
                $"Adapter '{adapter.Host}' does not implement host launch.",
                ExecutionState.NotStarted);
        }

        try
        {
            var observation = launchAdapter
                .LaunchAsync(command.Launch)
                .AsTask()
                .GetAwaiter()
                .GetResult();

            return new BrokerResponse
            {
                BrokerVersion = BrokerVersion.Current,
                Sequence = command.Sequence,
                RequestId = command.RequestId,
                Kind = BrokerResponseKind.Launch,
                Ok = true,
                Launch = observation
            };
        }
        catch (HostLaunchException ex)
        {
            return Error(
                command,
                ex.Kind,
                ex.Message,
                ex.Execution,
                ex.HResultCode,
                retryable: ex.Retryable,
                suggestedActions:
                    ex.Execution == ExecutionState.Ambiguous
                        ? ["inspect_running_targets_before_retry"]
                        : null);
        }
    }

    private static BrokerResponse ExecuteOperation(
        BrokerCommand command,
        IHostSession session)
    {
        if (command.Operation is null || command.MutationClass is null)
        {
            return Error(
                command,
                "invalid_broker_command",
                "Operation command requires operation and mutationClass.",
                ExecutionState.NotStarted);
        }

        if (command.Operation.Preconditions is { Count: > 0 } ||
            command.Operation.Postconditions is { Count: > 0 })
        {
            return Error(
                command,
                "conditions_require_runtime_supervisor",
                "A host worker cannot execute a condition-bearing request directly.",
                ExecutionState.NotStarted);
        }

        if (!BuiltInOperations.Catalog.TryGet(
                command.Operation.Operation,
                out var definition))
        {
            return Error(
                command,
                "unsupported_operation",
                $"Operation '{command.Operation.Operation}' is not registered by this runtime.",
                ExecutionState.NotStarted);
        }

        if (definition.Scope != OperationExecutionScope.Host)
        {
            return Error(
                command,
                "operation_scope_mismatch",
                $"Operation '{command.Operation.Operation}' is runtime-scoped and cannot execute inside a host worker.",
                ExecutionState.NotStarted);
        }

        MutationClass effectiveMutationClass;
        try
        {
            effectiveMutationClass = OperationMutationResolver.Resolve(
                definition,
                command.Operation);
        }
        catch (OperationMutationPolicyException ex)
        {
            return Error(
                command,
                ex.Kind,
                ex.Message,
                ExecutionState.NotStarted);
        }

        if (effectiveMutationClass != command.MutationClass)
        {
            return Error(
                command,
                "mutation_class_mismatch",
                $"Operation '{command.Operation.Operation}' resolves to '{effectiveMutationClass}', not '{command.MutationClass}'.",
                ExecutionState.NotStarted);
        }

        var result = session
            .ExecuteAsync(command.Operation)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        return new BrokerResponse
        {
            BrokerVersion = BrokerVersion.Current,
            Sequence = command.Sequence,
            RequestId = command.RequestId,
            Kind = BrokerResponseKind.Operation,
            Ok = result.Ok,
            OperationResult = result,
            Error = result.Error,
            OperationExecutionDisposition =
                result.Error?.Execution ==
                ExecutionState.NotStarted
                    ? BrokerOperationExecutionDisposition
                        .CertifiedNotStarted
                    : BrokerOperationExecutionDisposition
                        .MayHaveStarted
        };
    }

    private static BrokerResponse Reconcile(
        BrokerCommand command,
        IHostSession session)
    {
        var result = session
            .ReconcileAsync()
            .AsTask()
            .GetAwaiter()
            .GetResult();

        return new BrokerResponse
        {
            BrokerVersion = BrokerVersion.Current,
            Sequence = command.Sequence,
            RequestId = command.RequestId,
            Kind = BrokerResponseKind.Reconcile,
            Ok = result.Reconciled,
            Reconciliation = new BrokerReconciliation
            {
                Reconciled = result.Reconciled,
                TargetState = result.TargetState,
                Evidence = result.Evidence,
                Error = result.Error
            },
            Error = result.Error
        };
    }

    private static BrokerResponse Pong(
        BrokerCommand command,
        BrokerWorkerStatus workerStatus) =>
        new()
        {
            BrokerVersion = BrokerVersion.Current,
            Sequence = command.Sequence,
            RequestId = command.RequestId,
            Kind = BrokerResponseKind.Pong,
            Ok = true,
            Worker = workerStatus
        };

    private static BrokerResponse Shutdown(BrokerCommand command) =>
        new()
        {
            BrokerVersion = BrokerVersion.Current,
            Sequence = command.Sequence,
            RequestId = command.RequestId,
            Kind = BrokerResponseKind.Shutdown,
            Ok = true
        };

    private static void WriteResponse(
        LengthPrefixedFramedStream framed,
        BrokerCommand command,
        BrokerResponse original)
    {
        var response = original;
        byte[]? artifactPayload = null;

        if (TryPrepareArtifactTransfer(
                command,
                original,
                framed.MaxFrameBytes,
                out var prepared,
                out artifactPayload))
        {
            response = prepared;
        }
        else if (!ReferenceEquals(prepared, original))
        {
            response = prepared;
        }

        if (artifactPayload is not null)
        {
            var transfer = response.ArtifactTransfer
                ?? throw new InvalidOperationException(
                    "Prepared artifact transfer is missing metadata.");

            for (var chunkIndex = 0;
                 chunkIndex < transfer.ChunkCount;
                 chunkIndex++)
            {
                var offset =
                    chunkIndex *
                    BrokerArtifactTransferLimits.ChunkByteCount;
                var length = Math.Min(
                    BrokerArtifactTransferLimits.ChunkByteCount,
                    artifactPayload.Length - offset);
                var data = new byte[length];
                Buffer.BlockCopy(
                    artifactPayload,
                    offset,
                    data,
                    0,
                    length);

                framed.Write(
                    BrokerJson.Serialize(
                        new BrokerResponse
                        {
                            BrokerVersion = BrokerVersion.Current,
                            Sequence = command.Sequence,
                            RequestId = command.RequestId,
                            Kind = BrokerResponseKind.ArtifactChunk,
                            Ok = true,
                            ArtifactChunk = new BrokerArtifactChunk
                            {
                                TransferId = transfer.TransferId,
                                ChunkIndex = chunkIndex,
                                Data = data
                            }
                        }));
            }
        }

        var responseBytes = BrokerJson.Serialize(response);
        if (responseBytes.Length > framed.MaxFrameBytes)
        {
            response = ResponseTooLarge(
                command,
                original,
                responseBytes.Length,
                framed.MaxFrameBytes);
            responseBytes = BrokerJson.Serialize(response);
        }

        framed.Write(responseBytes);
    }

    private static bool TryPrepareArtifactTransfer(
        BrokerCommand command,
        BrokerResponse original,
        int maxFrameBytes,
        out BrokerResponse response,
        out byte[]? payload)
    {
        response = original;
        payload = null;

        if (command.Kind != BrokerCommandKind.Operation ||
            original.Kind != BrokerResponseKind.Operation ||
            original.OperationResult is not { Ok: true } result ||
            result.Result?.Value is not { } value)
        {
            return false;
        }

        var serialized = JsonSerializer.SerializeToUtf8Bytes(
            value,
            ArtifactPayloadJson);
        if (serialized.LongLength <
            ArtifactResultOffload.DefaultThresholdByteCount)
        {
            return false;
        }

        if (serialized.LongLength >
            ArtifactStoreOptions.DefaultMaxArtifactByteCount)
        {
            response = ArtifactResultTooLarge(
                command,
                original,
                serialized.LongLength);
            return false;
        }

        var transferId = Guid.NewGuid().ToString("N");
        var chunkCount = checked(
            (serialized.Length +
             BrokerArtifactTransferLimits.ChunkByteCount - 1) /
            BrokerArtifactTransferLimits.ChunkByteCount);
        var transfer = new BrokerArtifactTransfer
        {
            TransferId = transferId,
            ChunkCount = chunkCount,
            PayloadByteCount = serialized.LongLength,
            PayloadSha256 = Convert
                .ToHexString(SHA256.HashData(serialized))
                .ToLowerInvariant(),
            OriginalResultKind = result.Result.Kind
        };
        var compact = original with
        {
            OperationResult = result with { Result = null },
            ArtifactTransfer = transfer
        };

        // Do not emit any transfer frame until both the final response and the
        // largest possible chunk are proven to fit the configured broker bound.
        var compactBytes = BrokerJson.Serialize(compact);
        var largestChunkLength = Math.Min(
            BrokerArtifactTransferLimits.ChunkByteCount,
            serialized.Length);
        var largestChunk = new byte[largestChunkLength];
        var chunkProbeBytes = BrokerJson.Serialize(
            new BrokerResponse
            {
                BrokerVersion = BrokerVersion.Current,
                Sequence = command.Sequence,
                RequestId = command.RequestId,
                Kind = BrokerResponseKind.ArtifactChunk,
                Ok = true,
                ArtifactChunk = new BrokerArtifactChunk
                {
                    TransferId = transferId,
                    ChunkIndex = Math.Max(0, chunkCount - 1),
                    Data = largestChunk
                }
            });

        if (compactBytes.Length > maxFrameBytes ||
            chunkProbeBytes.Length > maxFrameBytes)
        {
            response = ResponseTooLarge(
                command,
                original,
                Math.Max(
                    compactBytes.Length,
                    chunkProbeBytes.Length),
                maxFrameBytes);
            return false;
        }

        response = compact;
        payload = serialized;
        return true;
    }

    private static BrokerResponse ArtifactResultTooLarge(
        BrokerCommand command,
        BrokerResponse original,
        long actualBytes)
    {
        var mutationClass =
            command.MutationClass ?? MutationClass.Unknown;
        var error = new ProtocolError
        {
            Kind = "artifact_result_too_large",
            Message =
                $"The completed host result payload is {actualBytes} bytes, " +
                $"exceeding the runtime artifact ceiling of " +
                $"{ArtifactStoreOptions.DefaultMaxArtifactByteCount} bytes.",
            Retryable = mutationClass == MutationClass.ReadOnly,
            Execution = ExecutionState.Completed,
            SuggestedActions =
            [
                "request_bounded_result",
                "use_operation_specific_pagination"
            ]
        };
        var result = new OperationResult
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = command.Operation!.Id,
            Operation = command.Operation.Operation,
            Ok = false,
            Status = OperationStatus.Failed,
            TargetState =
                mutationClass == MutationClass.ReadOnly
                    ? original.OperationResult?.TargetState ??
                      TargetState.Known
                    : TargetState.KnownChanged,
            Error = error,
            Timing = original.OperationResult?.Timing
        };

        return new BrokerResponse
        {
            BrokerVersion = BrokerVersion.Current,
            Sequence = command.Sequence,
            RequestId = command.RequestId,
            Kind = BrokerResponseKind.Operation,
            Ok = false,
            OperationResult = result,
            Error = error,
            OperationExecutionDisposition =
                BrokerOperationExecutionDisposition.MayHaveStarted
        };
    }

    private static BrokerResponse ResponseTooLarge(
        BrokerCommand command,
        BrokerResponse original,
        int actualBytes,
        int maxBytes)
    {
        if (command.Kind == BrokerCommandKind.Operation &&
            command.Operation is not null)
        {
            var execution =
                original.OperationResult?.Ok == true
                    ? ExecutionState.Completed
                    : original.OperationResult?.Error?.Execution ??
                      original.Error?.Execution ??
                      ExecutionState.Ambiguous;
            var targetState =
                original.OperationResult?.Ok == true &&
                command.MutationClass is not null and not MutationClass.ReadOnly
                    ? TargetState.KnownChanged
                    : original.OperationResult?.TargetState ??
                      TargetState.Known;
            var error = new ProtocolError
            {
                Kind = "worker_result_too_large",
                Message =
                    $"The serialized worker result is {actualBytes} bytes, " +
                    $"exceeding the private worker-frame limit of {maxBytes} bytes.",
                Retryable = command.MutationClass == MutationClass.ReadOnly,
                Execution = execution,
                SuggestedActions =
                [
                    "use_artifact_or_bounded_operation_result"
                ]
            };
            var result = new OperationResult
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = command.Operation.Id,
                Operation = command.Operation.Operation,
                Ok = false,
                Status = OperationStatus.Failed,
                TargetState = targetState,
                Error = error,
                Timing = original.OperationResult?.Timing
            };

            return new BrokerResponse
            {
                BrokerVersion = BrokerVersion.Current,
                Sequence = command.Sequence,
                RequestId = command.RequestId,
                Kind = BrokerResponseKind.Operation,
                Ok = false,
                OperationResult = result,
                Error = error,
                OperationExecutionDisposition =
                    execution == ExecutionState.NotStarted
                        ? BrokerOperationExecutionDisposition.CertifiedNotStarted
                        : BrokerOperationExecutionDisposition.MayHaveStarted
            };
        }

        return Error(
            command,
            "worker_response_too_large",
            $"The serialized worker response is {actualBytes} bytes, exceeding " +
            $"the private worker-frame limit of {maxBytes} bytes.",
            command.Kind == BrokerCommandKind.Launch && original.Ok
                ? ExecutionState.Completed
                : ExecutionState.Started,
            suggestedActions: ["inspect_worker_response_bounds"]);
    }

    private static BrokerResponse Error(
        BrokerCommand command,
        string kind,
        string message,
        ExecutionState execution,
        int? hresult = null,
        bool retryable = false,
        IReadOnlyList<string>? suggestedActions = null) =>
        new()
        {
            BrokerVersion = BrokerVersion.Current,
            Sequence = command.Sequence,
            RequestId = command.RequestId,
            Kind = BrokerResponseKind.Error,
            Ok = false,
            OperationExecutionDisposition =
                command.Kind ==
                BrokerCommandKind.Operation
                    ? execution ==
                      ExecutionState.NotStarted
                        ? BrokerOperationExecutionDisposition
                            .CertifiedNotStarted
                        : BrokerOperationExecutionDisposition
                            .MayHaveStarted
                    : null,
            Error = new ProtocolError
            {
                Kind = kind,
                Message = message,
                Retryable = retryable,
                Execution = execution,
                HResult = hresult,
                HResultHex = hresult is null
                    ? null
                    : $"0x{unchecked((uint)hresult.Value):X8}",
                SuggestedActions = suggestedActions ??
                    (execution == ExecutionState.Ambiguous
                        ? ["reconcile_target_before_mutation"]
                        : ["inspect_worker_state"])
            }
        };

    private static string FormatUnhandledException(Exception ex)
    {
        const int maxDiagnosticChars = 16_384;

        var diagnostic = ex.ToString();
        return diagnostic.Length <= maxDiagnosticChars
            ? diagnostic
            : diagnostic[..maxDiagnosticChars] +
              Environment.NewLine +
              "... worker exception diagnostic truncated ...";
    }

    private static IHostAdapter CreateAdapter(string host) =>
        host switch
        {
            IllustratorAdapter.HostName => new IllustratorAdapter(),
            _ => throw new NotSupportedException(
                $"Worker does not contain adapter '{host}'.")
        };

    private static HostTargetDescriptor DiscoverExpectedTarget(
        IHostAdapter adapter,
        string expectedTargetId)
    {
        var targets = adapter
            .DiscoverAsync()
            .AsTask()
            .GetAwaiter()
            .GetResult();

        return targets.SingleOrDefault(
                   target => string.Equals(
                       target.Identity.TargetId,
                       expectedTargetId,
                       StringComparison.Ordinal))
               ?? throw new InvalidOperationException(
                   $"Expected target '{expectedTargetId}' was not discovered.");
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(arg => string.Equals(arg, name, StringComparison.Ordinal));

    private static string? GetOptionalArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
                return args[i + 1];
        }

        return null;
    }

    private static string GetRequiredArg(string[] args, string name) =>
        GetOptionalArg(args, name)
        ?? throw new ArgumentException($"Missing required argument {name}.");

    private static int GetOptionalPositiveIntArg(
        string[] args,
        string name,
        int defaultValue)
    {
        var raw = GetOptionalArg(args, name);
        if (raw is null)
            return defaultValue;

        if (!int.TryParse(
                raw,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value) ||
            value < 256)
        {
            throw new ArgumentException(
                $"{name} must be an integer >= 256.");
        }

        return value;
    }
}
