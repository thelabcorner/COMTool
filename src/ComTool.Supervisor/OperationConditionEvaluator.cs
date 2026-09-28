using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;
using ComTool.Runtime;

namespace ComTool.Supervisor;

internal static class OperationConditionEvaluator
{
    public static void ValidateSources(
        IReadOnlyList<OperationCondition>? conditions,
        HostTargetDescriptor target,
        string phase)
    {
        if (conditions is null || conditions.Count == 0)
            return;

        foreach (var condition in conditions)
        {
            if (!BuiltInOperations.Catalog.TryGet(
                    condition.Source.Operation,
                    out var definition))
            {
                throw new OperationConditionPolicyException(
                    "condition_source_unsupported",
                    $"{phase} assertion '{condition.Id}' references unregistered operation '{condition.Source.Operation}'.");
            }

            if (definition.Scope != OperationExecutionScope.Host ||
                !definition.RequiresTarget ||
                definition.MutationResolution !=
                    MutationResolutionMode.Fixed ||
                definition.MutationClass !=
                    MutationClass.ReadOnly ||
                definition.RequiresLease)
            {
                throw new OperationConditionPolicyException(
                    "condition_source_not_read_only",
                    $"{phase} assertion '{condition.Id}' source '{condition.Source.Operation}' is not a fixed read-only host operation.");
            }

            if (definition.Host is not null &&
                !string.Equals(
                    definition.Host,
                    target.Identity.Host,
                    StringComparison.Ordinal))
            {
                throw new OperationConditionPolicyException(
                    "condition_source_host_mismatch",
                    $"{phase} assertion '{condition.Id}' source '{condition.Source.Operation}' is registered for host '{definition.Host}', not '{target.Identity.Host}'.");
            }

            if (!target.Capabilities.Any(
                    capability =>
                        capability.Supported &&
                        string.Equals(
                            capability.Name,
                            condition.Source.Operation,
                            StringComparison.Ordinal)))
            {
                throw new OperationConditionPolicyException(
                    "condition_source_not_advertised",
                    $"{phase} assertion '{condition.Id}' source '{condition.Source.Operation}' is not advertised by target '{target.Identity.TargetId}'.");
            }
        }
    }

    public static async Task<ConditionBatchResult> EvaluateAsync(
        IReadOnlyList<OperationCondition>? conditions,
        string phase,
        OperationRequest outerRequest,
        HostTargetDescriptor target,
        WorkerBrokerClient worker,
        TimeSpan? watchdog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outerRequest);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(worker);

        var clock = Stopwatch.StartNew();

        if (conditions is null || conditions.Count == 0)
        {
            return new ConditionBatchResult(
                Passed: true,
                Verified: true,
                FailureKind: null,
                FailureMessage: null,
                SourceFailure: null,
                Evidence: null,
                VerifyMs: 0);
        }

        var observations =
            new List<ConditionObservation>(
                conditions.Count);

        OperationResult? sourceFailure = null;
        string? failureKind = null;
        string? failureMessage = null;
        var allPassed = true;
        var allVerified = true;

        for (var index = 0;
             index < conditions.Count;
             index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var condition = conditions[index];
            var sourceRequest = new OperationRequest
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = CreateConditionRequestId(
                    outerRequest.Id,
                    phase,
                    index,
                    condition.Id),
                Target = target.Target,
                Operation = condition.Source.Operation,
                Input = condition.Source.Input.Clone()
            };

            var sourceResult = await worker
                .ExecuteAsync(
                    sourceRequest,
                    watchdog,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!sourceResult.Ok ||
                sourceResult.Result is null)
            {
                allPassed = false;
                allVerified = false;
                sourceFailure = sourceResult;
                failureKind = "condition_source_failed";
                failureMessage =
                    $"{phase} assertion '{condition.Id}' could not be verified because source operation '{condition.Source.Operation}' failed.";

                observations.Add(
                    CreateObservation(
                        condition,
                        verified: false,
                        passed: false,
                        actual: sourceResult.Result,
                        sourceResult));

                break;
            }

            var passed = EvaluatePredicate(
                condition.Predicate,
                sourceResult.Result);

            observations.Add(
                CreateObservation(
                    condition,
                    verified: true,
                    passed,
                    sourceResult.Result,
                    sourceResult));

