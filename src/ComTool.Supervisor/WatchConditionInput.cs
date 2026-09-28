using System.Text.Json;
using ComTool.Protocol;

namespace ComTool.Supervisor;

/// <summary>
/// Strict parser for the watch.condition input. Unknown fields and duplicate
/// fields are rejected rather than ignored, and the watched source reuses
/// <see cref="OperationCondition"/> so the runtime catalog stays the only
/// authority on what a watch may compose.
/// </summary>
internal static class WatchConditionInput
{
    private static readonly string[] EqualityPredicates =
        ["equals", "not_equals"];

    private static readonly string[] ChangePredicates =
        ["changed", "unchanged"];

    private static readonly string[] ValueOnlyPredicates =
        ["truthy", "falsey", "is_null", "not_null"];

    public static bool TryRead(
        JsonElement input,
        int? policyWorkerWatchdogMs,
        out WatchPlan? plan,
        out string? error)
    {
        plan = null;
        error = null;

        if (input.ValueKind != JsonValueKind.Object)
        {
            error =
                "watch.condition input must be a JSON object with source, predicate, and optional timing fields.";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        OperationConditionSource? source = null;
        string? predicateKind = null;
        JsonElement? predicateExpected = null;
        var predicateExpectedPresent = false;
        var predicateSeen = false;
        int? pollIntervalMs = null;
        int? timeoutMs = null;
        int? pollTimeoutMs = null;

        foreach (var property in input.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                error =
                    $"Duplicate watch.condition input field '{property.Name}'.";
                return false;
            }

            switch (property.Name)
            {
                case "source":
                    if (!TryReadSource(
                            property.Value,
                            out source,
                            out error))
                    {
                        return false;
                    }

                    break;

                case "predicate":
                    if (!TryReadPredicate(
                            property.Value,
                            out predicateKind,
                            out predicateExpected,
                            out predicateExpectedPresent,
                            out error))
                    {
                        return false;
                    }

                    predicateSeen = true;
                    break;

                case "pollIntervalMs":
                    if (!TryReadBoundedInt(
                            property.Value,
                            WatchConditionPoller.MinPollIntervalMs,
                            WatchConditionPoller.MaxPollIntervalMs,
                            "pollIntervalMs",
                            out pollIntervalMs,
                            out error))
                    {
                        return false;
                    }

                    break;

                case "timeoutMs":
                    if (!TryReadBoundedInt(
                            property.Value,
                            WatchConditionPoller.MinTimeoutMs,
                            WatchConditionPoller.MaxTimeoutMs,
                            "timeoutMs",
                            out timeoutMs,
                            out error))
                    {
                        return false;
                    }

                    break;

                case "pollTimeoutMs":
                    if (!TryReadBoundedInt(
                            property.Value,
                            WatchConditionPoller.MinPollTimeoutMs,
                            WatchConditionPoller.MaxPollTimeoutMs,
                            "pollTimeoutMs",
                            out pollTimeoutMs,
                            out error))
                    {
                        return false;
                    }

                    break;

                default:
                    error =
                        $"Unknown watch.condition input field '{property.Name}'.";
                    return false;
            }
        }

        if (source is null)
        {
            error =
                "watch.condition requires input.source with an operation to poll.";
            return false;
        }

        if (!predicateSeen || predicateKind is null)
        {
            // No hidden truthiness routing: the condition is always explicit.
            error =
                "watch.condition requires input.predicate.kind; use 'truthy' explicitly instead of relying on a default.";
            return false;
        }

        var effectiveTimeoutMs =
            timeoutMs ?? WatchConditionPoller.DefaultTimeoutMs;
        var effectiveIntervalMs =
            pollIntervalMs ?? WatchConditionPoller.DefaultPollIntervalMs;

        // A caller-supplied worker watchdog caps each poll. It is never allowed
        // to exceed the explicit pollTimeoutMs.
        var effectivePollTimeoutMs =
            pollTimeoutMs ??
            policyWorkerWatchdogMs ??
            Math.Min(
                WatchConditionPoller.DefaultPollTimeoutMs,
                effectiveTimeoutMs);

        if (effectivePollTimeoutMs is <
                WatchConditionPoller.MinPollTimeoutMs or
            > WatchConditionPoller.MaxPollTimeoutMs)
        {
            error =
                $"'pollTimeoutMs' must be between {WatchConditionPoller.MinPollTimeoutMs} and {WatchConditionPoller.MaxPollTimeoutMs} ms.";
            return false;
        }

        plan = new WatchPlan(
            new OperationCondition
            {
                Id = "watch.source",
                Source = source,
                Predicate = new OperationConditionPredicate
                {
                    Kind = predicateKind,
                    Expected = predicateExpectedPresent
                        ? ProtocolValue.From(predicateExpected!.Value)
                        : null
                }
            },
            predicateKind,
            predicateExpectedPresent
                ? ProtocolValue.From(predicateExpected!.Value)
                : null,
            TimeSpan.FromMilliseconds(effectiveIntervalMs),
            TimeSpan.FromMilliseconds(effectiveTimeoutMs),
            TimeSpan.FromMilliseconds(effectivePollTimeoutMs),
            WatchConditionPoller.DeriveMaxPolls(
                TimeSpan.FromMilliseconds(effectiveTimeoutMs),
                TimeSpan.FromMilliseconds(effectiveIntervalMs)));

        return true;
    }

