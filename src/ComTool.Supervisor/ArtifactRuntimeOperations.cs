using System.Text.Json;
using ComTool.Protocol;
using ComTool.Runtime.Artifacts;

namespace ComTool.Supervisor;

/// <summary>
/// Protocol adapter over the runtime-owned artifact store. Retrieval is by
/// opaque artifact id only; filesystem paths never enter or leave this surface.
/// </summary>
internal static class ArtifactRuntimeOperations
{
    public const string DescribeOperation = "core.artifact.describe";
    public const string ReadOperation = "core.artifact.read";

    // The outer transports use 1 MiB frames. 512 KiB of raw bytes expands to
    // ~683 KiB in base64, leaving ample room for the descriptor and envelope.
    public const int MaxProtocolReadByteCount = 512 * 1024;

    private static readonly JsonSerializerOptions PayloadJson =
        new(JsonSerializerDefaults.Web);

    public static OperationResult Execute(
        IArtifactStore store,
        OperationRequest request,
        double totalMs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return request.Operation switch
            {
                DescribeOperation =>
                    Describe(store, request, totalMs, cancellationToken),
                ReadOperation =>
                    Read(store, request, totalMs, cancellationToken),
                _ => Failure(
                    request,
                    OperationStatus.UnsupportedOperation,
                    "unsupported_operation",
                    $"Artifact operation '{request.Operation}' is not supported.",
                    totalMs)
            };
        }
        catch (ArtifactStoreException ex)
        {
            var invalid = ex.Kind is
                "artifact_id_invalid" or
                "artifact_not_found" or
                "artifact_expired" or
                "artifact_range_invalid" or
                "artifact_range_too_large" or
                "artifact_range_out_of_bounds";

            return Failure(
                request,
                invalid
                    ? OperationStatus.InvalidRequest
                    : OperationStatus.Failed,
                ex.Kind,
                ex.Message,
                totalMs);
        }
        catch (ArgumentException ex)
        {
            return Failure(
                request,
                OperationStatus.InvalidRequest,
                "invalid_artifact_request",
                ex.Message,
                totalMs);
        }
    }

    private static OperationResult Describe(
        IArtifactStore store,
        OperationRequest request,
        double totalMs,
        CancellationToken cancellationToken)
    {
        var fields = ReadFields(request.Input, "artifactId");
        var artifactId = RequiredString(fields, "artifactId");

        var descriptor = store.Describe(
            artifactId,
            cancellationToken);

        var payload = JsonSerializer.SerializeToElement(
            new { artifact = descriptor },
            PayloadJson);

        return Success(request, payload, totalMs);
    }

    private static OperationResult Read(
        IArtifactStore store,
        OperationRequest request,
        double totalMs,
        CancellationToken cancellationToken)
    {
        var fields = ReadFields(
            request.Input,
            "artifactId",
            "offset",
            "length");
        var artifactId = RequiredString(fields, "artifactId");
        var descriptor = store.Describe(
            artifactId,
            cancellationToken);

        var hasOffset = fields.TryGetValue("offset", out var offsetElement);
        var hasLength = fields.TryGetValue("length", out var lengthElement);

        ArtifactRange? range = null;
        if (hasOffset || hasLength)
        {
            if (!hasLength)
            {
                throw new ArgumentException(
                    "'length' is required when 'offset' is supplied.");
            }

            var offset = hasOffset
                ? ReadInt64(offsetElement, "offset")
                : 0L;
            var length = ReadInt64(lengthElement, "length");

            if (length > MaxProtocolReadByteCount)
            {
                throw new ArtifactStoreException(
                    "artifact_range_too_large",
                    $"Artifact protocol reads are limited to " +
                    $"{MaxProtocolReadByteCount} raw bytes.");
            }

            range = new ArtifactRange(offset, length);
        }
        else if (descriptor.ByteCount > MaxProtocolReadByteCount)
        {
            return Failure(
                request,
                OperationStatus.InvalidRequest,
                "artifact_range_required",
                $"Artifact contains {descriptor.ByteCount} bytes; whole reads " +
                $"are limited to {MaxProtocolReadByteCount}. Request an " +
                "explicit offset/length range.",
                totalMs);
        }

        var read = store.GetRange(
            artifactId,
            range,
            cancellationToken);

        if (read.ReturnedByteCount > MaxProtocolReadByteCount)
        {
            throw new ArtifactStoreException(
                "artifact_range_too_large",
                $"Artifact protocol reads are limited to " +
                $"{MaxProtocolReadByteCount} raw bytes.");
        }

        var payload = JsonSerializer.SerializeToElement(
            new
            {
                artifact = read.Descriptor,
                offset = read.Offset,
                returnedByteCount = read.ReturnedByteCount,
                isPartial = read.IsPartial,
                contentBase64 = Convert.ToBase64String(read.Content.Span)
            },
            PayloadJson);

        return Success(request, payload, totalMs);
    }

    private static Dictionary<string, JsonElement> ReadFields(
        JsonElement input,
        params string[] allowed)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "Artifact operation input must be a JSON object.");
        }

        var accepted = allowed.ToHashSet(StringComparer.Ordinal);
        var fields = new Dictionary<string, JsonElement>(
            StringComparer.Ordinal);

        foreach (var property in input.EnumerateObject())
        {
            if (!accepted.Contains(property.Name))
            {
                throw new ArgumentException(
                    $"Unknown artifact input field '{property.Name}'.");
            }

            if (!fields.TryAdd(property.Name, property.Value.Clone()))
            {
                throw new ArgumentException(
                    $"Duplicate artifact input field '{property.Name}'.");
            }
        }

        return fields;
    }

    private static string RequiredString(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        if (!fields.TryGetValue(name, out var element) ||
            element.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new ArgumentException(
                $"'{name}' must be a non-empty string.");
        }

        return element.GetString()!;
    }

    private static long ReadInt64(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt64(out var value))
        {
            throw new ArgumentException(
                $"'{name}' must be an integer.");
        }

        return value;
    }

    private static OperationResult Success(
        OperationRequest request,
        JsonElement payload,
        double totalMs) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = request.Id,
            Operation = request.Operation,
            Ok = true,
            Status = OperationStatus.Completed,
            TargetState = TargetState.Known,
            Result = ProtocolValue.From(payload),
            Timing = new OperationTiming(TotalMs: totalMs)
        };

    private static OperationResult Failure(
        OperationRequest request,
        OperationStatus status,
        string kind,
        string message,
        double totalMs) =>
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
                Execution = ExecutionState.NotStarted,
                SuggestedActions =
                    kind == "artifact_range_required"
                        ? ["request_a_bounded_range"]
                        : null
            },
            Timing = new OperationTiming(TotalMs: totalMs)
        };
}
