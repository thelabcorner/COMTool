using System.Text.Json;

namespace ComTool.Protocol;

public static class ProtocolJson
{
    public static OperationRequest DeserializeRequest(ReadOnlySpan<byte> utf8Json)
    {
        var request = JsonSerializer.Deserialize(
            utf8Json,
            ProtocolJsonContext.Default.OperationRequest)
            ?? throw new JsonException("Request deserialized to null");

        ValidateRequest(request);
        return request;
    }

    public static OperationRequest DeserializeRequest(string json) =>
        DeserializeRequest(System.Text.Encoding.UTF8.GetBytes(json));

    public static string Serialize(OperationRequest request) =>
        JsonSerializer.Serialize(request, ProtocolJsonContext.Default.OperationRequest);

    public static byte[] SerializeUtf8(OperationRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(
            request,
            ProtocolJsonContext.Default.OperationRequest);

    public static OperationResult DeserializeResult(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize(
            utf8Json,
            ProtocolJsonContext.Default.OperationResult)
        ?? throw new JsonException("Result deserialized to null");

    public static OperationResult DeserializeResult(string json) =>
        DeserializeResult(System.Text.Encoding.UTF8.GetBytes(json));

    public static string Serialize(OperationResult result) =>
        JsonSerializer.Serialize(result, ProtocolJsonContext.Default.OperationResult);

    public static byte[] SerializeUtf8(OperationResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(
            result,
            ProtocolJsonContext.Default.OperationResult);

    public static void ValidateRequest(OperationRequest request)
    {
        if (request.ProtocolVersion != ProtocolVersion.Current)
            throw new ProtocolValidationException(
                "unsupported_protocol_version",
                $"Protocol version {request.ProtocolVersion} is unsupported; expected {ProtocolVersion.Current}.");

        if (string.IsNullOrWhiteSpace(request.Id))
            throw new ProtocolValidationException("invalid_id", "Request id must be non-empty.");

        if (request.Id.Length > 128)
            throw new ProtocolValidationException("invalid_id", "Request id exceeds 128 characters.");

        if (string.IsNullOrWhiteSpace(request.Operation))
            throw new ProtocolValidationException("invalid_operation", "Operation must be non-empty.");

        if (request.Operation.Length > 256)
            throw new ProtocolValidationException("invalid_operation", "Operation exceeds 256 characters.");

        ValidateTimeout(request.Policy?.QueueTimeoutMs, nameof(OperationPolicy.QueueTimeoutMs));
        ValidateTimeout(request.Policy?.RetryBudgetMs, nameof(OperationPolicy.RetryBudgetMs));
        ValidateTimeout(request.Policy?.OperationSoftTimeoutMs, nameof(OperationPolicy.OperationSoftTimeoutMs));
        ValidateTimeout(request.Policy?.WorkerWatchdogMs, nameof(OperationPolicy.WorkerWatchdogMs));
        ValidateLeaseId(request.Policy?.LeaseId);
        ValidateConditions(
            request.Preconditions,
            "preconditions");
        ValidateConditions(
            request.Postconditions,
            "postconditions");
    }

    private static void ValidateConditions(
        IReadOnlyList<OperationCondition>? conditions,
        string collectionName)
    {
        if (conditions is null)
            return;

        if (conditions.Count > 128)
        {
            throw new ProtocolValidationException(
                "invalid_condition",
                $"{collectionName} exceeds 128 assertions.");
        }

        var ids = new HashSet<string>(
            StringComparer.Ordinal);

        foreach (var condition in conditions)
        {
            if (condition is null)
            {
                throw new ProtocolValidationException(
                    "invalid_condition",
                    $"{collectionName} contains a null assertion.");
            }

            if (string.IsNullOrWhiteSpace(condition.Id) ||
                condition.Id.Length > 128)
            {
                throw new ProtocolValidationException(
                    "invalid_condition",
                    $"{collectionName} assertion id must be non-empty and at most 128 characters.");
            }

            if (!ids.Add(condition.Id))
            {
                throw new ProtocolValidationException(
                    "duplicate_condition_id",
                    $"{collectionName} contains duplicate assertion id '{condition.Id}'.");
            }

            if (condition.Source is null ||
                string.IsNullOrWhiteSpace(
                    condition.Source.Operation) ||
                condition.Source.Operation.Length > 256)
            {
                throw new ProtocolValidationException(
                    "invalid_condition_source",
                    $"{collectionName} assertion '{condition.Id}' has an invalid source operation.");
            }

            if (condition.Source.Input.ValueKind ==
                JsonValueKind.Undefined)
            {
                throw new ProtocolValidationException(
                    "invalid_condition_source",
                    $"{collectionName} assertion '{condition.Id}' source input is missing.");
            }

            if (condition.Predicate is null)
            {
                throw new ProtocolValidationException(
                    "invalid_condition_predicate",
                    $"{collectionName} assertion '{condition.Id}' is missing a predicate.");
            }

            var requiresExpected =
                condition.Predicate.Kind is
                    "equals" or
                    "not_equals";

            var forbidsExpected =
                condition.Predicate.Kind is
                    "truthy" or
                    "falsey" or
                    "is_null" or
                    "not_null";

            if (!requiresExpected && !forbidsExpected)
            {
                throw new ProtocolValidationException(
                    "invalid_condition_predicate",
                    $"{collectionName} assertion '{condition.Id}' uses unsupported predicate '{condition.Predicate.Kind}'.");
            }

            if (requiresExpected)
            {
                if (condition.Predicate.Expected is null)
                {
                    throw new ProtocolValidationException(
                        "invalid_condition_predicate",
                        $"{collectionName} assertion '{condition.Id}' predicate '{condition.Predicate.Kind}' requires expected.");
                }

                ValidateProtocolValue(
                    condition.Predicate.Expected,
                    collectionName,
                    condition.Id);
            }
            else if (condition.Predicate.Expected is not null)
            {
                throw new ProtocolValidationException(
                    "invalid_condition_predicate",
                    $"{collectionName} assertion '{condition.Id}' predicate '{condition.Predicate.Kind}' must not specify expected.");
            }
        }
    }

    private static void ValidateProtocolValue(
        ProtocolValue value,
        string collectionName,
        string conditionId)
    {
        var valid = value.Kind switch
        {
            "null" => value.Value is null,
            "string" =>
                value.Value is { } stringValue &&
                stringValue.ValueKind == JsonValueKind.String,
            "number" =>
                value.Value is { } numberValue &&
                numberValue.ValueKind == JsonValueKind.Number,
            "boolean" =>
                value.Value is { } booleanValue &&
                booleanValue.ValueKind is
                    JsonValueKind.True or
                    JsonValueKind.False,
            "array" =>
                value.Value is { } arrayValue &&
                arrayValue.ValueKind == JsonValueKind.Array,
            "object" =>
                value.Value is { } objectValue &&
                objectValue.ValueKind == JsonValueKind.Object,
            _ => false
        };

        if (!valid)
        {
            throw new ProtocolValidationException(
                "invalid_condition_expected",
                $"{collectionName} assertion '{conditionId}' has an invalid expected ProtocolValue.");
        }
    }

    private static void ValidateLeaseId(string? leaseId)
    {
        if (leaseId is null)
            return;

        if (leaseId.Length is < 32 or > 128 ||
            leaseId.Any(static ch =>
                !(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-')))
        {
            throw new ProtocolValidationException(
                "invalid_lease_id",
                "LeaseId must be 32-128 ASCII letters, digits, '_' or '-'.");
        }
    }

    private static void ValidateTimeout(int? value, string name)
    {
        if (value is < 0)
            throw new ProtocolValidationException("invalid_timeout", $"{name} must be non-negative.");
    }
}

public sealed class ProtocolValidationException(string kind, string message) : Exception(message)
{
    public string Kind { get; } = kind;
}
