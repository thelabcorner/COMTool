using System.Runtime.InteropServices;
using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;

namespace ComTool.Hosts.Illustrator.Tests;

public sealed class IllustratorSessionSnapshotTests
{
    [Fact]
    public void CollectionCountNormalizesArraysAndCountBearingObjects()
    {
        Assert.Equal(
            3,
            IllustratorSession.ReadCollectionCountValue(
                new object[] { 1, 2, 3 }));

        Assert.Equal(
            2,
            IllustratorSession.ReadCollectionCountValue(
                new List<int> { 1, 2 }));

        Assert.Null(
            IllustratorSession.ReadCollectionCountValue(null));
    }

    [Theory]
    [InlineData("core.target.status")]
    [InlineData("core.target.snapshot")]
    public async Task ApplicationDocumentsMayMarshalAsSystemArray(
        string operation)
    {
        var identity = Identity();
        var app = new SnapshotArrayAppFixture();

        await using var session = new IllustratorSession(
            identity,
            app);

        using var input = JsonDocument.Parse("{}");
        var result = await session.ExecuteAsync(
            new OperationRequest
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = "array-documents-snapshot",
                Target = new TargetRef(
                    identity.Host,
                    identity.TargetId,
                    Generation: 0),
                Operation = operation,
                Input = input.RootElement.Clone()
            });

        Assert.True(
            result.Ok,
            result.Error?.Message ?? "operation failed");
        Assert.Equal(
            OperationStatus.Completed,
            result.Status);

        var value = Assert.IsType<JsonElement>(
            result.Result?.Value);
        Assert.Equal(
            0,
            value.GetProperty("documentsCount").GetInt32());

        if (operation == "core.target.snapshot")
        {
            Assert.Equal(
                JsonValueKind.Null,
                value.GetProperty("activeDocument").ValueKind);
            Assert.Equal(
                0,
                value.GetProperty("selection").GetArrayLength());
        }
    }

    [Fact]
    public async Task SnapshotNormalizesArrayCollectionsAndNullSelection()
    {
        var identity = Identity();
        var app = new SnapshotArrayActiveAppFixture();

        await using var session = new IllustratorSession(
            identity,
            app);

        using var input = JsonDocument.Parse("{}");
        var result = await session.ExecuteAsync(
            new OperationRequest
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = "array-active-document-snapshot",
                Target = new TargetRef(
                    identity.Host,
                    identity.TargetId,
                    Generation: 0),
                Operation = "core.target.snapshot",
                Input = input.RootElement.Clone()
            });

        Assert.True(
            result.Ok,
            result.Error?.Message ?? "operation failed");

        var value = Assert.IsType<JsonElement>(
            result.Result?.Value);
        Assert.Equal(1, value.GetProperty("documentsCount").GetInt32());
        Assert.Equal(0, value.GetProperty("selection").GetArrayLength());

        var document = value.GetProperty("activeDocument");
        Assert.Equal(2, document.GetProperty("layersCount").GetInt32());
        Assert.Equal(2, document.GetProperty("artboardsCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, document.GetProperty("activeArtboardIndex").ValueKind);
        Assert.Equal(3, document.GetProperty("pageItemsCount").GetInt32());
        Assert.Equal(1, document.GetProperty("pathItemsCount").GetInt32());
        Assert.Equal(0, document.GetProperty("selectionCount").GetInt32());
    }

    [Fact]
    public async Task StatusFailsTruthfullyWhenRequiredReadIsBusy()
    {
        var identity = Identity();
        await using var session = new IllustratorSession(
            identity,
            new BusyStatusAppFixture());

        using var input = JsonDocument.Parse("{}");
        var result = await session.ExecuteAsync(
            new OperationRequest
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = "status-required-read-busy",
                Target = new TargetRef(
                    identity.Host,
                    identity.TargetId,
                    Generation: 0),
                Operation = "core.target.status",
                Input = input.RootElement.Clone(),
                Policy = new OperationPolicy(RetryBudgetMs: 0)
            });

        Assert.False(result.Ok);
        Assert.Equal(OperationStatus.HostBusy, result.Status);
        Assert.Equal(TargetState.Busy, result.TargetState);
        Assert.Equal("host_busy", result.Error?.Kind);
        Assert.True(result.Error?.Retryable);
        Assert.Equal(
            ExecutionState.NotStarted,
            result.Error?.Execution);
        Assert.Equal(
            unchecked((int)0x80010001),
            result.Error?.HResult);
    }

    private static HostTargetIdentity Identity() =>
        new()
        {
            Host = IllustratorAdapter.HostName,
            ProcessId = 1234,
            ProcessStartedAt =
                DateTimeOffset.Parse("2026-09-26T19:00:00Z"),
            ExecutablePath = @"C:\Adobe\Illustrator.exe",
            HostVersion = "30.6.0",
            AdapterVersion = IllustratorAdapter.CurrentAdapterVersion,
            EndpointIdentity = IllustratorComInterop.ProgId
        };
}

public sealed class SnapshotArrayActiveAppFixture
{
    public SnapshotArrayDocumentFixture ActiveDocument { get; } = new();
    public object[] Documents => [new object()];
    public string Name => "Adobe Illustrator";
    public string Version => "30.6.0";
    public string BuildNumber => "test";
    public string ScriptingVersion => "4.5.6";
    public string Locale => "en_US";
    public bool ActionIsRunning => false;
    public int UserInteractionLevel => 0;
    public int CoordinateSystem => 0;
    public int FreeMemory => 1024;
}

public sealed class SnapshotArrayDocumentFixture
{
    public string Name => "Array Fixture.ai";
    public object[] Layers => [new object(), new object()];
    public object[] Artboards => [new object(), new object()];
    public object[] PageItems => [new object(), new object(), new object()];
    public object[] PathItems => [new object()];
    public object? Selection => null;
}

public sealed class SnapshotArrayAppFixture
{
    public string Name => "Adobe Illustrator";
    public string Version => "30.6.0";
    public string BuildNumber => "test";
    public string ScriptingVersion => "4.5.6";
    public string Locale => "en_US";
    public bool ActionIsRunning => false;
    public int UserInteractionLevel => 0;
    public int CoordinateSystem => 0;
    public int FreeMemory => 1024;
    public object[] Documents => Array.Empty<object>();
}

public sealed class BusyStatusAppFixture
{
    public string Name =>
        throw new COMException(
            "Synthetic RPC_E_CALL_REJECTED.",
            unchecked((int)0x80010001));
}
