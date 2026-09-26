using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using ComTool.Broker.Protocol;
using ComTool.Hosts.Abstractions;
using ComTool.Hosts.Illustrator;
using ComTool.Protocol;
using ComTool.Runtime;
using ComTool.Transport.Pipe;

internal static class Program
{
    private const string TokenEnvironmentVariable = "COMTOOL_V2_WORKER_TOKEN";

    [STAThread]
    private static int Main(string[] args)
    {
        var pipeName = GetRequiredArg(args, "--pipe");
        var host = GetRequiredArg(args, "--host");
        var expectedTargetId = GetOptionalArg(args, "--target");
        var discoveryMode = HasFlag(args, "--discover");
        var token = Environment.GetEnvironmentVariable(TokenEnvironmentVariable);

        // Exactly one worker mode must be selected.
        if (discoveryMode == (expectedTargetId is not null) ||
            string.IsNullOrWhiteSpace(token))
        {
            WriteStartupError(
                "worker_invalid_bootstrap",
                "Worker requires exactly one of --discover or --target and an authenticated bootstrap token.",
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
            var mode = discoveryMode
                ? WorkerMode.Discovery
                : WorkerMode.Target;

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
            framed = new LengthPrefixedFramedStream(pipe, leaveOpen: true);

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

            framed.Write(BrokerJson.Serialize(response));

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
            Error = result.Error
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

    private static BrokerResponse Error(
        BrokerCommand command,
        string kind,
        string message,
        ExecutionState execution,
        int? hresult = null) =>
        new()
        {
            BrokerVersion = BrokerVersion.Current,
            Sequence = command.Sequence,
            RequestId = command.RequestId,
            Kind = BrokerResponseKind.Error,
            Ok = false,
            Error = new ProtocolError
            {
                Kind = kind,
                Message = message,
                Retryable = false,
                Execution = execution,
                HResult = hresult,
                HResultHex = hresult is null
                    ? null
                    : $"0x{unchecked((uint)hresult.Value):X8}",
                SuggestedActions =
                    execution == ExecutionState.Ambiguous
                        ? ["reconcile_target_before_mutation"]
                        : ["inspect_worker_state"]
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
}
