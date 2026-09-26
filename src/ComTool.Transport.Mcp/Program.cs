using System.Reflection;
using System.Text.Json;
using ComTool.Protocol;
using ComTool.Runtime;
using ComTool.Runtime.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

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

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 &&
            args[0] is "--help" or "-h" or "help")
            return WriteHelp();
        if (args.Length > 0 &&
            args[0] is "--version" or "-v" or "version")
            return WriteVersion();

        if (args.Contains("--server", StringComparer.Ordinal))
        {
            await RunServerAsync(args);
            return 0;
        }

        return await RunSelfTestAsync(args);
    }

    private static int WriteHelp()
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            component = "ComTool.Transport.Mcp",
            version = ProductVersion,
            informationalVersion = InformationalVersion,
            protocolVersion = ProtocolVersion.Current,
            usage = new[]
            {
                "ComTool.Transport.Mcp.exe --server [--pipe <name>]",
                "ComTool.Transport.Mcp.exe --self-test [--pipe <name>]"
            }
        }));
        return 0;
    }

    private static int WriteVersion()
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            component = "ComTool.Transport.Mcp",
            version = ProductVersion,
            informationalVersion = InformationalVersion,
            protocolVersion = ProtocolVersion.Current,
            sdk = "ModelContextProtocol 2.2.0"
        }));
        return 0;
    }

    private static async Task RunServerAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // stdout is the MCP wire. Keep it machine-clean; the gate-0e evidence
        // (zero stderr noise) is preserved by not attaching a console logger.
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton(
            new RuntimeBridge(Option(args, "--pipe")));
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync();
    }

    private static async Task<int> RunSelfTestAsync(string[] args)
    {
        var stderr = new List<string>();
        var pipeName = Option(args, "--pipe");
        var (command, arguments) = SelfCommand(
            pipeName is null
                ? ["--server"]
                : ["--server", "--pipe", pipeName]);

        try
        {
            await using var client = await McpClient.CreateAsync(
                new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = "COM Tool V2 MCP adapter",
                    Command = command,
                    Arguments = arguments,
                    ShutdownTimeout = TimeSpan.FromSeconds(5),
                    StandardErrorLines = line =>
                    {
                        lock (stderr) stderr.Add(line);
                    }
                }));

            var tools = await client.ListToolsAsync();
            var toolNames = tools
                .Select(tool => tool.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();

            await using var runtime = await RuntimePipeClient.ConnectAsync(
                pipeName,
                timeout: TimeSpan.FromSeconds(10));

            var statusRequest = new OperationRequest
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = "mcp-selftest-health",
                Operation = "core.runtime.health",
                Input = NullInput()
            };

            var mcpStatus = Text(await client.CallToolAsync(
                "adobe_execute",
                Values(
                    ("operation", "core.runtime.health"),
                    ("inputJson", "null"),
                    ("requestId", statusRequest.Id)),
                cancellationToken: CancellationToken.None));

            var directStatus = ProtocolJson.Serialize(
                await runtime.ExecuteAsync(statusRequest));

            var echoRequest = new OperationRequest
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = "mcp-selftest-unsupported",
                Operation = "core.runtime.does_not_exist",
                Input = NullInput()
            };

            var mcpUnsupported = Text(await client.CallToolAsync(
                "adobe_execute",
                Values(
                    ("operation", "core.runtime.does_not_exist"),
                    ("inputJson", "null"),
                    ("requestId", echoRequest.Id)),
                cancellationToken: CancellationToken.None));

            var directUnsupported = ProtocolJson.Serialize(
                await runtime.ExecuteAsync(echoRequest));

            var checks = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["toolsDiscovered"] = toolNames.Contains(
                    "adobe_execute",
                    StringComparer.Ordinal),
                ["statusParity"] = EnvelopeParity(mcpStatus, directStatus),
                ["errorEnvelopeParity"] = EnvelopeParity(
                    mcpUnsupported,
                    directUnsupported),
                ["zeroStderrNoise"] = stderr.Count == 0
            };

            var ok = checks.Values.All(static value => value);

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                adapter = "ComTool.Transport.Mcp",
                ok,
                sdk = "ModelContextProtocol 2.2.0",
                tools = toolNames,
                checks
            }));

            return ok ? 0 : 1;        }
        catch (Exception ex)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                adapter = "ComTool.Transport.Mcp",
                ok = false,
                error = new
                {
                    kind = ex.GetType().Name,
                    message = ex.Message,
                    hresult = ex.HResult,
                    hresultHex = $"0x{unchecked((uint)ex.HResult):X8}"
                },
                stderr
            }));
            return 1;
        }
    }

    private static string Text(CallToolResult result)
    {
        var blocks = result.Content.OfType<TextContentBlock>().ToArray();
        if (blocks.Length != 1)
            throw new InvalidDataException(
                $"Expected exactly one text block, got {blocks.Length}.");
        return blocks[0].Text;
    }

    /// <summary>
    /// Compares two canonical OperationResult envelopes by every semantic field
    /// while excluding fields that are volatile telemetry by design:
    /// the <c>timing</c> block and the runtime <c>uptimeMs</c> counter. Both
    /// legitimately differ between two independent executions; including them
    /// would turn a transport-parity check into a latency race. Everything else
    /// (ids, operation, status, target state, result kind/value, error) must match.
    /// </summary>
    private static bool EnvelopeParity(string left, string right)
    {
        try
        {
            var leftNode = Normalize(left);
            var rightNode = Normalize(right);

            return string.Equals(
                leftNode,
                rightNode,
                StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Normalize(string envelope)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(envelope);

        if (node is System.Text.Json.Nodes.JsonObject root)
        {
            root.Remove("timing");

            if (root["result"] is System.Text.Json.Nodes.JsonObject result &&
                result["value"] is System.Text.Json.Nodes.JsonObject value)
            {
                value.Remove("uptimeMs");
            }
        }

        return node?.ToJsonString() ?? string.Empty;
    }

    private static Dictionary<string, object?> Values(
        params (string Key, object? Value)[] pairs)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
            map[key] = value;
        return map;
    }

    private static JsonElement NullInput()
    {
        using var document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }

    private static string? Option(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
                return args[i + 1];
        }

        return null;
    }

    private static (string Command, string[] Arguments) SelfCommand(
        params string[] args)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "Environment.ProcessPath unavailable.");
        var assemblyPath = Assembly.GetExecutingAssembly().Location;

        if (string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            return (processPath, [assemblyPath, .. args]);
        }

        return (processPath, args);
    }
}

