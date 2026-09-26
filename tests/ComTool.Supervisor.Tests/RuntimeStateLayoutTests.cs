using System.Text.Json;
using ComTool.Protocol;
using ComTool.Supervisor;

namespace ComTool.Supervisor.Tests;

public sealed class RuntimeStateLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "comtool-v2-state-layout-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void OpenCreatesVersionedManifestAndCanonicalStorePaths()
    {
        var layout = RuntimeStateLayout.Open(_root);

        Assert.Equal(Path.GetFullPath(_root), layout.Root);
        Assert.Equal(
            Path.Combine(_root, "mutation-ledger"),
            layout.MutationLedgerRoot);
        Assert.Equal(
            Path.Combine(_root, "workflows"),
            layout.WorkflowRoot);
        Assert.True(File.Exists(layout.ManifestPath));

        using var manifest = JsonDocument.Parse(
            File.ReadAllBytes(layout.ManifestPath));
        Assert.Equal(
            "comtool-v2-state",
            manifest.RootElement.GetProperty("format").GetString());
        Assert.Equal(
            RuntimeStateLayout.CurrentSchemaVersion,
            manifest.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void OpenRejectsUnsupportedFutureSchema()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "state-manifest.json"),
            """
            {
              "format": "comtool-v2-state",
              "schemaVersion": 999,
              "createdAt": "2026-09-25T00:00:00Z"
            }
            """);

        var ex = Assert.Throws<RuntimeStateLayoutException>(
            () => RuntimeStateLayout.Open(_root));

        Assert.Equal("runtime_state_schema_mismatch", ex.Kind);
    }

    [Fact]
    public void OpenRejectsPreV1CustomMutationLedgerAtStateRoot()
    {
        Directory.CreateDirectory(Path.Combine(_root, "records"));
        Directory.CreateDirectory(Path.Combine(_root, "active"));

        var ex = Assert.Throws<RuntimeStateLayoutException>(
            () => RuntimeStateLayout.Open(_root));

        Assert.Equal("runtime_state_legacy_layout", ex.Kind);
        Assert.False(
            File.Exists(
                Path.Combine(_root, "state-manifest.json")));
    }

    [Fact]
    public async Task RuntimeCreatesStoresUnderCanonicalStateSubdirectories()
    {
        await using var runtime = new RuntimeSupervisor(
            ["illustrator"],
            new WorkerBrokerOptions
            {
                WorkerExecutablePath =
                    Path.Combine(_root, "unused-worker.exe")
            },
            _root);

        Assert.True(
            Directory.Exists(
                Path.Combine(_root, "mutation-ledger", "records")));
        Assert.True(
            Directory.Exists(
                Path.Combine(_root, "mutation-ledger", "active")));
        Assert.True(
            Directory.Exists(
                Path.Combine(_root, "workflows")));
    }

    [Fact]
    public async Task RuntimeHealthReportsBuildAndStateSchemaVersions()
    {
        await using var runtime = new RuntimeSupervisor(
            ["illustrator"],
            new WorkerBrokerOptions
            {
                WorkerExecutablePath =
                    Path.Combine(_root, "unused-worker.exe")
            },
            _root);

        using var input = JsonDocument.Parse("null");
        var result = await runtime.ExecuteAsync(
            new OperationRequest
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = "health-version-provenance",
                Operation = "core.runtime.health",
                Input = input.RootElement.Clone()
            });

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal("object", result.Result?.Kind);
        Assert.NotNull(result.Result?.Value);

        var payload = result.Result!.Value!.Value;
        Assert.Equal(
            RuntimeStateLayout.CurrentSchemaVersion,
            payload.GetProperty("stateSchemaVersion").GetInt32());
        Assert.False(
            string.IsNullOrWhiteSpace(
                payload.GetProperty("runtimeVersion").GetString()));
        Assert.False(
            string.IsNullOrWhiteSpace(
                payload.GetProperty("runtimeInformationalVersion")
                    .GetString()));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}