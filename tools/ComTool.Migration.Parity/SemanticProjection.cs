using System.Text.Json;

namespace ComTool.Migration.Parity;

/// <summary>
/// Projects a legacy or V2 envelope onto the same nine canonical semantic
/// fields so the two can be compared without a byte comparison. Every field is
/// derived from data that is actually present in the sample; a field the
/// transport cannot express projects to <c>absent</c>, which is a real
/// semantic answer rather than a missing measurement.
/// </summary>
internal static class SemanticProjection
{
    public const string Outcome = "outcome";
    public const string PayloadKind = "payloadKind";
    public const string ValueType = "valueType";
    public const string HostFailureIdentified = "hostFailureIdentified";
    public const string Retryable = "retryable";
    public const string Execution = "execution";
    public const string TargetIdentity = "targetIdentity";
    public const string TargetState = "targetState";
    public const string RecoverableSuggestion = "recoverableSuggestion";

    public static readonly string[] Fields =
    [
        Outcome,
        PayloadKind,
        ValueType,
        HostFailureIdentified,
        Retryable,
        Execution,
        TargetIdentity,
        TargetState,
        RecoverableSuggestion
    ];

    public const string Absent = "absent";

    public static IReadOnlyDictionary<string, string> FromLegacy(
        JsonElement envelope) => new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
        [Outcome] = Flag(envelope, "ok") ? "success" : "failure",
        [PayloadKind] = LegacyPayloadKind(envelope),
        [ValueType] = ValueKindOf(OptionalPayload(envelope)),
        [HostFailureIdentified] =
            LegacyHostFailureIdentified(envelope) ? "true" : "false",
        [Retryable] = DeclaredFlag(envelope, "retryable"),
        [Execution] = DeclaredString(envelope, "execution"),
        [TargetIdentity] = TargetIdentityOf(OptionalRequest(legacy: envelope, null)),
        [TargetState] = DeclaredString(envelope, "targetState"),
        [RecoverableSuggestion] =
            LegacyRecoverableSuggestion(envelope) ? "true" : "false"
    };

    public static IReadOnlyDictionary<string, string> FromV2(
        JsonElement request,
        JsonElement result)
    {
        var ok = Flag(result, "ok");
        var error = OptionalObject(result, "error");
        var status = DeclaredString(result, "status");

        return new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            [Outcome] = ok ? "success" : "failure",
            [PayloadKind] = V2PayloadKind(result),
            [ValueType] = V2ValueType(result),
            [HostFailureIdentified] = V2HostFailureIdentified(result, status)
                ? "true"
                : "false",
            [Retryable] = error is { } value
                ? DeclaredFlag(value, "retryable")
                : Absent,
            [Execution] = error is { } execution
                ? DeclaredString(execution, "execution")
                : Absent,
            [TargetIdentity] = TargetIdentityOf(request),
            [TargetState] = DeclaredString(result, "targetState"),
            [RecoverableSuggestion] = V2RecoverableSuggestion(result) ? "true" : "false"
        };
    }

    public sealed record Comparison(
        bool Passed,
        IReadOnlyList<string> EquivalenceViolations,
        IReadOnlyList<string> DivergenceViolations,
        IReadOnlyList<string> UnaccountedFields,
        IReadOnlyList<string> Conflicts,
        IReadOnlyList<string> Observed);

    /// <summary>
    /// Accounts for every projection field. A field must be declared
    /// equivalent (and then match) or declared divergent (and then match the
    /// observed pair). Nothing passes by omission.
    /// </summary>
    public static Comparison Compare(
        IReadOnlyDictionary<string, string> v1,
        IReadOnlyDictionary<string, string> v2,
        IReadOnlyList<string> equivalence,
        IReadOnlyList<DeclaredDivergence> divergences)
    {
        var equivalenceSet = equivalence.ToHashSet(StringComparer.Ordinal);
        var divergenceFields = new HashSet<string>(StringComparer.Ordinal);
        var conflicts = new List<string>();
        var equivalenceViolations = new List<string>();
        var divergenceViolations = new List<string>();
        var unaccounted = new List<string>();
        var observed = new List<string>();

        foreach (var field in Fields)
        {
            var left = v1[field];
            var right = v2[field];
            observed.Add($"{field}={left}|{right}");

            var declaredEquivalent = equivalenceSet.Contains(field);
            var declaredDivergent = divergenceFields.Add(field) &&
                divergences.Any(item =>
                    string.Equals(
                        item.Field,
                        field,
                        StringComparison.Ordinal));

            if (declaredEquivalent && declaredDivergent)
            {
                conflicts.Add(
                    $"'{field}' is declared both equivalent and divergent.");
            }

            if (declaredEquivalent)
            {
                if (!string.Equals(left, right, StringComparison.Ordinal))
                {
                    equivalenceViolations.Add(
                        $"'{field}' is declared equivalent but observed " +
                        $"v1='{left}' v2='{right}'.");
                }

                continue;
            }

            if (declaredDivergent)
            {
                var declaration = divergences.First(item =>
                    string.Equals(item.Field, field, StringComparison.Ordinal));

                if (string.Equals(left, right, StringComparison.Ordinal))
                {
                    divergenceViolations.Add(
                        $"'{field}' is declared divergent but both sides " +
                        $"project to '{left}'.");
                }

                if (!string.Equals(
                        declaration.V1,
                        left,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        declaration.V2,
                        right,
                        StringComparison.Ordinal))
                {
                    divergenceViolations.Add(
                        $"'{field}' divergence is stale: declared " +
                        $"v1='{declaration.V1}' v2='{declaration.V2}', " +
                        $"observed v1='{left}' v2='{right}'.");
                }

                continue;
            }

            unaccounted.Add(
                $"'{field}' is unaccounted for (observed v1='{left}' " +
                $"v2='{right}').");
        }

        foreach (var declaration in divergences)
        {
            if (!Fields.Contains(declaration.Field, StringComparer.Ordinal))
                divergenceViolations.Add(
                    $"Divergence names unknown projection field " +
                    $"'{declaration.Field}'.");
        }

        foreach (var field in equivalence)
        {
            if (!Fields.Contains(field, StringComparer.Ordinal))
                equivalenceViolations.Add(
                    $"Equivalence names unknown projection field '{field}'.");
        }

        return new Comparison(
            equivalenceViolations.Count == 0 &&
            divergenceViolations.Count == 0 &&
            unaccounted.Count == 0 &&
            conflicts.Count == 0,
            equivalenceViolations,
            divergenceViolations,
            unaccounted,
            conflicts,
            observed);
    }

    private static JsonElement? OptionalPayload(JsonElement envelope) =>
        envelope.TryGetProperty("result", out var result) ? result : null;

    private static string LegacyPayloadKind(JsonElement envelope)
    {
        if (OptionalPayload(envelope) is not { } payload)
            return "none";

        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("path", out _) &&
            payload.TryGetProperty("bytes", out _))
        {
            return "artifact_reference";
        }

        return payload.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? "structure"
            : "scalar";
    }

    private static string V2PayloadKind(JsonElement result)
    {
        if (OptionalObject(result, "result") is not { } payload)
            return "none";

        if (payload.TryGetProperty("kind", out var kind) &&
            kind.GetString() is "artifact_reference")
        {
            return "artifact_reference";
        }

        return kind.GetString() is "object" or "array" ? "structure" : "scalar";
    }

    private static string V2ValueType(JsonElement result) =>
        OptionalObject(result, "result") is { } payload &&
        payload.TryGetProperty("kind", out var kind)
            ? kind.GetString() ?? "unknown"
            : "none";

    private static string ValueKindOf(JsonElement? payload) =>
        payload?.ValueKind switch
        {
            null => "none",
            JsonValueKind.Null => "null",
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Array => "array",
            JsonValueKind.Object => "object",
            _ => "unknown"
        };

    private static bool LegacyHostFailureIdentified(JsonElement envelope)
    {
        if (!Flag(envelope, "ok") &&
            envelope.TryGetProperty("hresult", out _))
        {
            return true;
        }

        return envelope.TryGetProperty("result", out var result) &&
            ContainsNestedError(result);
    }

    private static bool ContainsNestedError(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("_error") ||
                        ContainsNestedError(property.Value))
                    {
                        return true;
                    }
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ContainsNestedError(item))
                        return true;
                }
                break;
        }

        return false;
    }

    private static bool V2HostFailureIdentified(
        JsonElement result,
        string status)
    {
        if (OptionalObject(result, "error") is { } error &&
            error.TryGetProperty("hResult", out _))
        {
            return true;
        }

        return status is "host_busy" or "target_unavailable";
    }

    private static bool LegacyRecoverableSuggestion(JsonElement envelope) =>
        (envelope.TryGetProperty("suggestion", out var suggestion) &&
         suggestion.ValueKind == JsonValueKind.String &&
         !string.IsNullOrWhiteSpace(suggestion.GetString())) ||
        envelope.TryGetProperty("lock", out _);

    private static bool V2RecoverableSuggestion(JsonElement result) =>
        OptionalObject(result, "error") is { } error &&
        error.TryGetProperty("suggestedActions", out var actions) &&
        actions.ValueKind == JsonValueKind.Array &&
        actions.GetArrayLength() > 0;

    private static string TargetIdentityOf(JsonElement? request) =>
        request is { } element &&
        element.TryGetProperty("target", out var target) &&
        target.ValueKind == JsonValueKind.Object &&
        target.TryGetProperty("generation", out var generation) &&
        generation.ValueKind != JsonValueKind.Null
            ? "strong"
            : Absent;

    private static JsonElement? OptionalRequest(
        JsonElement? legacy,
        JsonElement? request) =>
        request ?? legacy;

    private static bool Flag(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static string DeclaredFlag(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() ? "true" : "false"
            : Absent;

    private static string DeclaredString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? Absent
            : Absent;

    private static JsonElement? OptionalObject(
        JsonElement element,
        string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : null;
}