            if (!passed)
            {
                allPassed = false;
                failureKind = "condition_predicate_failed";
                failureMessage =
                    $"{phase} assertion '{condition.Id}' predicate '{condition.Predicate.Kind}' evaluated false.";
                break;
            }
        }

        var evidenceValue =
            JsonSerializer.SerializeToElement(new
            {
                phase,
                passed = allPassed,
                verified = allVerified,
                evaluated = observations.Count,
                total = conditions.Count,
                assertions = observations
            });

        return new ConditionBatchResult(
            allPassed,
            allVerified,
            failureKind,
            failureMessage,
            sourceFailure,
            new EvidenceItem(
                phase == "precondition"
                    ? "conditions.pre"
                    : "conditions.post",
                evidenceValue),
            clock.Elapsed.TotalMilliseconds);
    }

    internal static bool EvaluatePredicate(
        OperationConditionPredicate predicate,
        ProtocolValue actual)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(actual);

        return predicate.Kind switch
        {
            "equals" =>
                ValuesEqual(
                    actual,
                    predicate.Expected
                    ?? throw new InvalidOperationException(
                        "equals predicate is missing expected.")),

            "not_equals" =>
                !ValuesEqual(
                    actual,
                    predicate.Expected
                    ?? throw new InvalidOperationException(
                        "not_equals predicate is missing expected.")),

            "truthy" => IsTruthy(actual),
            "falsey" => !IsTruthy(actual),
            "is_null" =>
                string.Equals(
                    actual.Kind,
                    "null",
                    StringComparison.Ordinal),
            "not_null" =>
                !string.Equals(
                    actual.Kind,
                    "null",
                    StringComparison.Ordinal),

            _ => throw new InvalidOperationException(
                $"Unsupported condition predicate '{predicate.Kind}'.")
        };
    }

    // Shared with the watch runtime so JSON-type-exact equality has exactly
    // one implementation: a boolean must never match a number.
    internal static bool ValuesEqual(
        ProtocolValue actual,
        ProtocolValue expected)
    {
        if (!string.Equals(
                actual.Kind,
                expected.Kind,
                StringComparison.Ordinal))
            return false;

        if (string.Equals(
                actual.Kind,
                "null",
                StringComparison.Ordinal))
            return true;

        if (actual.Value is not { } actualValue ||
            expected.Value is not { } expectedValue)
            return false;

        return JsonElement.DeepEquals(
            actualValue,
            expectedValue);
    }

    private static bool IsTruthy(
        ProtocolValue value) =>
        value.Kind switch
        {
            "null" => false,
            "boolean" =>
                value.Value is { } booleanValue &&
                booleanValue.ValueKind ==
                    JsonValueKind.True,
            "number" =>
                value.Value is { } numberValue &&
                numberValue.GetDouble() != 0d,
            "string" =>
                value.Value is { } stringValue &&
                !string.IsNullOrEmpty(
                    stringValue.GetString()),
            "array" or "object" => true,
            _ => false
        };

    private static ConditionObservation CreateObservation(
        OperationCondition condition,
        bool verified,
        bool passed,
        ProtocolValue? actual,
        OperationResult sourceResult) =>
        new(
            condition.Id,
            condition.Source.Operation,
            condition.Predicate.Kind,
            verified,
            passed,
            condition.Predicate.Expected,
            actual,
            sourceResult.Status.ToString(),
            sourceResult.TargetState.ToString(),
            sourceResult.Error?.Kind,
            sourceResult.Error?.Message,
            sourceResult.Error?.Execution.ToString(),
            sourceResult.Timing);

    private static string CreateConditionRequestId(
        string outerRequestId,
        string phase,
        int index,
        string conditionId)
    {
        var material =
            outerRequestId + "\n" +
            phase + "\n" +
            index.ToString(
                System.Globalization.CultureInfo.InvariantCulture) +
            "\n" +
            conditionId;

        var hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(material));

        return "cond-" +
               Convert.ToHexString(
                       hash.AsSpan(0, 20))
                   .ToLowerInvariant();
    }
}

internal sealed record ConditionBatchResult(
    bool Passed,
    bool Verified,
    string? FailureKind,
    string? FailureMessage,
    OperationResult? SourceFailure,
    EvidenceItem? Evidence,
    double VerifyMs);

internal sealed record ConditionObservation(
    string Id,
    string SourceOperation,
    string Predicate,
    bool Verified,
    bool Passed,
    ProtocolValue? Expected,
    ProtocolValue? Actual,
    string SourceStatus,
    string SourceTargetState,
    string? SourceErrorKind,
    string? SourceErrorMessage,
    string? SourceExecution,
    OperationTiming? SourceTiming);

internal sealed class OperationConditionPolicyException(
    string kind,
    string message)
    : Exception(message)
{
    public string Kind { get; } = kind;
}
