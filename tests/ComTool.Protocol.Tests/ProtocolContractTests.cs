using System.Text.Json;
using ComTool.Protocol;

namespace ComTool.Protocol.Tests;

public sealed class ProtocolContractTests
{
    [Theory]
    [InlineData("true")]
    [InlineData("123")]
    [InlineData("null")]
    [InlineData("{\"x\":1}")]
    [InlineData("[1,2,3]")]
    public void JsonLookingStringsRemainStrings(string input)
    {
        var value = ProtocolValue.FromString(input);

        Assert.Equal("string", value.Kind);
        Assert.True(value.Value.HasValue);
        Assert.Equal(JsonValueKind.String, value.Value.Value.ValueKind);
        Assert.Equal(input, value.Value.Value.GetString());
    }

    [Fact]
    public void UnknownRequestFieldsAreRejected()
    {
        const string json =
            """
            {
              "protocolVersion": 1,
              "id": "req-1",
              "operation": "core.status",
              "input": null,
              "surprise": true
            }
            """;

        Assert.Throws<JsonException>(() => ProtocolJson.DeserializeRequest(json));
    }

    [Fact]
    public void UnsupportedProtocolVersionIsRejectedBeforeDispatch()
    {
        const string json =
            """
            {
              "protocolVersion": 2,
              "id": "req-1",
              "operation": "core.status",
              "input": null
            }
            """;

        var error = Assert.Throws<ProtocolValidationException>(
            () => ProtocolJson.DeserializeRequest(json));

        Assert.Equal("unsupported_protocol_version", error.Kind);
    }

    [Theory]
    [InlineData("queueTimeoutMs")]
    [InlineData("operationSoftTimeoutMs")]
    [InlineData("allowReplayAfterAmbiguous")]
    public void UnimplementedStablePolicyFieldsAreRejected(string fieldName)
    {
        var json = $$"""
            {
              "protocolVersion": 1,
              "id": "req-1",
              "operation": "core.status",
              "input": null,
              "policy": {
                "{{fieldName}}": 1
              }
            }
            """;

        Assert.Throws<JsonException>(
            () => ProtocolJson.DeserializeRequest(json));
    }

