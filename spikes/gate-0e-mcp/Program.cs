using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--server", StringComparer.Ordinal))
        {
            await RunServerAsync(args);
            return 0;
        }

        return await RunSelfTestAsync();
    }

    private static async Task RunServerAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync();
    }

    private static async Task<int> RunSelfTestAsync()
    {
        var stderr = new List<string>();
        var (command, arguments) = SelfCommand("--server");

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "COM Tool V2 Gate 0E",
            Command = command,
            Arguments = arguments,
            ShutdownTimeout = TimeSpan.FromSeconds(5),
            StandardErrorLines = line =>
            {
                lock (stderr) stderr.Add(line);
            }
        });

        try
        {
            await using var client = await McpClient.CreateAsync(transport);

            var tools = await client.ListToolsAsync();
            var toolNames = tools.Select(t => t.Name).Order(StringComparer.Ordinal).ToArray();

            var statusResult = await client.CallToolAsync(
                "adobe_status",
                new Dictionary<string, object?>(),
                cancellationToken: CancellationToken.None);

            var mcpStatus = Text(statusResult);
            var directStatus = OperationKernel.Execute("core.status", "null");

            const string inputJson = "\"true\"";
            var executeResult = await client.CallToolAsync(
                "adobe_execute",
                new Dictionary<string, object?>
                {
                    ["operation"] = "core.echo",
                    ["inputJson"] = inputJson
                },
                cancellationToken: CancellationToken.None);

            var mcpExecute = Text(executeResult);
            var directExecute = OperationKernel.Execute("core.echo", inputJson);

            var checks = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["toolsDiscovered"] =
                    toolNames.SequenceEqual(["adobe_execute", "adobe_status"], StringComparer.Ordinal),
                ["statusNotError"] = statusResult.IsError is not true,
                ["executeNotError"] = executeResult.IsError is not true,
                ["statusParity"] = string.Equals(mcpStatus, directStatus, StringComparison.Ordinal),
                ["executeParity"] = string.Equals(mcpExecute, directExecute, StringComparison.Ordinal)
            };

            var ok = checks.Values.All(static x => x);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                gate = "0E",
                ok,
                sdk = "ModelContextProtocol 2.2.0",
                tools = toolNames,
                checks,
                stderrLines = stderr.Count
            }));

            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                gate = "0E",
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
            throw new InvalidDataException($"Expected exactly one text block, got {blocks.Length}");
        return blocks[0].Text;
    }

    private static (string Command, string[] Arguments) SelfCommand(params string[] args)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Environment.ProcessPath unavailable");
        var assemblyPath = Assembly.GetExecutingAssembly().Location;

        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            return (processPath, [assemblyPath, .. args]);
        }

        return (processPath, args);
    }
}

[McpServerToolType]
internal static class AdobeTools
{
    [McpServerTool(Name = "adobe_status")]
    [Description("Returns the normalized runtime status operation through the common operation kernel.")]
    public static string Status() =>
        OperationKernel.Execute("core.status", "null");

    [McpServerTool(Name = "adobe_execute")]
    [Description("Executes one normalized operation through the common operation kernel.")]
    public static string Execute(
        [Description("Normalized operation name.")] string operation,
        [Description("JSON-encoded operation input.")] string inputJson) =>
        OperationKernel.Execute(operation, inputJson);
}

internal static class OperationKernel
{
    public static string Execute(string operation, string inputJson)
    {
        using var inputDocument = JsonDocument.Parse(inputJson);
        var input = inputDocument.RootElement.Clone();

        object result = operation switch
        {
            "core.status" => new
            {
                protocolVersion = 1,
                operation,
                ok = true,
                status = "completed",
                targetState = "known",
                result = new
                {
                    kind = "object",
                    value = new
                    {
                        runtime = "gate-0e",
                        adapter = "operation-kernel"
                    }
                }
            },
            "core.echo" => new
            {
                protocolVersion = 1,
                operation,
                ok = true,
                status = "completed",
                targetState = "known",
                result = Tag(input)
            },
            _ => new
            {
                protocolVersion = 1,
                operation,
                ok = false,
                status = "unsupported_operation",
                targetState = "known",
                error = new
                {
                    kind = "unknown_operation",
                    retryable = false,
                    execution = "not_started"
                }
            }
        };

        return JsonSerializer.Serialize(result);
    }

    private static object Tag(JsonElement input) =>
        input.ValueKind switch
        {
            JsonValueKind.Null => new { kind = "null", value = (object?)null },
            JsonValueKind.String => new { kind = "string", value = (object?)input.GetString() },
            JsonValueKind.Number => new { kind = "number", value = (object?)input.Clone() },
            JsonValueKind.True or JsonValueKind.False =>
                new { kind = "boolean", value = (object?)input.GetBoolean() },
            JsonValueKind.Array => new { kind = "array", value = (object?)input.Clone() },
            JsonValueKind.Object => new { kind = "object", value = (object?)input.Clone() },
            _ => throw new InvalidDataException($"Unsupported input kind {input.ValueKind}")
        };
}
