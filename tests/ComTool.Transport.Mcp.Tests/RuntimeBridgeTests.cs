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
        Assert.False(result.Error?.Retryable);
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
}