    [Theory]
    [InlineData(99)]
    [InlineData(3_600_001)]
    public void WorkerWatchdogOutsideSupportedBoundsIsRejected(
        int watchdogMs)
    {
        var json = $$"""
            {
              "protocolVersion": 1,
              "id": "req-watchdog",
              "operation": "script.eval",
              "input": {},
              "policy": {
                "workerWatchdogMs": {{watchdogMs}}
              }
            }
            """;

        var error = Assert.Throws<ProtocolValidationException>(
            () => ProtocolJson.DeserializeRequest(json));

        Assert.Equal("invalid_timeout", error.Kind);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(120_000)]
    [InlineData(3_600_000)]
    public void WorkerWatchdogAcceptsCallerSuppliedDuration(
        int watchdogMs)
    {
        var json = $$"""
            {
              "protocolVersion": 1,
              "id": "req-watchdog",
              "operation": "script.eval",
              "input": {},
              "policy": {
                "workerWatchdogMs": {{watchdogMs}}
              }
            }
            """;

        var request = ProtocolJson.DeserializeRequest(json);

        Assert.Equal(
            watchdogMs,
            request.Policy?.WorkerWatchdogMs);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3_600_001)]
    public void RetryBudgetOutsideSupportedBoundsIsRejected(
        int retryBudgetMs)
    {
        var json = $$"""
            {
              "protocolVersion": 1,
              "id": "req-retry-budget",
              "operation": "script.eval",
              "input": {},
              "policy": {
                "retryBudgetMs": {{retryBudgetMs}}
              }
            }
            """;

        var error = Assert.Throws<ProtocolValidationException>(
            () => ProtocolJson.DeserializeRequest(json));

        Assert.Equal("invalid_retry_budget", error.Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2_000)]
    [InlineData(3_600_000)]
    public void RetryBudgetAcceptsCallerSuppliedDuration(
        int retryBudgetMs)
    {
        var json = $$"""
            {
              "protocolVersion": 1,
              "id": "req-retry-budget",
              "operation": "script.eval",
              "input": {},
              "policy": {
                "retryBudgetMs": {{retryBudgetMs}}
              }
            }
            """;

        var request = ProtocolJson.DeserializeRequest(json);

        Assert.Equal(
            retryBudgetMs,
            request.Policy?.RetryBudgetMs);
        Assert.Equal(
            retryBudgetMs,
            ProtocolJson.DeserializeRequest(
                ProtocolJson.Serialize(request))
                .Policy?.RetryBudgetMs);
    }

    [Fact]
    public void RequestRoundTripPreservesTargetAndConditions()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "protocolVersion": 1,
              "id": "req-42",
              "target": {
                "host": "illustrator",
                "id": "illustrator:test",
                "generation": 7
              },
              "operation": "illustrator.com.get",
              "input": {
                "path": "Version"
              },
              "policy": {
                "workerWatchdogMs": 5000,
                "leaseId": "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF"
              },
              "preconditions": [
                {
                  "id": "version-match",
                  "source": {
                    "operation": "com.get",
                    "input": {
                      "path": "Version"
                    }
                  },
                  "predicate": {
                    "kind": "equals",
                    "expected": {
                      "kind": "string",
                      "value": "30.6.0"
                    }
                  }
                }
              ]
            }
            """);

        var request = ProtocolJson.DeserializeRequest(
            document.RootElement.GetRawText());

        var roundTrip = ProtocolJson.DeserializeRequest(
            ProtocolJson.Serialize(request));

        Assert.Equal("req-42", roundTrip.Id);
        Assert.Equal("illustrator", roundTrip.Target?.Host);
        Assert.Equal(7, roundTrip.Target?.Generation);
        Assert.Equal("illustrator.com.get", roundTrip.Operation);
        Assert.Equal("Version", roundTrip.Input.GetProperty("path").GetString());
        Assert.Equal(
            "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
            roundTrip.Policy?.LeaseId);
        var condition = Assert.Single(
            roundTrip.Preconditions!);
        Assert.Equal(
            "version-match",
            condition.Id);
        Assert.Equal(
            "com.get",
            condition.Source.Operation);
        Assert.Equal(
            "equals",
            condition.Predicate.Kind);
        Assert.Equal(
            "string",
            condition.Predicate.Expected?.Kind);
    }

    [Fact]
    public void EqualsConditionRequiresExpectedValue()
    {
        const string json =
            """
            {
              "protocolVersion": 1,
              "id": "req-condition",
              "operation": "script.eval",
              "input": {},
              "preconditions": [
                {
                  "id": "guard",
                  "source": {
                    "operation": "com.get",
                    "input": {"path": "Version"}
                  },
                  "predicate": {"kind": "equals"}
                }
              ]
            }
            """;

        var error = Assert.Throws<ProtocolValidationException>(
            () => ProtocolJson.DeserializeRequest(json));

        Assert.Equal(
            "invalid_condition_predicate",
            error.Kind);
    }

    [Fact]
    public void NonComparativeConditionRejectsExpectedValue()
    {
        const string json =
            """
            {
              "protocolVersion": 1,
              "id": "req-condition",
              "operation": "script.eval",
              "input": {},
              "preconditions": [
                {
                  "id": "guard",
                  "source": {
                    "operation": "com.get",
                    "input": {"path": "Version"}
                  },
                  "predicate": {
                    "kind": "truthy",
                    "expected": {
                      "kind": "boolean",
                      "value": true
                    }
                  }
                }
              ]
            }
            """;

        var error = Assert.Throws<ProtocolValidationException>(
            () => ProtocolJson.DeserializeRequest(json));

        Assert.Equal(
            "invalid_condition_predicate",
            error.Kind);
    }

    [Fact]
    public void DuplicateConditionIdsAreRejected()
    {
        const string json =
            """
            {
              "protocolVersion": 1,
              "id": "req-condition",
              "operation": "script.eval",
              "input": {},
              "preconditions": [
                {
                  "id": "same",
                  "source": {
                    "operation": "com.get",
                    "input": {"path": "Version"}
                  },
                  "predicate": {"kind": "truthy"}
                },
                {
                  "id": "same",
                  "source": {
                    "operation": "com.get",
                    "input": {"path": "Documents.Count"}
                  },
                  "predicate": {"kind": "truthy"}
                }
              ]
            }
            """;

        var error = Assert.Throws<ProtocolValidationException>(
            () => ProtocolJson.DeserializeRequest(json));

        Assert.Equal(
            "duplicate_condition_id",
            error.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF!")]
    public void InvalidLeaseIdIsRejectedBeforeDispatch(string leaseId)
    {
        var json = $$"""
            {
              "protocolVersion": 1,
              "id": "req-lease",
              "operation": "core.target.status",
              "input": null,
              "policy": {
                "leaseId": "{{leaseId}}"
              }
            }
            """;

        var error = Assert.Throws<ProtocolValidationException>(
            () => ProtocolJson.DeserializeRequest(json));

        Assert.Equal("invalid_lease_id", error.Kind);
    }

    [Fact]
    public void ResultUsesStableWireEnumNames()
    {
        var result = new OperationResult
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = "req-1",
            Operation = "core.status",
            Ok = false,
            Status = OperationStatus.ReconciliationRequired,
            TargetState = TargetState.ReconciliationRequired,
            Error = new ProtocolError
            {
                Kind = "ambiguous_execution",
                Message = "Worker exited while mutation state was unknown.",
                Retryable = false,
                Execution = ExecutionState.Ambiguous
            }
        };

        var json = ProtocolJson.Serialize(result);

        Assert.Contains("\"status\":\"reconciliation_required\"", json, StringComparison.Ordinal);
        Assert.Contains("\"targetState\":\"reconciliation_required\"", json, StringComparison.Ordinal);
        Assert.Contains("\"execution\":\"ambiguous\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DistinctJsonTypesRemainDistinct()
    {
        var cases = new Dictionary<string, string>
        {
            ["null"] = "null",
            ["string"] = "\"1\"",
            ["number"] = "1",
            ["boolean"] = "true",
            ["array"] = "[1]",
            ["object"] = "{\"x\":1}"
        };

        foreach (var (expectedKind, json) in cases)
        {
            using var document = JsonDocument.Parse(json);
            var value = ProtocolValue.From(document.RootElement);
            Assert.Equal(expectedKind, value.Kind);
        }
    }
}