/// <summary>
/// The single translation layer between MCP tool calls and the versioned
/// operation protocol. It contains no Adobe/host logic; every call becomes one
/// <see cref="OperationRequest"/> executed by the ordinary runtime supervisor
/// over the ordinary runtime pipe.
/// </summary>
internal sealed class RuntimeBridge(string? defaultPipeName = null)
{
    public async Task<string> ExecuteAsync(
        string operation,
        string inputJson,
        string? requestId,
        string? leaseId,
        string? targetHost,
        string? targetId,
        CancellationToken cancellationToken,
        string? pipeName = null)
    {
        JsonElement input;
        try
        {
            using var document = JsonDocument.Parse(inputJson);
            input = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return ProtocolJson.Serialize(
                Invalid(operation, requestId, "invalid_json", ex.Message));
        }

        if (string.IsNullOrWhiteSpace(operation))
        {
            return ProtocolJson.Serialize(
                Invalid(
                    operation,
                    requestId,
                    "invalid_operation",
                    "Operation must be non-empty."));
        }

        var target = string.IsNullOrWhiteSpace(targetHost) &&
                     string.IsNullOrWhiteSpace(targetId)
            ? null
            : new TargetRef(targetHost ?? "illustrator", targetId);

        var request = new OperationRequest
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = string.IsNullOrWhiteSpace(requestId)
                ? $"mcp-{Guid.NewGuid():N}"
                : requestId,
            Target = target,
            Operation = operation,
            Input = input,
            Policy = string.IsNullOrWhiteSpace(leaseId)
                ? null
                : new OperationPolicy(LeaseId: leaseId)
        };

        try
        {
            ProtocolJson.ValidateRequest(request);
        }
        catch (ProtocolValidationException ex)
        {
            return ProtocolJson.Serialize(
                Invalid(request.Operation, request.Id, ex.Kind, ex.Message));
        }

