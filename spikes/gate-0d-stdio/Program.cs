using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

internal static class Program
{
    private const int ProtocolVersion = 1;
    private static readonly HashSet<string> AllowedRootFields =
        new(StringComparer.Ordinal) { "protocolVersion", "id", "target", "operation", "input", "policy" };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static int Main()
    {
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var sw = Stopwatch.StartNew();
            Response response;
            try
            {
                response = Handle(line, sw);
            }
            catch (Exception ex)
            {
                sw.Stop();
                response = Error(
                    id: null,
                    status: "internal_error",
                    kind: "internal_error",
                    message: ex.Message,
                    retryable: false,
                    execution: "not_started",
                    targetState: "unknown",
                    elapsedMs: sw.Elapsed.TotalMilliseconds);
            }

            Console.Out.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
            Console.Out.Flush();
        }

        return 0;
    }

    private static Response Handle(string line, Stopwatch sw)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            sw.Stop();
            return Error(
                null,
                "invalid_request",
                "malformed_json",
                $"Malformed JSON at byte position {ex.BytePositionInLine}.",
                false,
                "not_started",
                "unknown",
                sw.Elapsed.TotalMilliseconds);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                sw.Stop();
                return Error(null, "invalid_request", "request_not_object",
                    "Request must be a JSON object.", false, "not_started", "unknown",
                    sw.Elapsed.TotalMilliseconds);
            }

            string? id = null;
            if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
                id = idElement.GetString();

            foreach (var property in root.EnumerateObject())
            {
                if (!AllowedRootFields.Contains(property.Name))
                {
                    sw.Stop();
                    return Error(id, "invalid_request", "unknown_field",
                        $"Unknown request field '{property.Name}'.", false, "not_started", "unknown",
                        sw.Elapsed.TotalMilliseconds);
                }
            }

            if (!root.TryGetProperty("protocolVersion", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.Number ||
                !versionElement.TryGetInt32(out var version))
            {
                sw.Stop();
                return Error(id, "invalid_request", "missing_protocol_version",
                    "protocolVersion must be an integer.", false, "not_started", "unknown",
                    sw.Elapsed.TotalMilliseconds);
            }

            if (version != ProtocolVersion)
            {
                sw.Stop();
                return Error(id, "unsupported_protocol", "unsupported_protocol_version",
                    $"Unsupported protocolVersion {version}; supported version is {ProtocolVersion}.",
                    false, "not_started", "unknown", sw.Elapsed.TotalMilliseconds);
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                sw.Stop();
                return Error(null, "invalid_request", "missing_request_id",
                    "id must be a non-empty string.", false, "not_started", "unknown",
                    sw.Elapsed.TotalMilliseconds);
            }

            if (!root.TryGetProperty("operation", out var operationElement) ||
                operationElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(operationElement.GetString()))
            {
                sw.Stop();
                return Error(id, "invalid_request", "missing_operation",
                    "operation must be a non-empty string.", false, "not_started", "unknown",
                    sw.Elapsed.TotalMilliseconds);
            }

            var operation = operationElement.GetString()!;
            root.TryGetProperty("input", out var input);

            Response response = operation switch
            {
                "core.echo" => Success(id, operation,
                    input.ValueKind == JsonValueKind.Undefined ? TaggedValue.Null() : TaggedValue.From(input),
                    sw),

                "core.capabilities" => Success(id, operation,
                    new TaggedValue("object", JsonSerializer.SerializeToElement(new
                    {
                        protocolVersion = ProtocolVersion,
                        transport = "stdio-ndjson",
                        strictRootFields = true,
                        taggedValues = true,
                        machineStdoutOnly = true
                    }, JsonOptions)),
                    sw),

                "core.fail" => Error(id, "operation_failed", "fixture_failure",
                    "Intentional semantic failure fixture.", false, "not_started", "known",
                    sw.Elapsed.TotalMilliseconds),

                _ => Error(id, "unsupported_operation", "unknown_operation",
                    $"Unknown operation '{operation}'.", false, "not_started", "known",
                    sw.Elapsed.TotalMilliseconds)
            };

            return response;
        }
    }

    private static Response Success(string id, string operation, TaggedValue value, Stopwatch sw)
    {
        sw.Stop();
        return new Response(
            ProtocolVersion,
            id,
            true,
            "completed",
            operation,
            value,
            null,
            "known",
            new Timing(Math.Round(sw.Elapsed.TotalMilliseconds, 3)));
    }

    private static Response Error(
        string? id,
        string status,
        string kind,
        string message,
        bool retryable,
        string execution,
        string targetState,
        double elapsedMs)
    {
        return new Response(
            ProtocolVersion,
            id,
            false,
            status,
            null,
            null,
            new ErrorInfo(kind, message, retryable, execution),
            targetState,
            new Timing(Math.Round(elapsedMs, 3)));
    }

    private sealed record Response(
        int ProtocolVersion,
        string? Id,
        bool Ok,
        string Status,
        string? Operation,
        TaggedValue? Result,
        ErrorInfo? Error,
        string TargetState,
        Timing Timing);

    private sealed record ErrorInfo(
        string Kind,
        string Message,
        bool Retryable,
        string Execution);

    private sealed record Timing(double ExecuteMs);

    private sealed record TaggedValue(string Kind, JsonElement? Value)
    {
        public static TaggedValue Null() => new("null", null);

        public static TaggedValue From(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Null => Null(),
                JsonValueKind.String => new("string", element.Clone()),
                JsonValueKind.Number => new("number", element.Clone()),
                JsonValueKind.True or JsonValueKind.False => new("boolean", element.Clone()),
                JsonValueKind.Array => new("array", element.Clone()),
                JsonValueKind.Object => new("object", element.Clone()),
                _ => throw new InvalidDataException($"Unsupported JSON value kind {element.ValueKind}")
            };
        }
    }
}
