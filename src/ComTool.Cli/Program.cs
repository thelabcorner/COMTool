using System.Reflection;
using System.Text.Json;
using ComTool.Protocol;
using ComTool.Runtime;
using ComTool.Runtime.Ipc;

internal static class Program
{
    private static readonly Assembly ExecutingAssembly =
        typeof(Program).Assembly;
    private static readonly string ProductVersion =
        ExecutingAssembly.GetName().Version?.ToString() ?? "unknown";
    private static readonly string InformationalVersion =
        ExecutingAssembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? ProductVersion;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
                return WriteHelp();
            if (args[0] is "--version" or "-v" or "version")
                return WriteVersion();

            if (HasFlag(args, "--broker") ||
                ParseOption(args, "--worker") is not null)
            {
                throw new ArgumentException(
                    "Direct/broker CLI control planes were removed. Every CLI " +
                    "operation is routed through the persistent RuntimeHost; " +
                    "use --pipe to select that runtime.");
            }

            return args[0] switch
            {
                "health" => RunRuntimeOperation(
                    "core.runtime.health",
                    requestedTargetId: null,
                    requiresTarget: false,
                    pipeName: ParseOption(args, "--pipe")),
                "incidents" => RunRuntimeOperation(
                    "core.incidents.list",
                    requestedTargetId: null,
                    requiresTarget: false,
                    pipeName: ParseOption(args, "--pipe")),
                "artifact-describe" => RunArtifactDescribe(args),
                "artifact-read" => RunArtifactRead(args),
                "stdio" => RunStdioProxy(args),
                "targets" => RunRuntimeOperation(
                    "core.targets.list",
                    requestedTargetId: null,
                    requiresTarget: false,
                    pipeName: ParseOption(args, "--pipe")),
                "capabilities" => RunRuntimeOperation(
                    "core.target.capabilities",
                    ParseTarget(args),
                    ParseOption(args, "--host"),
                    requiresTarget: true,
                    leaseId: ParseOption(args, "--lease"),
                    pipeName: ParseOption(args, "--pipe")),
                "status" => RunRuntimeOperation(
                    "core.target.status",
                    ParseTarget(args),
                    ParseOption(args, "--host"),
                    requiresTarget: true,
                    leaseId: ParseOption(args, "--lease"),
                    pipeName: ParseOption(args, "--pipe")),
                "snapshot" => RunRuntimeOperation(
                    "core.target.snapshot",
                    ParseTarget(args),
                    ParseOption(args, "--host"),
                    requiresTarget: true,
                    leaseId: ParseOption(args, "--lease"),
                    pipeName: ParseOption(args, "--pipe")),
                "reconcile" => RunRuntimeOnlyTargetOperation(
                    args,
                    "core.target.reconcile",
                    requireLease: true),
                "incident-resolve" => RunIncidentResolve(args),
                "incident-resolve-offline" =>
                    RunOfflineIncidentResolve(args),
                "mutation-reconcile" => RunMutationReconcile(args),
                "lease-acquire" => RunLeaseControl(
                    args,
                    "core.target.lease.acquire",
                    requireLease: false,
                    allowTtl: true),
                "lease-renew" => RunLeaseControl(
                    args,
                    "core.target.lease.renew",
                    requireLease: true,
                    allowTtl: true),
                "lease-release" => RunLeaseControl(
                    args,
                    "core.target.lease.release",
                    requireLease: true,
                    allowTtl: false),
                "get" => RunComGet(args),
                "call-read" => RunComCallRead(args),
                "artboards" => RunStructureRead(
                    args,
                    "illustrator.artboard.read"),
                "layers" => RunStructureRead(
                    args,
                    "illustrator.layer.read"),
                "eval" => RunScriptEval(args),
                "run-file" => RunScriptRunFile(args),
                _ => WriteError(
                    "unknown_command",
                    $"Unknown command '{args[0]}'.",
                    ["help"])
            };
        }
        catch (RuntimeUnavailableException ex)
        {
            return WriteError(
                "runtime_unreachable",
                $"Could not connect to COM Tool V2 runtime pipe '{ex.PipeName}': {ex.InnerException?.Message ?? ex.Message}",
                ["start_runtime", "inspect_runtime"],
                retryable: true,
                execution: ExecutionState.NotStarted);
        }
        catch (RuntimeRequestInterruptedException ex)
        {
            return WriteError(
                "runtime_request_interrupted",
                ex.Message,
                ["inspect_runtime", "inspect_mutation_ledger"],
                retryable: false,
                execution: ex.Execution);
        }
        catch (Exception ex)
        {
            return WriteError(
                "cli_failure",
                ex.Message,
                ["inspect_runtime"]);
        }
    }

    private static int RunComGet(string[] args)
    {
        var path = RequireOption(args, "--path");
        var input = JsonSerializer.SerializeToElement(
            new { path },
            JsonOptions);

        return RunRuntimeOperation(
            "com.get",
            ParseTarget(args),
            ParseOption(args, "--host"),
            requiresTarget: true,
            input,
            leaseId: ParseOption(args, "--lease"),
            pipeName: ParseOption(args, "--pipe"));
    }

    private static int RunComCallRead(string[] args)
    {
        var path = RequireOption(args, "--path");
        var argsValue = ParseJsonArrayOption(args, "--args-json");

        var input = JsonSerializer.SerializeToElement(
            new
            {
                path,
                args = argsValue
            },
            JsonOptions);

        return RunRuntimeOperation(
            "com.call.read",
            ParseTarget(args),
            ParseOption(args, "--host"),
            requiresTarget: true,
            input,
            leaseId: ParseOption(args, "--lease"),
            pipeName: ParseOption(args, "--pipe"));
    }

    private static int RunStructureRead(
        string[] args,
        string operation)
    {
        var input = BuildDocumentSelectorInput(args);

        return RunRuntimeOperation(
            operation,
            ParseTarget(args),
            ParseOption(args, "--host"),
            requiresTarget: true,
            input,
            leaseId: ParseOption(args, "--lease"),
            pipeName: ParseOption(args, "--pipe"));
    }

    private static JsonElement BuildDocumentSelectorInput(string[] args)
    {
        var name = ParseOption(args, "--name");
        var index = ParseIntOption(args, "--index");
        var active = HasFlag(args, "--active");

        var supplied =
            (name is not null ? 1 : 0) +
            (index is not null ? 1 : 0) +
            (active ? 1 : 0);

        if (supplied != 1)
        {
            throw new ArgumentException(
                "Exactly one document selector is required: --name <name>, --index <n>, or --active.");
        }

        return name is not null
            ? JsonSerializer.SerializeToElement(new { name }, JsonOptions)
            : index is not null
                ? JsonSerializer.SerializeToElement(new { index = index.Value }, JsonOptions)
                : JsonSerializer.SerializeToElement(new { active = true }, JsonOptions);
    }

    private static int RunScriptEval(string[] args)
    {
        var expression = ParseOption(args, "--expr");
        var code = ParseOption(args, "--code");

        if ((expression is null) == (code is null))
        {
            throw new ArgumentException(
                "eval requires exactly one of --expr <source> or --code <source>.");
        }

        var effects = ParseOption(args, "--effects");
        var scriptArgs = ParseJsonArrayOption(args, "--args-json");
        var leaseId = RequireOption(args, "--lease");
        var requestId = RequireOption(args, "--request-id");
        var workerWatchdogMs = ParseWorkerWatchdogOption(args);
        var retryBudgetMs = ParseRetryBudgetOption(args);
        var preconditions = ParseConditionsOption(
            args,
            "--preconditions-json",
            "--preconditions-file");
        var postconditions = ParseConditionsOption(
            args,
            "--postconditions-json",
            "--postconditions-file");

        var input = JsonSerializer.SerializeToElement(
            new
            {
                kind = expression is not null
                    ? "expression"
                    : "code",
                source = expression ?? code!,
                effects,
                args = scriptArgs
            },
            JsonOptions);

        return RunRuntimeOperation(
            "script.eval",
            ParseTarget(args),
            ParseOption(args, "--host"),
            requiresTarget: true,
            input: input,
            leaseId: leaseId,
            requestId: requestId,
            preconditions: preconditions,
            postconditions: postconditions,
            workerWatchdogMs: workerWatchdogMs,
            retryBudgetMs: retryBudgetMs,
            pipeName: ParseOption(args, "--pipe"));
    }

    private static int RunScriptRunFile(string[] args)
    {
        var path = RequireOption(
            args,
            "--path");
        var expectedSha256 = RequireOption(
            args,
            "--sha256");
        var effects = ParseOption(
            args,
            "--effects");
        var scriptArgs = ParseJsonArrayOption(
            args,
            "--args-json");
        var leaseId = RequireOption(
            args,
            "--lease");
        var requestId = RequireOption(
            args,
            "--request-id");
        var workerWatchdogMs = ParseWorkerWatchdogOption(args);
        var retryBudgetMs = ParseRetryBudgetOption(args);
        var preconditions = ParseConditionsOption(
            args,
            "--preconditions-json",
            "--preconditions-file");
        var postconditions = ParseConditionsOption(
            args,
            "--postconditions-json",
            "--postconditions-file");

        var input = JsonSerializer.SerializeToElement(
            new
            {
                path,
                expectedSha256,
                effects,
                args = scriptArgs
            },
            JsonOptions);

        return RunRuntimeOperation(
            "script.runFile",
            ParseTarget(args),
            ParseOption(args, "--host"),
            requiresTarget: true,
            input: input,
            leaseId: leaseId,
            requestId: requestId,
            preconditions: preconditions,
            postconditions: postconditions,
            workerWatchdogMs: workerWatchdogMs,
            retryBudgetMs: retryBudgetMs,
            pipeName: ParseOption(args, "--pipe"));
    }

    private static int RunIncidentResolve(string[] args)
    {
        var leaseId = RequireOption(
            args,
            "--lease");
        var incidentRequestId = RequireOption(
            args,
            "--incident-request-id");
        var expectedRevision = RequireLongOption(
            args,
            "--expected-revision");
        var rationale = RequireOption(
            args,
            "--rationale");

        var changed = HasFlag(
            args,
            "--changed");
        var unchanged = HasFlag(
            args,
            "--unchanged");

        if (changed == unchanged)
        {
            throw new ArgumentException(
                "incident-resolve requires exactly one of --changed or --unchanged.");
        }

        JsonElement? evidence = null;
        var evidenceRaw = ParseOption(
            args,
            "--evidence-json");

        if (evidenceRaw is not null)
        {
            using var evidenceDocument =
                JsonDocument.Parse(evidenceRaw);
            evidence =
                evidenceDocument.RootElement.Clone();
        }

        var input = JsonSerializer.SerializeToElement(
            new
            {
                incidentRequestId,
                expectedRevision,
                resolution = changed
                    ? "known_changed"
                    : "known_unchanged",
                rationale,
                evidence
            },
            JsonOptions);

        return RunRuntimeOperation(
            "core.target.incident.resolve",
            ParseTarget(args),
            ParseOption(args, "--host"),
            requiresTarget: true,
            input: input,
            leaseId: leaseId,
            pipeName: ParseOption(args, "--pipe"));
    }

    private static int RunOfflineIncidentResolve(string[] args)
    {
        var targetId = RequireOption(args, "--target-id");
        var incidentRequestId = RequireOption(
            args,
            "--incident-request-id");
        var expectedUpdatedAt = RequireOption(
            args,
            "--expected-updated-at");
        var rationale = RequireOption(args, "--rationale");

        var changed = HasFlag(args, "--changed");
        var unchanged = HasFlag(args, "--unchanged");
        if (changed == unchanged)
        {
            throw new ArgumentException(
                "incident-resolve-offline requires exactly one of --changed or --unchanged.");
        }

        JsonElement? evidence = null;
        var evidenceRaw = ParseOption(args, "--evidence-json");
        if (evidenceRaw is not null)
        {
            using var evidenceDocument =
                JsonDocument.Parse(evidenceRaw);
            evidence = evidenceDocument.RootElement.Clone();
        }

        var input = JsonSerializer.SerializeToElement(
            new
            {
                targetId,
                incidentRequestId,
                expectedUpdatedAt,
                resolution = changed
                    ? "known_changed"
                    : "known_unchanged",
                rationale,
                evidence
            },
            JsonOptions);

        return RunRuntimeOperation(
            "core.incident.resolve",
            requestedTargetId: null,
            requiresTarget: false,
            input: input,
            pipeName: ParseOption(args, "--pipe"));
    }

    private static int RunMutationReconcile(string[] args)
    {
        var leaseId = RequireOption(
            args,
            "--lease");
        var incidentRequestId = RequireOption(
            args,
            "--incident-request-id");
        var expectedRevision = RequireLongOption(
            args,
            "--expected-revision");
        var postconditions = ParseConditionsOption(
            args,
            "--postconditions-json",
            "--postconditions-file");

        var input = JsonSerializer.SerializeToElement(
            new
            {
                incidentRequestId,
                expectedRevision
            },
            JsonOptions);

        return RunRuntimeOperation(
            "core.target.mutation.reconcile",
            ParseTarget(args),
            ParseOption(args, "--host"),
            requiresTarget: true,
            input: input,
            leaseId: leaseId,
            postconditions: postconditions,
            pipeName: ParseOption(args, "--pipe"));
    }

    private static int RunRuntimeOnlyTargetOperation(
        string[] args,
        string operation,
        bool requireLease)
    {
        var leaseId = requireLease
            ? RequireOption(args, "--lease")
            : ParseOption(args, "--lease");

        return RunRuntimeOperation(
            operation,
            ParseTarget(args),
            ParseOption(args, "--host"),
            requiresTarget: true,
            leaseId: leaseId,
            pipeName: ParseOption(args, "--pipe"));
    }

    private static int RunLeaseControl(
        string[] args,
        string operation,
        bool requireLease,
        bool allowTtl)
    {
        var leaseId = requireLease
            ? RequireOption(args, "--lease")
            : ParseOption(args, "--lease");

        var ttlMs = allowTtl
            ? ParseIntOption(args, "--ttl-ms")
            : null;

        JsonElement input;
        if (ttlMs is null)
        {
            input = JsonSerializer.SerializeToElement(
                new { },
                JsonOptions);
        }
        else
        {
            input = JsonSerializer.SerializeToElement(
                new { ttlMs },
                JsonOptions);
        }

        return RunRuntimeOperation(
            operation,
            ParseTarget(args),
            ParseOption(args, "--host"),
            requiresTarget: true,
            input: input,
            leaseId: leaseId,
            pipeName: ParseOption(args, "--pipe"));
    }

    private static int RunRuntimeOperation(
        string operation,
        string? requestedTargetId,
        string? requestedHost = null,
        bool requiresTarget = false,
        JsonElement? input = null,
        string? leaseId = null,
        string? requestId = null,
        IReadOnlyList<OperationCondition>? preconditions = null,
        IReadOnlyList<OperationCondition>? postconditions = null,
        int? workerWatchdogMs = null,
        int? retryBudgetMs = null,
        string? pipeName = null)
    {
        var client = ConnectRuntime(pipeName);

        try
        {
            using var nullDocument = JsonDocument.Parse("null");
            var nullInput = nullDocument.RootElement.Clone();

            var target = requiresTarget
                ? ResolveRuntimeTarget(
                    client,
                    requestedTargetId,
                    requestedHost,
                    nullInput)
                : null;

            var request = new OperationRequest
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = requestId ?? $"cli-{Guid.NewGuid():N}",
                Target = target,
                Operation = operation,
                Input = input ?? nullInput,
                Policy =
                    leaseId is null &&
                    workerWatchdogMs is null &&
                    retryBudgetMs is null
                        ? null
                        : new OperationPolicy(
                            WorkerWatchdogMs: workerWatchdogMs,
                            LeaseId: leaseId,
                            RetryBudgetMs: retryBudgetMs),
                Preconditions = preconditions,
                Postconditions = postconditions
            };

            var result = client
                .ExecuteAsync(request)
                .GetAwaiter()
                .GetResult();

            Console.Out.WriteLine(ProtocolJson.Serialize(result));
            return result.Ok ? 0 : 1;
        }
        finally
        {
            client
                .DisposeAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
    }

    private static int RunArtifactDescribe(string[] args)
    {
        if (HasFlag(args, "--broker"))
        {
            throw new ArgumentException(
                "'artifact-describe' is runtime-scoped and does not support --broker.");
        }

        var artifactId = RequireOption(args, "--artifact-id");
        var input = JsonSerializer.SerializeToElement(
            new { artifactId },
            JsonOptions);

        return RunRuntimeOperation(
            "core.artifact.describe",
            requestedTargetId: null,
            requiresTarget: false,
            input: input,
            pipeName: ParseOption(args, "--pipe"));
    }

    private static int RunArtifactRead(string[] args)
    {
        if (HasFlag(args, "--broker"))
        {
            throw new ArgumentException(
                "'artifact-read' is runtime-scoped and does not support --broker.");
        }

        var artifactId = RequireOption(args, "--artifact-id");
        var offset = ParseLongOption(args, "--offset");
        var length = ParseLongOption(args, "--length");
        var fields = new Dictionary<string, object?>
        {
            ["artifactId"] = artifactId
        };

        if (offset is not null)
            fields["offset"] = offset.Value;
        if (length is not null)
            fields["length"] = length.Value;

        return RunRuntimeOperation(
            "core.artifact.read",
            requestedTargetId: null,
            requiresTarget: false,
            input: JsonSerializer.SerializeToElement(fields, JsonOptions),
            pipeName: ParseOption(args, "--pipe"));
    }

    /// <summary>
    /// NDJSON front end that normalizes every incoming line into an
    /// <see cref="OperationRequest"/> and forwards it over the SAME runtime pipe
    /// used by the direct CLI commands. This proves one protocol across the CLI,
    /// stdio, and the runtime rather than introducing a parallel path.
    /// </summary>
    private static int RunStdioProxy(string[] args)
    {
        if (HasFlag(args, "--broker"))
            throw new ArgumentException(
                "'stdio' forwards to the persistent runtime and does not support --broker.");

        var pipeName = RuntimeEndpoint.ResolvePipeName(
            ParseOption(args, "--pipe"));
        var pool = RuntimePipeClientPool
            .ConnectAsync(pipeName)
            .GetAwaiter()
            .GetResult();
        var dispatcher =
            new PipeDispatcher(pool);

        try
        {
            var server =
                new global::ComTool.Transport.Stdio.NdjsonServer(
                    dispatcher);

            server
                .RunAsync(
                    Console.OpenStandardInput(),
                    Console.OpenStandardOutput())
                .GetAwaiter()
                .GetResult();

            return 0;
        }
        finally
        {
            dispatcher
                .DisposeAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
    }

    /// <summary>
    /// Adapts the reusable runtime-pipe client pool to the generic dispatcher
    /// contract consumed by the NDJSON server.
    /// </summary>
    private sealed class PipeDispatcher(
        RuntimePipeClientPool pool)
        : IOperationDispatcher,
          IAsyncDisposable
    {
        public ValueTask<OperationResult> ExecuteAsync(
            OperationRequest request,
            CancellationToken cancellationToken = default) =>
            new(pool.ExecuteAsync(
                request,
                cancellationToken:
                    cancellationToken));

        public ValueTask DisposeAsync() =>
            pool.DisposeAsync();
    }

    private static RuntimePipeClient ConnectRuntime(string? pipeName)
    {
        var resolvedPipe = RuntimeEndpoint.ResolvePipeName(pipeName);

        try
        {
            return RuntimePipeClient
                .ConnectAsync(resolvedPipe)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex) when (
            ex is TimeoutException or
                IOException or
                OperationCanceledException)
        {
            throw new RuntimeUnavailableException(resolvedPipe, ex);
        }
    }

    private static TargetRef ResolveRuntimeTarget(
        RuntimePipeClient client,
        string? requestedTargetId,
        string? requestedHost,
        JsonElement nullInput)
    {
        if (!string.IsNullOrWhiteSpace(requestedTargetId))
        {
            if (!string.IsNullOrWhiteSpace(requestedHost))
                return new TargetRef(requestedHost, requestedTargetId);

            var configuredHosts = GetConfiguredRuntimeHosts(client, nullInput);
            if (configuredHosts.Count == 1)
                return new TargetRef(configuredHosts[0], requestedTargetId);
        }

        var listRequest = new OperationRequest
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = $"cli-targets-{Guid.NewGuid():N}",
            Operation = "core.targets.list",
            Input = nullInput
        };

        var list = client
            .ExecuteAsync(listRequest)
            .GetAwaiter()
            .GetResult();

        if (!list.Ok ||
            list.Result?.Value is not JsonElement values ||
            values.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                list.Error?.Message ?? "Runtime did not return a target list.");
        }

        var liveTargets = new List<TargetRef>();

        foreach (var entry in values.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("running", out var running) ||
                running.ValueKind != JsonValueKind.True ||
                !entry.TryGetProperty("target", out var targetElement))
                continue;

            var host = targetElement.GetProperty("host").GetString();
            var id = targetElement.GetProperty("id").GetString();

            if (string.IsNullOrWhiteSpace(host) ||
                string.IsNullOrWhiteSpace(id) ||
                (!string.IsNullOrWhiteSpace(requestedHost) &&
                 !string.Equals(host, requestedHost, StringComparison.Ordinal)))
                continue;

            long? generation = null;
            if (targetElement.TryGetProperty(
                    "generation",
                    out var generationElement) &&
                generationElement.ValueKind == JsonValueKind.Number)
                generation = generationElement.GetInt64();

            liveTargets.Add(new TargetRef(host, id, generation));
        }

        if (!string.IsNullOrWhiteSpace(requestedTargetId))
        {
            return liveTargets.SingleOrDefault(
                       target => string.Equals(
                           target.Id,
                           requestedTargetId,
                           StringComparison.Ordinal))
                   ?? throw new InvalidOperationException(
                       $"Target '{requestedTargetId}' is not currently running.");
        }

        return liveTargets.Count switch
        {
            1 => liveTargets[0],
            0 => throw new InvalidOperationException(
                "No matching supported Adobe target is currently running."),
            _ => throw new InvalidOperationException(
                "Multiple targets are running; pass --target <target-id>.")
        };
    }

    private static IReadOnlyList<string> GetConfiguredRuntimeHosts(
        RuntimePipeClient client,
        JsonElement nullInput)
    {
        var health = client
            .ExecuteAsync(new OperationRequest
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = $"cli-health-{Guid.NewGuid():N}",
                Operation = "core.runtime.health",
                Input = nullInput
            })
            .GetAwaiter()
            .GetResult();

        if (!health.Ok ||
            health.Result?.Value is not JsonElement value ||
            value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("configuredHosts", out var hosts) ||
            hosts.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return hosts
            .EnumerateArray()
            .Where(static element => element.ValueKind == JsonValueKind.String)
            .Select(static element => element.GetString())
            .Where(static host => !string.IsNullOrWhiteSpace(host))
            .Cast<string>()
            .ToArray();
    }


    private static string? ParseTarget(string[] args) =>
        ParseOption(args, "--target");

    private static string RequireOption(string[] args, string name) =>
        ParseOption(args, name)
        ?? throw new ArgumentException($"{name} is required.");

    private static long RequireLongOption(
        string[] args,
        string name)
    {
        var raw = RequireOption(
            args,
            name);

        if (!long.TryParse(
                raw,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value))
        {
            throw new ArgumentException(
                $"{name} must be a 64-bit integer.");
        }

        return value;
    }

    private static long? ParseLongOption(
        string[] args,
        string name)
    {
        var raw = ParseOption(args, name);
        if (raw is null)
            return null;

        if (!long.TryParse(
                raw,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value))
        {
            throw new ArgumentException(
                $"{name} must be a 64-bit integer.");
        }

        return value;
    }

    private static int? ParseIntOption(
        string[] args,
        string name)
    {
        var raw = ParseOption(args, name);
        if (raw is null)
            return null;

        if (!int.TryParse(
                raw,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value))
        {
            throw new ArgumentException(
                $"{name} must be an integer.");
        }

        return value;
    }

    private static IReadOnlyList<OperationCondition>? ParseConditionsOption(
        string[] args,
        string jsonOption,
        string fileOption)
    {
        const int maxConditionDocumentChars = 1_000_000;

        var inline = ParseOption(
            args,
            jsonOption);
        var file = ParseOption(
            args,
            fileOption);

        if (inline is not null &&
            file is not null)
        {
            throw new ArgumentException(
                $"{jsonOption} and {fileOption} are mutually exclusive.");
        }

        if (inline is null &&
            file is null)
            return null;

        var raw = inline;
        if (file is not null)
        {
            var fullPath =
                Path.GetFullPath(file);

            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException(
                    $"Condition file '{fullPath}' was not found.",
                    fullPath);
            }

            raw = File.ReadAllText(fullPath);
        }

        if (raw is null ||
            raw.Length > maxConditionDocumentChars)
        {
            throw new ArgumentException(
                $"Condition document must not exceed {maxConditionDocumentChars} characters.");
        }

        var conditions = JsonSerializer.Deserialize(
            raw,
            ProtocolJsonContext.Default.ListOperationCondition)
            ?? throw new ArgumentException(
                "Condition document deserialized to null.");

        return conditions;
    }

    private static int? ParseWorkerWatchdogOption(string[] args)
    {
        var value = ParseIntOption(args, "--timeout-ms");
        if (value is null)
            return null;

        if (value is <
                OperationPolicy.MinWorkerWatchdogMs or
            >
                OperationPolicy.MaxWorkerWatchdogMs)
        {
            throw new ArgumentException(
                $"--timeout-ms must be between {OperationPolicy.MinWorkerWatchdogMs} and {OperationPolicy.MaxWorkerWatchdogMs}.");
        }

        return value;
    }

    private static int? ParseRetryBudgetOption(string[] args)
    {
        var value = ParseIntOption(args, "--retry-budget-ms");
        if (value is null)
            return null;

        if (!OperationPolicy.IsValidRetryBudget(value.Value))
        {
            throw new ArgumentException(
                $"--retry-budget-ms must be between " +
                $"{OperationPolicy.MinRetryBudgetMs} and " +
                $"{OperationPolicy.MaxRetryBudgetMs}.");
        }

        return value;
    }

    private static JsonElement ParseJsonArrayOption(
        string[] args,
        string name)
    {
        var raw = ParseOption(args, name);
        if (raw is null)
            return JsonSerializer.SerializeToElement(Array.Empty<object>());

        using var document = JsonDocument.Parse(raw);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"{name} must be a JSON array.");

        return document.RootElement.Clone();
    }

    private static string? ParseOption(string[] args, string name)
    {
        for (var i = 1; i < args.Length; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.Ordinal))
                continue;

            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                throw new ArgumentException($"{name} requires a value.");

            return args[i + 1];
        }

        return null;
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Skip(1).Any(arg => string.Equals(arg, name, StringComparison.Ordinal));


    private static int WriteHelp()
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            product = "COM Tool V2",
            version = ProductVersion,
            informationalVersion = InformationalVersion,
            protocolVersion = ProtocolVersion.Current,
            phase = "production-foundation",
            commands = new[]
            {
                "health [--pipe <name>]",
                "incidents [--pipe <name>]",
                "artifact-describe --artifact-id <id> [--pipe <name>]",
                "artifact-read --artifact-id <id> [--offset <bytes>] [--length <bytes>] [--pipe <name>]",
                "stdio [--pipe <name>]",
                "targets [--pipe <name>]",
                "capabilities [--target <id>] [--host <host>] [--lease <id>] [--pipe <name>]",
                "status [--target <id>] [--host <host>] [--lease <id>] [--pipe <name>]",
                "snapshot [--target <id>] [--host <host>] [--lease <id>] [--pipe <name>]",
                "reconcile --lease <id> [--pipe <name>] [--target <id>] [--host <host>]",
                "incident-resolve --lease <id> --incident-request-id <id> --expected-revision <n> (--changed | --unchanged) --rationale <text> [--evidence-json <json>] [--pipe <name>] [--target <id>] [--host <host>]",
                "incident-resolve-offline --target-id <id> --incident-request-id <id> --expected-updated-at <iso8601> (--changed | --unchanged) --rationale <text> [--evidence-json <json>] [--pipe <name>]",
                "mutation-reconcile --lease <id> --incident-request-id <id> --expected-revision <n> [--postconditions-json <array> | --postconditions-file <path>] [--pipe <name>] [--target <id>] [--host <host>]",
                "lease-acquire [--pipe <name>] [--target <id>] [--host <host>] [--ttl-ms <ms>]",
                "lease-renew --lease <id> [--pipe <name>] [--target <id>] [--host <host>] [--ttl-ms <ms>]",
                "lease-release --lease <id> [--pipe <name>] [--target <id>] [--host <host>]",
                "get --path <dotted-com-path> [--target <id>] [--host <host>] [--lease <id>] [--pipe <name>]",
                "call-read --path <allowlisted-com-method> [--args-json <array>] [--target <id>] [--host <host>] [--lease <id>] [--pipe <name>]",
                "artboards (--name <name> | --index <n> | --active) [--target <id>] [--host <host>] [--pipe <name>]",
                "layers (--name <name> | --index <n> | --active) [--target <id>] [--host <host>] [--pipe <name>]",
                "eval --lease <id> --request-id <stable-id> [--timeout-ms <100-3600000>] [--retry-budget-ms <0-3600000>] [--pipe <name>] (--expr <source> | --code <source>) [--effects <write-class>] [--args-json <array>] [--preconditions-json <array> | --preconditions-file <path>] [--postconditions-json <array> | --postconditions-file <path>] [--target <id>] [--host <host>]",
                "run-file --lease <id> --request-id <stable-id> --path <absolute-jsx-or-jsxbin> --sha256 <expected-sha256> [--timeout-ms <100-3600000>] [--retry-budget-ms <0-3600000>] [--pipe <name>] [--effects <write-class>] [--args-json <array>] [--preconditions-json <array> | --preconditions-file <path>] [--postconditions-json <array> | --postconditions-file <path>] [--target <id>] [--host <host>]"
            }
        }, JsonOptions));
        return 0;
    }

    private static int WriteVersion()
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            product = "COM Tool V2",
            component = "ComTool.Cli",
            version = ProductVersion,
            informationalVersion = InformationalVersion,
            protocolVersion = ProtocolVersion.Current
        }, JsonOptions));
        return 0;
    }

    private static int WriteError(
        string kind,
        string message,
        IReadOnlyList<string> suggestedActions,
        int? hresult = null,
        bool retryable = false,
        ExecutionState execution = ExecutionState.NotStarted)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(new
        {
            ok = false,
            protocolVersion = ProtocolVersion.Current,
            error = new
            {
                kind,
                message,
                retryable,
                execution,
                hResult = hresult,
                hResultHex = hresult is null
                    ? null
                    : $"0x{unchecked((uint)hresult.Value):X8}",
                suggestedActions
            }
        }, JsonOptions));

        return 1;
    }

    private sealed class RuntimeUnavailableException(
        string pipeName,
        Exception innerException)
        : Exception(
            "The persistent COM Tool V2 runtime is unreachable.",
            innerException)
    {
        public string PipeName { get; } = pipeName;
    }
}
