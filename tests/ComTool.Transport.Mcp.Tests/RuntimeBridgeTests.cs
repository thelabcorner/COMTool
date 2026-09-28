using System.Text.Json;
using ComTool.Protocol;

namespace ComTool.Transport.Mcp.Tests;

public sealed class RuntimeBridgeTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task InvalidJsonInputReturnsCanonicalValidationEnvelope()
    {
        var bridge = new RuntimeBridge();

        var json = await bridge.ExecuteAsync(
            "core.runtime.health",
            "{not-json}",
            "mcp-invalid-json",
            leaseId: null,
            targetHost: null,
            targetId: null,
            None);

        var result = ProtocolJson.DeserializeResult(json);

        Assert.False(result.Ok);
        Assert.Equal("mcp-invalid-json", result.Id);
        Assert.Equal("core.runtime.health", result.Operation);
        Assert.Equal(OperationStatus.InvalidRequest, result.Status);
        Assert.Equal("invalid_json", result.Error?.Kind);
        Assert.Equal(ExecutionState.NotStarted, result.Error?.Execution);
    }

    [Fact]
    public async Task EmptyOperationReturnsCanonicalValidationEnvelope()
    {
        var bridge = new RuntimeBridge();

        var json = await bridge.ExecuteAsync(
            "   ",
            "null",
            "mcp-empty-op",
            leaseId: null,
            targetHost: null,
            targetId: null,
            None);

        var result = ProtocolJson.DeserializeResult(json);

        Assert.False(result.Ok);
        Assert.Equal("mcp-empty-op", result.Id);
        Assert.Equal(OperationStatus.InvalidRequest, result.Status);
        Assert.Equal("invalid_operation", result.Error?.Kind);
    }

    [Fact]
    public async Task UnreachableRuntimeReturnsStructuredRuntimeUnreachableError()
    {
        var bridge = new RuntimeBridge();
        var isolatedPipe = $"comtool-v2-mcp-test-{Guid.NewGuid():N}";

        var json = await bridge.ExecuteAsync(
            "core.runtime.health",
            "null",
            "mcp-unreachable",
            leaseId: null,
            targetHost: null,
            targetId: null,
            None,
            pipeName: isolatedPipe);

        var result = ProtocolJson.DeserializeResult(json);

        Assert.False(result.Ok);
        Assert.Equal("mcp-unreachable", result.Id);
        Assert.Equal("core.runtime.health", result.Operation);
        Assert.Equal("runtime_unreachable", result.Error?.Kind);
        Assert.True(result.Error?.Retryable);
        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(TargetState.Unavailable, result.TargetState);
        Assert.Equal(ExecutionState.NotStarted, result.Error?.Execution);
    }

    [Fact]
    public async Task CallerCancellationReturnsStructuredTransportCancelledError()
    {
        var bridge = new RuntimeBridge();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var json = await bridge.ExecuteAsync(
            "core.runtime.health",
            "null",
            "mcp-cancelled",
            leaseId: null,
            targetHost: null,
            targetId: null,
            cancellation.Token,
            pipeName: $"comtool-v2-mcp-test-{Guid.NewGuid():N}");

        var result = ProtocolJson.DeserializeResult(json);

        Assert.False(result.Ok);
        Assert.Equal("runtime_transport_cancelled", result.Error?.Kind);
        Assert.True(result.Error?.Retryable);
        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(TargetState.Unavailable, result.TargetState);
        Assert.Equal(ExecutionState.NotStarted, result.Error?.Execution);
    }

    [Fact]
    public async Task InvalidLeaseIdIsRejectedBeforeTransport()
    {
        var bridge = new RuntimeBridge();

        var json = await bridge.ExecuteAsync(
            "script.eval",
            "null",
            "mcp-bad-lease",
            leaseId: "too-short",
            targetHost: "illustrator",
            targetId: "illustrator:abc",
            None,
            pipeName: $"comtool-v2-mcp-test-{Guid.NewGuid():N}");

        var result = ProtocolJson.DeserializeResult(json);

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.InvalidRequest, result.Status);
        Assert.Equal("invalid_lease_id", result.Error?.Kind);
    }

    [Fact]
    public async Task InvalidRetryBudgetIsRejectedBeforeTransport()
    {
        var bridge = new RuntimeBridge();

        var json = await bridge.ExecuteAsync(
            "script.eval",
            "null",
            "mcp-bad-retry-budget",
            leaseId:
                "lease_abcdefghijklmnopqrstuvwxyz123456",
            targetHost: "illustrator",
            targetId: "illustrator:abc",
            None,
            retryBudgetMs: -1,
            pipeName:
                $"comtool-v2-mcp-test-{Guid.NewGuid():N}");

        var result = ProtocolJson.DeserializeResult(json);

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.InvalidRequest, result.Status);
        Assert.Equal(
            "invalid_retry_budget",
            result.Error?.Kind);
        Assert.Equal(
            ExecutionState.NotStarted,
            result.Error?.Execution);
    }

    [Fact]
    public async Task GeneratedRequestIdIsStableWithinSingleCall()
    {
        var bridge = new RuntimeBridge();

        var json = await bridge.ExecuteAsync(
            "core.runtime.health",
            "null",
            requestId: null,
            leaseId: null,
            targetHost: null,
            targetId: null,
            None,
            pipeName: $"comtool-v2-mcp-test-{Guid.NewGuid():N}");

        using var document = JsonDocument.Parse(json);
        var id = document.RootElement.GetProperty("id").GetString();

        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.StartsWith("mcp-", id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConditionJsonUsesCanonicalProtocolValidationBeforeTransport()
    {
        var bridge = new RuntimeBridge();
        const string invalidPreconditions =
            """
            [
              {
                "id":"document-open",
                "source":{"operation":"com.get","input":{"path":"ActiveDocument.Name"}},
                "predicate":{"kind":"unsupported"}
              }
            ]
            """;

        var json = await bridge.ExecuteAsync(
            "script.eval",
            """{"kind":"expression","source":"1","effects":"read_only"}""",
            "mcp-condition-validation",
            leaseId: null,
            targetHost: "illustrator",
            targetId: "illustrator:test",
            None,
            preconditionsJson: invalidPreconditions,
            pipeName: $"comtool-v2-mcp-test-{Guid.NewGuid():N}");

        var result = ProtocolJson.DeserializeResult(json);

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.InvalidRequest, result.Status);
        Assert.Equal(
            "invalid_condition_predicate",
            result.Error?.Kind);
        Assert.Equal(
            ExecutionState.NotStarted,
            result.Error?.Execution);
    }

    [Fact]
    public async Task MalformedConditionJsonIsRejectedBeforeTransport()
    {
        var bridge = new RuntimeBridge();

        var json = await bridge.ExecuteAsync(
            "script.eval",
            "null",
            "mcp-condition-json",
            leaseId: null,
            targetHost: "illustrator",
            targetId: "illustrator:test",
            None,
            postconditionsJson: "{not-an-array}",
            pipeName: $"comtool-v2-mcp-test-{Guid.NewGuid():N}");

        var result = ProtocolJson.DeserializeResult(json);

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.InvalidRequest, result.Status);
        Assert.Equal("invalid_json", result.Error?.Kind);
        Assert.Contains(
            "postconditionsJson",
            result.Error?.Message ?? string.Empty,
            StringComparison.Ordinal);
    }
}
