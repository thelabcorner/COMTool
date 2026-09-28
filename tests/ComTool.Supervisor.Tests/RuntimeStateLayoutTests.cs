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
    public async Task RuntimeOperationCatalogIsSelfDescribingWithoutHostDiscovery()
    {
        await using var runtime = new RuntimeSupervisor(
            ["illustrator"],
            new WorkerBrokerOptions
            {
                WorkerExecutablePath =
                    Path.Combine(_root, "unused-worker.exe")
            },
            _root);

        using var nullInput = JsonDocument.Parse("null");
        var listed = await runtime.ExecuteAsync(
            new OperationRequest
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = "operations-list",
                Operation = "core.operations.list",
                Input = nullInput.RootElement.Clone()
            });

        Assert.True(listed.Ok, listed.Error?.Message);
        var operations = listed.Result!.Value!.Value;
        Assert.Equal(JsonValueKind.Array, operations.ValueKind);
        var scriptEval = operations
            .EnumerateArray()
            .Single(entry =>
                entry.GetProperty("name").GetString() == "script.eval");
        Assert.Equal(
            "unknown",
            scriptEval.GetProperty("mutationClass").GetString());
        Assert.Equal(
            "host",
            scriptEval.GetProperty("executionScope").GetString());
        Assert.Equal(
            "illustrator",
            scriptEval.GetProperty("host").GetString());
        Assert.True(
            scriptEval.GetProperty("requiresLease").GetBoolean());
        Assert.Equal(
            "declared_or_unknown",
            scriptEval.GetProperty("mutationResolution").GetString());

        var policyLimits = scriptEval.GetProperty("policyLimits");
        Assert.Equal(
            OperationPolicy.MinWorkerWatchdogMs,
            policyLimits.GetProperty("workerWatchdogMs")
                .GetProperty("min")
                .GetInt32());
        Assert.Equal(
            OperationPolicy.MaxWorkerWatchdogMs,
            policyLimits.GetProperty("workerWatchdogMs")
                .GetProperty("max")
                .GetInt32());
        Assert.Equal(
            OperationPolicy.MinRetryBudgetMs,
            policyLimits.GetProperty("retryBudgetMs")
                .GetProperty("min")
                .GetInt32());
        Assert.Equal(
            OperationPolicy.MaxRetryBudgetMs,
            policyLimits.GetProperty("retryBudgetMs")
                .GetProperty("max")
                .GetInt32());
        Assert.Equal(
            OperationPolicy.DefaultRetryBudgetMs,
            policyLimits.GetProperty("retryBudgetMs")
                .GetProperty("default")
                .GetInt32());

        using var describeInput = JsonDocument.Parse(
            """{"name":"core.target.host.terminate"}""");
        var described = await runtime.ExecuteAsync(
            new OperationRequest
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = "operation-describe",
                Operation = "core.operation.describe",
                Input = describeInput.RootElement.Clone()
            });

        Assert.True(described.Ok, described.Error?.Message);
        var descriptor = described.Result!.Value!.Value;
        Assert.Equal(
            "external_side_effect",
            descriptor.GetProperty("mutationClass").GetString());
        Assert.Equal(
            "runtime",
            descriptor.GetProperty("executionScope").GetString());
        Assert.True(
            descriptor.GetProperty("requiresTarget").GetBoolean());
        Assert.True(
            descriptor.GetProperty("requiresLease").GetBoolean());
    }

    [Fact]
    public async Task RuntimeOperationDescribeRejectsUnknownNameWithoutHostDiscovery()
    {
        await using var runtime = new RuntimeSupervisor(
            ["illustrator"],
            new WorkerBrokerOptions
            {
                WorkerExecutablePath =
                    Path.Combine(_root, "unused-worker.exe")
            },
            _root);

        using var input = JsonDocument.Parse(
            """{"name":"plugin.future.missing"}""");
        var result = await runtime.ExecuteAsync(
            new OperationRequest
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = "operation-describe-missing",
                Operation = "core.operation.describe",
                Input = input.RootElement.Clone()
            });

        Assert.False(result.Ok);
        Assert.Equal(
            OperationStatus.UnsupportedOperation,
            result.Status);
        Assert.Equal(
            "unsupported_operation",
            result.Error?.Kind);
        Assert.Equal(
            ExecutionState.NotStarted,
            result.Error?.Execution);
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
                WorkerExecutablePath = ResolveTestWorkerExecutable()
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

        var offlineResolutionInput =
            JsonSerializer.SerializeToElement(new
            {
                targetId = target.Identity.TargetId,
                incidentRequestId = request.Id,
                expectedUpdatedAt =
                    incident.GetProperty("updatedAt").GetString(),
                resolution = "known_unchanged",
                rationale =
                    "The recorded target generation is no longer running; external inspection established that the intended change is absent."
            });
        var resolved = await runtime.ExecuteAsync(
            new OperationRequest
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = "resolve-offline-incident",
                Operation = "core.incident.resolve",
                Input = offlineResolutionInput
            });

        Assert.True(resolved.Ok, resolved.Error?.Message);
        Assert.Equal(TargetState.Unavailable, resolved.TargetState);
        Assert.Equal(
            "known_unchanged",
            resolved.Result!.Value!.Value
                .GetProperty("resolution")
                .GetString());

        var health = await runtime.ExecuteAsync(
            new OperationRequest
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = "health-with-undiscovered-incident",
                Operation = "core.runtime.health",
                Input = nullInput.RootElement.Clone()
            });
        Assert.Equal(
            0,
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

    private static string ResolveTestWorkerExecutable()
    {
        var candidates = new[]
        {
            Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "ComTool.Worker",
                    "release",
                    "ComTool.Worker.exe")),
            Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "src",
                    "ComTool.Worker",
                    "bin",
                    "Release",
                    "net10.0-windows",
                    "ComTool.Worker.exe"))
        };

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "ComTool.Worker.exe was not built for the supervisor integration test.",
                candidates[0]);
    }
}