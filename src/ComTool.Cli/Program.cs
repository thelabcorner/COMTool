using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Hosts.Illustrator;
using ComTool.Protocol;
using ComTool.Runtime;
using ComTool.Runtime.Ipc;
using ComTool.Supervisor;

internal static class Program
{
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
            var registry = new TargetRegistry(
            [
                new IllustratorAdapter()
            ]);

            if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
                return WriteHelp();

            var brokered = HasFlag(args, "--broker");
            var runtime = HasFlag(args, "--runtime");

            if (brokered && runtime)
                throw new ArgumentException(
                    "--broker and --runtime are mutually exclusive transports.");

            return args[0] switch
            {
                "health" => RunRuntimeOperation(
                    "core.runtime.health",
                    requestedTargetId: null,
                    requiresTarget: false,
                    pipeName: ParseOption(args, "--pipe")),
                "stdio" => RunStdioProxy(args),
                "targets" => RunTargets(
                    registry,
                    brokered,
                    runtime,
                    workerPath: ParseOption(args, "--worker"),
                    pipeName: ParseOption(args, "--pipe")),
                "capabilities" => RunCapabilities(
                    registry,
                    ParseTarget(args),
                    ParseOption(args, "--host"),
                    brokered,
                    runtime,
                    workerPath: ParseOption(args, "--worker"),
                    leaseId: ParseOption(args, "--lease"),
                    pipeName: ParseOption(args, "--pipe")),
                "status" => RunTargetOperation(
                    registry,
                    ParseTarget(args),
                    ParseOption(args, "--host"),
                    "core.target.status",
                    brokered,
                    runtime,
                    workerPath: ParseOption(args, "--worker"),
                    leaseId: ParseOption(args, "--lease"),
                    pipeName: ParseOption(args, "--pipe")),
                "snapshot" => RunTargetOperation(
                    registry,
                    ParseTarget(args),
                    ParseOption(args, "--host"),
                    "core.target.snapshot",
                    brokered,
                    runtime,
                    workerPath: ParseOption(args, "--worker"),
                    leaseId: ParseOption(args, "--lease"),
                    pipeName: ParseOption(args, "--pipe")),
                "reconcile" => RunRuntimeOnlyTargetOperation(
                    args,
                    brokered,
                    runtime,
                    "core.target.reconcile",
                    requireLease: true),
                "incident-resolve" => RunIncidentResolve(
                    args,
                    brokered,
                    runtime),
                "mutation-reconcile" => RunMutationReconcile(
                    args,
                    brokered,
                    runtime),
                "lease-acquire" => RunLeaseControl(
                    args,
                    brokered,
                    runtime,
                    "core.target.lease.acquire",
                    requireLease: false,
                    allowTtl: true),
                "lease-renew" => RunLeaseControl(
                    args,
                    brokered,
                    runtime,
                    "core.target.lease.renew",
                    requireLease: true,
                    allowTtl: true),
                "lease-release" => RunLeaseControl(
                    args,
                    brokered,
                    runtime,
                    "core.target.lease.release",
                    requireLease: true,
                    allowTtl: false),
                "get" => RunComGet(
                    registry,
                    args,
                    brokered,
                    runtime),
                "call-read" => RunComCallRead(
                    registry,
                    args,
                    brokered,
                    runtime),
                "artboards" => RunStructureRead(
                    registry,
                    args,
                    brokered,
                    runtime,
                    "illustrator.artboard.read"),
                "layers" => RunStructureRead(
                    registry,
                    args,
                    brokered,
                    runtime,
                    "illustrator.layer.read"),
                "eval" => RunScriptEval(
                    args,
                    brokered,
                    runtime),
                "run-file" => RunScriptRunFile(
                    args,
                    brokered,
                    runtime),
                _ => WriteError(
                    "unknown_command",
                    $"Unknown command '{args[0]}'.",
                    ["help"])
            };
        }
        catch (HostAdapterException ex)
        {
            return WriteError(
                ex.Kind,
                ex.Message,
                ex.Retryable ? ["retry"] : ["inspect_host_state"],
                ex.HResultCode);
        }
        catch (Exception ex)
        {
            return WriteError(
                ex.GetType().Name,
                ex.Message,
                ["inspect_runtime"]);
        }
    }

    private static int RunTargets(
        TargetRegistry registry,
        bool brokered,
        bool runtime,
        string? workerPath,
        string? pipeName = null)
    {
        if (runtime)
            return RunRuntimeOperation(
                "core.targets.list",
                requestedTargetId: null,
                requiresTarget: false,
                pipeName: pipeName);

        var targets = Discover(registry, brokered, workerPath);
        var payload = targets.Select(target => new
        {
            target = target.Target,
            target.Identity.ProcessId,
            target.Identity.ProcessStartedAt,
            target.Identity.ExecutablePath,
            target.Identity.HostVersion,
            target.Identity.AdapterVersion,
            target.Running,
            capabilities = target.Capabilities.Select(c => c.Name).ToArray()
        }).ToArray();

        Console.Out.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            protocolVersion = ProtocolVersion.Current,
            targets = payload
        }, JsonOptions));

        return 0;
    }

    private static int RunCapabilities(
        TargetRegistry registry,
        string? targetId,
        string? host,
        bool brokered,
        bool runtime,
        string? workerPath,
        string? leaseId,
        string? pipeName = null)
    {
        if (runtime)
            return RunRuntimeOperation(
                "core.target.capabilities",
                targetId,
                host,
                requiresTarget: true,
                leaseId: leaseId,
                pipeName: pipeName);

        if (leaseId is not null)
            throw new ArgumentException(
                "--lease is supported only with --runtime.");

        var target = SelectTarget(
            Discover(registry, brokered, workerPath),
            targetId);

        Console.Out.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            protocolVersion = ProtocolVersion.Current,
            target = target.Target,
            capabilities = target.Capabilities
        }, JsonOptions));

        return 0;
    }

    private static int RunTargetOperation(
        TargetRegistry registry,
        string? targetId,
        string? host,
        string operation,
        bool brokered,
        bool runtime,
        string? workerPath,
        JsonElement? input = null,
        string? leaseId = null,
        string? pipeName = null)
    {
        if (runtime)
            return RunRuntimeOperation(
                operation,
                targetId,
                host,
                requiresTarget: true,
                input,
                leaseId,
                pipeName: pipeName);

        if (leaseId is not null)
            throw new ArgumentException(
                "--lease is supported only with --runtime.");

        var target = SelectTarget(
            Discover(registry, brokered, workerPath),
            targetId);
        var request = CreateRequest(target, operation, input);

        return brokered
            ? RunBrokeredTargetOperation(target, request, workerPath)
            : RunDirectTargetOperation(registry, target, request);
    }

    private static int RunComGet(
        TargetRegistry registry,
        string[] args,
        bool brokered,
        bool runtime)
    {
        var path = RequireOption(args, "--path");
        var input = JsonSerializer.SerializeToElement(
            new { path },
            JsonOptions);

        return RunTargetOperation(
            registry,
            ParseTarget(args),
            ParseOption(args, "--host"),
            "com.get",
            brokered,
            runtime,
            ParseOption(args, "--worker"),
            input,
            ParseOption(args, "--lease"),
            pipeName: ParseOption(args, "--pipe"));
    }

    private static int RunComCallRead(
        TargetRegistry registry,
        string[] args,
        bool brokered,
        bool runtime)
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

        return RunTargetOperation(
            registry,
            ParseTarget(args),
            ParseOption(args, "--host"),
            "com.call.read",
            brokered,
            runtime,
            ParseOption(args, "--worker"),
            input,
            ParseOption(args, "--lease"),
            pipeName: ParseOption(args, "--pipe"));
    }

    private static int RunStructureRead(
        TargetRegistry registry,
        string[] args,
        bool brokered,
        bool runtime,
        string operation)
    {
        var input = BuildDocumentSelectorInput(args);

        return RunTargetOperation(
            registry,
            ParseTarget(args),
            ParseOption(args, "--host"),
            operation,
            brokered,
            runtime,
            ParseOption(args, "--worker"),
            input,
            ParseOption(args, "--lease"),
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

    private static int RunScriptEval(
        string[] args,
        bool brokered,
        bool runtime)
    {
        EnsureRuntimeOnly(brokered, runtime, args[0]);

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
            pipeName: ParseOption(args, "--pipe"));
    }

    private static int RunScriptRunFile(
        string[] args,
        bool brokered,
        bool runtime)
    {
        EnsureRuntimeOnly(brokered, runtime, args[0]);

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
            pipeName: ParseOption(args, "--pipe"));
    }

    private static int RunIncidentResolve(
        string[] args,
        bool brokered,
        bool runtime)
    {
        EnsureRuntimeOnly(
            brokered,
            runtime,
            args[0]);

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

    private static int RunMutationReconcile(
        string[] args,
        bool brokered,
        bool runtime)
    {
        EnsureRuntimeOnly(
            brokered,
            runtime,
            args[0]);

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
        bool brokered,
        bool runtime,
        string operation,
        bool requireLease)
    {
        EnsureRuntimeOnly(brokered, runtime, args[0]);

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
        bool brokered,
        bool runtime,
        string operation,
        bool requireLease,
        bool allowTtl)
    {
        EnsureRuntimeOnly(brokered, runtime, args[0]);

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

    private static void EnsureRuntimeOnly(
        bool brokered,
        bool runtime,
        string command)
    {
        if (runtime && !brokered)
            return;

        throw new ArgumentException(
            $"'{command}' is available only through --runtime.");
    }

    private static int RunDirectTargetOperation(
        TargetRegistry registry,
        HostTargetDescriptor target,
        OperationRequest request)
    {
        var adapter = registry.GetAdapter(target.Identity.Host);

        // Direct mode deliberately blocks on the adapter from this STA thread.
        // Brokered mode executes inside a dedicated per-host STA worker.
        var session = adapter
            .ConnectAsync(target)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        try
        {
            var result = session
                .ExecuteAsync(request)
                .AsTask()
                .GetAwaiter()
                .GetResult();

            Console.Out.WriteLine(ProtocolJson.Serialize(result));
            return result.Ok ? 0 : 1;
        }
        finally
        {
            session
                .DisposeAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
    }

    private static int RunBrokeredTargetOperation(
        HostTargetDescriptor target,
        OperationRequest request,
        string? workerPath)
    {
        var resolvedWorker = ResolveWorkerPath(workerPath);
        var state = new TargetStateMachine();
        var broker = WorkerBrokerClient
            .LaunchAsync(
                target,
                new WorkerBrokerOptions
                {
                    WorkerExecutablePath = resolvedWorker
                },
                state)
            .GetAwaiter()
            .GetResult();

        try
        {
            var result = broker
                .ExecuteAsync(request)
                .GetAwaiter()
                .GetResult();

            Console.Out.WriteLine(ProtocolJson.Serialize(result));
            return result.Ok ? 0 : 1;
        }
        finally
        {
            broker
                .DisposeAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
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
        string? pipeName = null)
    {
        var client = RuntimePipeClient
            .ConnectAsync(pipeName)
            .GetAwaiter()
            .GetResult();

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
                Policy = leaseId is null
                    ? null
                    : new OperationPolicy(LeaseId: leaseId),
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

        var pipeName = ParseOption(args, "--pipe");
        var client = RuntimePipeClient
            .ConnectAsync(pipeName)
            .GetAwaiter()
            .GetResult();

        try
        {
            var server = new global::ComTool.Transport.Stdio.NdjsonServer(
                new PipeDispatcher(client));

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
            client
                .DisposeAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
    }

    private sealed class PipeDispatcher(RuntimePipeClient client)
        : IOperationDispatcher
    {
        public ValueTask<OperationResult> ExecuteAsync(
            OperationRequest request,
            CancellationToken cancellationToken = default) =>
            new(client.ExecuteAsync(request, cancellationToken: cancellationToken));
    }

    private static TargetRef ResolveRuntimeTarget(
        RuntimePipeClient client,
        string? requestedTargetId,
        string? requestedHost,
        JsonElement nullInput)
    {        if (!string.IsNullOrWhiteSpace(requestedTargetId))
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

    private static OperationRequest CreateRequest(
        HostTargetDescriptor target,
        string operation,
        JsonElement? input = null)
    {
        if (input is not null)
        {
            return new OperationRequest
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = $"cli-{Guid.NewGuid():N}",
                Target = target.Target,
                Operation = operation,
                Input = input.Value
            };
        }

        using var nullDocument = JsonDocument.Parse("null");

        return new OperationRequest
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = $"cli-{Guid.NewGuid():N}",
            Target = target.Target,
            Operation = operation,
            Input = nullDocument.RootElement.Clone()
        };
    }

    private static IReadOnlyList<HostTargetDescriptor> Discover(
        TargetRegistry registry,
        bool brokered,
        string? workerPath)
    {
        if (!brokered)
        {
            return registry
                .DiscoverAllAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }

        // Brokered mode keeps COM discovery off the CLI thread entirely.
        // Illustrator is the only production adapter today; when additional
        // adapters graduate from spikes this becomes a host-catalog loop.
        var resolvedWorker = ResolveWorkerPath(workerPath);
        return WorkerDiscoveryClient
            .DiscoverAsync(
                IllustratorAdapter.HostName,
                new WorkerBrokerOptions
                {
                    WorkerExecutablePath = resolvedWorker
                })
            .GetAwaiter()
            .GetResult();
    }

    private static HostTargetDescriptor SelectTarget(
        IReadOnlyList<HostTargetDescriptor> targets,
        string? requestedId)
    {
        if (!string.IsNullOrWhiteSpace(requestedId))
        {
            return targets.SingleOrDefault(
                       target => string.Equals(
                           target.Target.Id,
                           requestedId,
                           StringComparison.Ordinal))
                   ?? throw new InvalidOperationException(
                       $"Target '{requestedId}' was not discovered.");
        }

        return targets.Count switch
        {
            1 => targets[0],
            0 => throw new InvalidOperationException(
                "No supported Adobe target is currently running."),
            _ => throw new InvalidOperationException(
                "Multiple targets are running; pass --target <target-id>.")
        };
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
            "Broker mode requires ComTool.Worker.exe. Pass --worker <path>, set COMTOOL_V2_WORKER_PATH, or distribute the worker beside the CLI.",
            sibling);
    }

    private static int WriteHelp()
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            product = "COM Tool V2",
            phase = "mutation-safety-alpha",
            commands = new[]
            {
                "health [--runtime] [--pipe <name>]",
                "stdio [--pipe <name>]",
                "targets [--broker --worker <path> | --runtime [--pipe <name>]]",
                "capabilities [--target <id>] [--host <host>] [--lease <id>] [--broker --worker <path> | --runtime [--pipe <name>]]",
                "status [--target <id>] [--host <host>] [--lease <id>] [--broker --worker <path> | --runtime [--pipe <name>]]",
                "snapshot [--target <id>] [--host <host>] [--lease <id>] [--broker --worker <path> | --runtime [--pipe <name>]]",
                "reconcile --runtime --lease <id> [--pipe <name>] [--target <id>] [--host <host>]",
                "incident-resolve --runtime --lease <id> --incident-request-id <id> --expected-revision <n> (--changed | --unchanged) --rationale <text> [--evidence-json <json>] [--pipe <name>] [--target <id>] [--host <host>]",
                "mutation-reconcile --runtime --lease <id> --incident-request-id <id> --expected-revision <n> [--postconditions-json <array> | --postconditions-file <path>] [--pipe <name>] [--target <id>] [--host <host>]",
                "lease-acquire --runtime [--pipe <name>] [--target <id>] [--host <host>] [--ttl-ms <ms>]",
                "lease-renew --runtime --lease <id> [--pipe <name>] [--target <id>] [--host <host>] [--ttl-ms <ms>]",
                "lease-release --runtime --lease <id> [--pipe <name>] [--target <id>] [--host <host>]",
                "get --path <dotted-com-path> [--target <id>] [--host <host>] [--lease <id>] [--broker --worker <path> | --runtime [--pipe <name>]]",
                "call-read --path <allowlisted-com-method> [--args-json <array>] [--target <id>] [--host <host>] [--lease <id>] [--broker --worker <path> | --runtime [--pipe <name>]]",
                "artboards (--name <name> | --index <n> | --active) [--target <id>] [--host <host>] [--broker --worker <path> | --runtime [--pipe <name>]]",
                "layers (--name <name> | --index <n> | --active) [--target <id>] [--host <host>] [--broker --worker <path> | --runtime [--pipe <name>]]",
                "eval --runtime --lease <id> --request-id <stable-id> [--pipe <name>] (--expr <source> | --code <source>) [--effects <write-class>] [--args-json <array>] [--preconditions-json <array> | --preconditions-file <path>] [--postconditions-json <array> | --postconditions-file <path>] [--target <id>] [--host <host>]",
                "run-file --runtime --lease <id> --request-id <stable-id> --path <absolute-jsx-or-jsxbin> --sha256 <expected-sha256> [--pipe <name>] [--effects <write-class>] [--args-json <array>] [--preconditions-json <array> | --preconditions-file <path>] [--postconditions-json <array> | --postconditions-file <path>] [--target <id>] [--host <host>]"
            }
        }, JsonOptions));
        return 0;
    }

    private static int WriteError(
        string kind,
        string message,
        IReadOnlyList<string> suggestedActions,
        int? hresult = null)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(new
        {
            ok = false,
            error = new
            {
                kind,
                message,
                retryable = false,
                hResult = hresult,
                hResultHex = hresult is null
                    ? null
                    : $"0x{unchecked((uint)hresult.Value):X8}",
                suggestedActions
            }
        }, JsonOptions));

        return 1;
    }
}