        try
        {
            await using var client = await RuntimePipeClient.ConnectAsync(
                pipeName ?? defaultPipeName,
                cancellationToken: cancellationToken);

            var result = await client.ExecuteAsync(
                request,
                cancellationToken: cancellationToken);

            return ProtocolJson.Serialize(result);
        }
        catch (RuntimeRequestInterruptedException ex)
        {
            return ProtocolJson.Serialize(
                TransportFailure(
                    request,
                    "runtime_request_interrupted",
                    ex.Message,
                    retryable: false,
                    execution: ex.Execution,
                    ["inspect_runtime", "inspect_mutation_ledger"]));
        }
        catch (OperationCanceledException ex)
            when (cancellationToken.IsCancellationRequested)
        {
            return ProtocolJson.Serialize(
                TransportFailure(
                    request,
                    "runtime_transport_cancelled",
                    ex.Message,
                    retryable: true,
                    execution: ExecutionState.NotStarted,
                    ["retry_after_runtime_recovery"]));
        }
        catch (OperationCanceledException ex)
        {
            var callerCancelled = cancellationToken.IsCancellationRequested;
            return ProtocolJson.Serialize(
                TransportFailure(
                    request,
                    callerCancelled
                        ? "runtime_transport_cancelled"
                        : "runtime_unreachable",
                    ex.Message,
                    retryable: true,
                    execution: ExecutionState.NotStarted,
                    callerCancelled
                        ? ["retry_after_runtime_recovery"]
                        : ["start_runtime", "inspect_runtime"]));
        }
        catch (Exception ex)
        {
            return ProtocolJson.Serialize(
                TransportFailure(
                    request,
                    "runtime_unreachable",
                    ex.Message,
                    retryable: true,
                    execution: ExecutionState.NotStarted,
                    ["start_runtime", "inspect_runtime"]));
        }
    }

    private static OperationResult TransportFailure(
        OperationRequest request,
        string kind,
        string message,
        bool retryable,
        ExecutionState execution,
        IReadOnlyList<string> suggestedActions) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = false,
            Status = OperationStatus.Failed,
            TargetState = TargetState.Unavailable,
            Error = new ProtocolError
            {
                Kind = kind,
                Message = message,
                Retryable = retryable,
                Execution = execution,
                SuggestedActions = suggestedActions
            }
        };

    private static OperationResult Invalid(
        string operation,
        string? id,
        string kind,
        string message) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = string.IsNullOrWhiteSpace(id)
                ? $"mcp-{Guid.NewGuid():N}"
                : id,
            Operation = string.IsNullOrWhiteSpace(operation)
                ? "_invalid"
                : operation,
            Ok = false,
            Status = OperationStatus.InvalidRequest,
            TargetState = TargetState.Known,
            Error = new ProtocolError
            {
                Kind = kind,
                Message = message,
                Retryable = false,
                Execution = ExecutionState.NotStarted,
                SuggestedActions = ["fix_request_and_retry"]
            }
        };
}

[McpServerToolType]
internal sealed class AdobeTools(RuntimeBridge bridge)
{
    [McpServerTool(Name = "adobe_execute")]
    [System.ComponentModel.Description(
        "Execute one versioned COM Tool operation through the shared runtime. " +
        "Returns the canonical OperationResult JSON.")]
    public Task<string> ExecuteAsync(
        [System.ComponentModel.Description(
            "Normalized operation name, e.g. core.runtime.health, core.targets.list, com.get, script.eval.")]
        string operation,
        [System.ComponentModel.Description("JSON-encoded operation input, or 'null'.")]
        string inputJson = "null",
        [System.ComponentModel.Description(
            "Caller-stable request id; required for durable mutation replay.")]
        string? requestId = null,
        [System.ComponentModel.Description("Exclusive target lease id, when required.")]
        string? leaseId = null,
        [System.ComponentModel.Description("Target host family, e.g. 'illustrator'.")]
        string? targetHost = null,
        [System.ComponentModel.Description("Opaque generation-specific target id.")]
        string? targetId = null,
        CancellationToken cancellationToken = default) =>
        bridge.ExecuteAsync(
            operation,
            inputJson,
            requestId,
            leaseId,
            targetHost,
            targetId,
            cancellationToken);
}
