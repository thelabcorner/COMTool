using System.Text.Json;
using ComTool.Protocol;
using ComTool.Runtime;
using ComTool.Runtime.Ipc;
using ComTool.Supervisor;
using ComTool.Transport.Stdio;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var stdio = HasFlag(args, "--stdio");
            var workerPath = ResolveWorkerPath(GetOption(args, "--worker"));
            var pipeName = RuntimeEndpoint.ResolvePipeName(
                GetOption(args, "--pipe"));
            var hosts = GetOptions(args, "--host");
            if (hosts.Count == 0)
                hosts = ["illustrator"];

            var stateDirectory =
                GetOption(args, "--state-dir") ??
                Environment.GetEnvironmentVariable(
                    "COMTOOL_V2_STATE_DIR");

            using var instanceMutex = new Mutex(
                initiallyOwned: true,
                RuntimeEndpoint.MutexNameForPipe(pipeName),
                out var createdNew);

            if (!createdNew)
            {
                WriteStartupError(
                    "runtime_already_running",
                    $"A COM Tool V2 runtime already owns pipe '{pipeName}'.",
                    exitCode: 3);
                return 3;
            }

            using var shutdown = new CancellationTokenSource();

            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            };

            var runtime = new RuntimeSupervisor(
                hosts,
                new WorkerBrokerOptions
                {
                    WorkerExecutablePath = workerPath
                },
                stateDirectory);

            await using (runtime.ConfigureAwait(false))
            {
                if (stdio)
                {
                    // Stdio is an alternate front end over the SAME supervisor
                    // instance and the SAME operation catalog. It is never a
                    // parallel execution path; malformed frames are isolated by
                    // NdjsonServer exactly as in the gate-0d contract.
                    var server = new NdjsonServer(
                        new SupervisorDispatcher(runtime));

                    await server
                        .RunAsync(
                            Console.OpenStandardInput(),
                            Console.OpenStandardOutput(),
                            shutdown.Token)
                        .ConfigureAwait(false);
                }
                else
                {
                    var server = new RuntimePipeServer(
                        runtime.ExecuteAsync,
                        pipeName);

                    await server
                        .RunAsync(shutdown.Token)
                        .ConfigureAwait(false);
                }
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (RuntimeStateLayoutException ex)
        {
            WriteStartupError(ex.Kind, ex.Message, exitCode: 1);
            return 1;
        }
        catch (FileNotFoundException ex)
        {
            WriteStartupError(
                "runtime_worker_not_found",
                ex.Message,
                exitCode: 1);
            return 1;
        }
        catch (Exception ex)
        {
            WriteStartupError(
                "runtime_start_failed",
                ex.Message,
                exitCode: 1);
            return 1;
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
                component = "ComTool.RuntimeHost",
                exitCode,
                error = new
                {
                    kind,
                    message
                }
            }));
    }

    /// <summary>
    /// Adapts the long-lived <see cref="RuntimeSupervisor"/> to the transport-neutral
    /// <see cref="IOperationDispatcher"/> contract so any front end (NDJSON stdio,
    /// future MCP, tests) drives the one supervisor and one catalog.
    /// </summary>
    private sealed class SupervisorDispatcher(RuntimeSupervisor supervisor)
        : IOperationDispatcher
    {
        public ValueTask<OperationResult> ExecuteAsync(
            OperationRequest request,
            CancellationToken cancellationToken = default) =>
            new(supervisor.ExecuteAsync(request, cancellationToken));
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(arg => string.Equals(arg, name, StringComparison.Ordinal));

    private static string ResolveWorkerPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return Path.GetFullPath(explicitPath);

        var environmentPath = Environment.GetEnvironmentVariable(
            "COMTOOL_V2_WORKER_PATH");
        if (!string.IsNullOrWhiteSpace(environmentPath))
            return Path.GetFullPath(environmentPath);

        var sibling = Path.Combine(
            AppContext.BaseDirectory,
            "ComTool.Worker.exe");

        if (File.Exists(sibling))
            return sibling;

        throw new FileNotFoundException(
            "Runtime host requires ComTool.Worker.exe via --worker, COMTOOL_V2_WORKER_PATH, or a sibling executable.",
            sibling);
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.Ordinal))
                continue;

            if (i + 1 >= args.Length ||
                string.IsNullOrWhiteSpace(args[i + 1]))
                throw new ArgumentException($"{name} requires a value.");

            return args[i + 1];
        }

        return null;
    }

    private static List<string> GetOptions(string[] args, string name)
    {
        var values = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.Ordinal))
                continue;

            if (i + 1 >= args.Length ||
                string.IsNullOrWhiteSpace(args[i + 1]))
                throw new ArgumentException($"{name} requires a value.");

            values.Add(args[i + 1]);
            i++;
        }

        return values;
    }
}