    private static bool TryReadSource(
        JsonElement value,
        out OperationConditionSource? source,
        out string? error)
    {
        source = null;
        error = null;

        if (value.ValueKind != JsonValueKind.Object)
        {
            error = "watch.condition 'source' must be a JSON object.";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? operation = null;
        JsonElement operationInput = JsonSerializer.SerializeToElement(
            (object?)null);

        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                error =
                    $"Duplicate watch.condition source field '{property.Name}'.";
                return false;
            }

            switch (property.Name)
            {
                case "operation":
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        error = "watch.condition source 'operation' must be a string.";
                        return false;
                    }

                    operation = property.Value.GetString();
                    break;

                case "input":
                    if (property.Value.ValueKind == JsonValueKind.Undefined)
                    {
                        error = "watch.condition source 'input' must not be undefined.";
                        return false;
                    }

                    operationInput = property.Value.Clone();
                    break;

                default:
                    error =
                        $"Unknown watch.condition source field '{property.Name}'.";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(operation) || operation.Length > 256)
        {
            error =
                "watch.condition requires source.operation as a non-empty operation name of at most 256 characters.";
            return false;
        }

        source = new OperationConditionSource
        {
            Operation = operation,
            Input = operationInput
        };

        return true;
    }

    private static bool TryReadPredicate(
        JsonElement value,
        out string? kind,
        out JsonElement? expected,
        out bool expectedPresent,
        out string? error)
    {
        kind = null;
        expected = null;
        expectedPresent = false;
        error = null;

        if (value.ValueKind != JsonValueKind.Object)
        {
            error = "watch.condition 'predicate' must be a JSON object.";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                error =
                    $"Duplicate watch.condition predicate field '{property.Name}'.";
                return false;
            }

            switch (property.Name)
            {
                case "kind":
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        error = "watch.condition predicate 'kind' must be a string.";
                        return false;
                    }

                    kind = property.Value.GetString();
                    break;

                case "expected":
                    expected = property.Value.Clone();
                    expectedPresent = true;
                    break;

                default:
                    error =
                        $"Unknown watch.condition predicate field '{property.Name}'.";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(kind))
        {
            error = "watch.condition requires predicate.kind.";
            return false;
        }

        var requiresExpected = Array.IndexOf(
            EqualityPredicates,
            kind) >= 0;
        var forbidsExpected = Array.IndexOf(
            ChangePredicates,
            kind) >= 0 ||
            Array.IndexOf(ValueOnlyPredicates, kind) >= 0;

        if (!requiresExpected && !forbidsExpected)
        {
            error =
                $"Unsupported watch.condition predicate '{kind}'.";
            return false;
        }

        if (requiresExpected && !expectedPresent)
        {
            error =
                $"watch.condition predicate '{kind}' requires 'expected'.";
            return false;
        }

        // 'changed' compares against the first observed sample and 'equals'
        // against a fixed literal; they cannot both drive one watch.
        if (forbidsExpected && expectedPresent)
        {
            error =
                $"watch.condition predicate '{kind}' must not specify 'expected'.";
            return false;
        }

        return true;
    }

    private static bool TryReadBoundedInt(
        JsonElement value,
        int minimum,
        int maximum,
        string field,
        out int? parsed,
        out string? error)
    {
        parsed = null;
        error = null;

        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var number))
        {
            error =
                $"watch.condition '{field}' must be an integer.";
            return false;
        }

        if (number < minimum || number > maximum)
        {
            error =
                $"watch.condition '{field}' must be between {minimum} and {maximum} ms.";
            return false;
        }

        parsed = number;
        return true;
    }
}
