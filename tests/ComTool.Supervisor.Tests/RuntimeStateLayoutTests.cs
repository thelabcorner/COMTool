using System.Text.Json;
using ComTool.Hosts.Abstractions;
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
        using var layout = RuntimeStateLayout.Open(_root);

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
    public void OpenRejectsConcurrentOwnerAndAllowsTakeoverAfterDispose()
    {
        using var first = RuntimeStateLayout.Open(_root);

        var ex = Assert.Throws<RuntimeStateLayoutException>(
            () => RuntimeStateLayout.Open(_root));

        Assert.Equal("runtime_state_in_use", ex.Kind);

        first.Dispose();
        using var second = RuntimeStateLayout.Open(_root);
        Assert.Equal(Path.GetFullPath(_root), second.Root);
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

        File.Delete(Path.Combine(_root, "state-manifest.json"));
        using var recovered = RuntimeStateLayout.Open(_root);
        Assert.Equal(Path.GetFullPath(_root), recovered.Root);
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
        Assert.Equal(
            Environment.ProcessId,
            payload.GetProperty("processId").GetInt32());
        Assert.Equal(
            "embedded",
            payload.GetProperty("frontEnd").GetString());
        Assert.Equal(
            Path.GetFullPath(_root),
            payload.GetProperty("stateRoot").GetString());
        Assert.Equal(
            0,
            payload.GetProperty("activeIncidents").GetInt32());
    }

    [Fact]
    public async Task RuntimeIncidentsListDoesNotRequireLiveTargetDiscovery()
    {
        HostTargetDescriptor target;
        OperationRequest request;

        using (var layout = RuntimeStateLayout.Open(_root))
        {
            var identity = new HostTargetIdentity
            {
                Host = "illustrator",
                ProcessId = 4242,
                ProcessStartedAt =
                    DateTimeOffset.Parse("2026-09-26T00:00:00Z"),
                ExecutablePath = @"C:\Adobe\Illustrator.exe",
                HostVersion = "30.6.0",
                AdapterVersion = "test",
                EndpointIdentity = "Illustrator.Application"
            };
            target = new HostTargetDescriptor
            {
                Identity = identity,
                Target = new TargetRef(
                    identity.Host,
                    identity.TargetId,
                    Generation: 0),
                Capabilities = Array.Empty<CapabilityDescriptor>(),
                Running = false
            };

            using var input = JsonDocument.Parse(
                """{"kind":"code","source":"return 1;"}""");
            request = new OperationRequest
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = "req-runtime-incident-list",
                Target = target.Target,
                Operation = "script.eval",
                Input = input.RootElement.Clone()
            };

            var ledger = new MutationLedger(
                layout.MutationLedgerRoot);
            _ = ledger.Begin(
                target,
                request,
                MutationClass.Unknown);
        }

        await using var runtime = new RuntimeSupervisor(
            ["illustrator"],
            new WorkerBrokerOptions
            {
                WorkerExecutablePath =
                    Path.Combine(_root, "unused-worker.exe")
            },
            _root);

        using var nullInput = JsonDocument.Parse("null");
        var result = await runtime.ExecuteAsync(
            new OperationRequest
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = "list-incidents-without-host",
                Operation = "core.incidents.list",
                Input = nullInput.RootElement.Clone()
            });

        Assert.True(result.Ok, result.Error?.Message);
        var incidents = result.Result!.Value!.Value;
        Assert.Equal(JsonValueKind.Array, incidents.ValueKind);

        var incident = Assert.Single(
            incidents.EnumerateArray().ToArray());
        Assert.Equal(
            target.Identity.TargetId,
            incident.GetProperty("targetId").GetString());
        Assert.Equal(
            request.Id,
            incident.GetProperty("requestId").GetString());
        Assert.Equal(
            "prepared",
            incident.GetProperty("phase").GetString());
        Assert.False(
            incident.GetProperty("recordCorrupt").GetBoolean());

        var health = await runtime.ExecuteAsync(
            new OperationRequest
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = "health-with-undiscovered-incident",
                Operation = "core.runtime.health",
                Input = nullInput.RootElement.Clone()
            });
        Assert.Equal(
            1,
            health.Result!.Value!.Value
                .GetProperty("activeIncidents")
                .GetInt32());
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