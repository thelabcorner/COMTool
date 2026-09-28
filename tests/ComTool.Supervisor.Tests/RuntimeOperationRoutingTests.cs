using System.Text.Json;
using ComTool.Protocol;
using ComTool.Supervisor;

namespace ComTool.Supervisor.Tests;

public sealed class RuntimeOperationRoutingTests
{
    [Fact]
    public async Task ScriptValidateRunsWithoutTargetWorkerOrHostDispatch()
    {
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "comtool-v2-runtime-operation-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            await using var runtime = new RuntimeSupervisor(
                ["illustrator"],
                new WorkerBrokerOptions
                {
                    WorkerExecutablePath =
                        Path.Combine(stateDirectory, "does-not-exist.exe")
                },
                stateDirectory);

            using var input = JsonDocument.Parse(
                """
                {
                  "kind":"expression",
                  "source":"6*7"
                }
                """);

            var result = await runtime.ExecuteAsync(
                new OperationRequest
                {
                    ProtocolVersion = ProtocolVersion.Current,
                    Id = "script-validate-runtime-route",
                    Operation = "script.validate",
                    Input = input.RootElement.Clone()
                });

            Assert.True(result.Ok, result.Error?.Message);
            Assert.Equal(OperationStatus.Completed, result.Status);
            Assert.Equal(TargetState.Known, result.TargetState);
            Assert.NotNull(result.Result);
            Assert.Equal("object", result.Result!.Kind);
            Assert.True(result.Result.Value.HasValue);

            var payload = result.Result.Value.Value;
            Assert.True(payload.GetProperty("advisory").GetBoolean());
            Assert.False(payload.GetProperty("authoritative").GetBoolean());
            Assert.False(payload.GetProperty("engineProof").GetBoolean());
            Assert.Equal(
                "es3-preflight",
                payload.GetProperty("analyzer").GetString());
            Assert.Equal(
                "expression",
                payload.GetProperty("kind").GetString());
            Assert.Equal(3, payload.GetProperty("sourceLength").GetInt32());
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
                Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task TargetAttachRejectsUnconfiguredHostBeforeWorkerDispatch()
    {
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "comtool-v2-runtime-operation-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            await using var runtime = new RuntimeSupervisor(
                ["illustrator"],
                new WorkerBrokerOptions
                {
                    WorkerExecutablePath =
                        Path.Combine(stateDirectory, "does-not-exist.exe")
                },
                stateDirectory);

            using var input = JsonDocument.Parse("{}");
            var result = await runtime.ExecuteAsync(
                new OperationRequest
                {
                    ProtocolVersion = ProtocolVersion.Current,
                    Id = "target-attach-unconfigured-host",
                    Target = new TargetRef(
                        "photoshop",
                        "photoshop:000000000000000000000000",
                        Generation: 0),
                    Operation = "core.target.attach",
                    Input = input.RootElement.Clone()
                });

            Assert.False(result.Ok);
            Assert.Equal(OperationStatus.InvalidRequest, result.Status);
            Assert.Equal("host_family_not_configured", result.Error?.Kind);
            Assert.Equal(
                ExecutionState.NotStarted,
                result.Error?.Execution);
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
                Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task TargetLaunchRejectsUnconfiguredHostBeforeWorkerDispatch()
    {
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "comtool-v2-runtime-operation-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            await using var runtime = new RuntimeSupervisor(
                ["illustrator"],
                new WorkerBrokerOptions
                {
                    WorkerExecutablePath =
                        Path.Combine(stateDirectory, "does-not-exist.exe")
                },
                stateDirectory);

            using var input = JsonDocument.Parse(
                """
                {
                  "host":"photoshop",
                  "progId":"Photoshop.Application",
                  "expectedHostVersion":null,
                  "launchTimeoutMs":10000,
                  "existingInstance":"fail",
                  "arguments":[]
                }
                """);

            var result = await runtime.ExecuteAsync(
                new OperationRequest
                {
                    ProtocolVersion = ProtocolVersion.Current,
                    Id = "target-launch-unconfigured-host",
                    Operation = "core.target.launch",
                    Input = input.RootElement.Clone()
                });

            Assert.False(result.Ok);
            Assert.Equal(OperationStatus.InvalidRequest, result.Status);
            Assert.Equal("host_family_not_configured", result.Error?.Kind);
            Assert.Equal(
                ExecutionState.NotStarted,
                result.Error?.Execution);
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
                Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task OperationExamplesRunsWithoutTargetWorkerOrHostDispatch()
    {
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "comtool-v2-runtime-operation-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            await using var runtime = new RuntimeSupervisor(
                ["illustrator"],
                new WorkerBrokerOptions
                {
                    WorkerExecutablePath =
                        Path.Combine(stateDirectory, "does-not-exist.exe")
                },
                stateDirectory);

            using var input = JsonDocument.Parse(
                """{"operation":"knowledge.symbol","limit":4}""");
            var result = await runtime.ExecuteAsync(
                new OperationRequest
                {
                    ProtocolVersion = ProtocolVersion.Current,
                    Id = "examples-runtime-route",
                    Operation = "core.operation.examples",
                    Input = input.RootElement.Clone()
                });

            Assert.True(result.Ok, result.Error?.Message);
            Assert.Equal(OperationStatus.Completed, result.Status);
            Assert.Equal(TargetState.Known, result.TargetState);
            var examples = result.Result!.Value!.Value
                .GetProperty("examples")
                .EnumerateArray()
                .ToArray();
            var example = Assert.Single(examples);
            Assert.Equal(
                "knowledge.symbol",
                example.GetProperty("operation").GetString());
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
                Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task KnowledgeDescribeRunsWithoutTargetWorkerOrHostDispatch()
    {
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "comtool-v2-runtime-operation-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            await using var runtime = new RuntimeSupervisor(
                ["illustrator"],
                new WorkerBrokerOptions
                {
                    WorkerExecutablePath =
                        Path.Combine(stateDirectory, "does-not-exist.exe")
                },
                stateDirectory);

            using var input = JsonDocument.Parse("{}");
            var result = await runtime.ExecuteAsync(
                new OperationRequest
                {
                    ProtocolVersion = ProtocolVersion.Current,
                    Id = "knowledge-describe-runtime-route",
                    Operation = "knowledge.describe",
                    Input = input.RootElement.Clone()
                });

            Assert.True(result.Ok, result.Error?.Message);
            Assert.Equal(OperationStatus.Completed, result.Status);
            Assert.Equal(TargetState.Known, result.TargetState);
            Assert.NotNull(result.Result);
            var payload = result.Result!.Value!.Value;
            Assert.Equal(
                "sqlite-index",
                payload.GetProperty("source")
                    .GetProperty("kind")
                    .GetString());
            Assert.False(
                payload.GetProperty("host")
                    .GetProperty("versionKnown")
                    .GetBoolean());
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
                Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task KnowledgePathsRunsWithoutTargetWorkerOrHostDispatch()
    {
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "comtool-v2-runtime-operation-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            await using var runtime = new RuntimeSupervisor(
                ["illustrator"],
                new WorkerBrokerOptions
                {
                    WorkerExecutablePath =
                        Path.Combine(stateDirectory, "does-not-exist.exe")
                },
                stateDirectory);

            using var input = JsonDocument.Parse(
                """
                {
                  "start":"Application",
                  "target":"Document.Close",
                  "maxDepth":4
                }
                """);

            var result = await runtime.ExecuteAsync(
                new OperationRequest
                {
                    ProtocolVersion = ProtocolVersion.Current,
                    Id = "knowledge-path-runtime-route",
                    Operation = "knowledge.paths",
                    Input = input.RootElement.Clone()
                });

            Assert.True(result.Ok, result.Error?.Message);
            Assert.Equal(OperationStatus.Completed, result.Status);
            Assert.Equal(TargetState.Known, result.TargetState);

            var payload = result.Result!.Value!.Value;
            Assert.Equal(
                "_Application",
                payload.GetProperty("start").GetString());
            Assert.Equal(
                "Document",
                payload.GetProperty("targetInterface").GetString());
            Assert.Equal(
                "Close",
                payload.GetProperty("targetMember").GetString());

            var step = Assert.Single(
                payload.GetProperty("steps").EnumerateArray().ToArray());
            Assert.Equal(
                "ActiveDocument",
                step.GetProperty("member").GetString());
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
                Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidRetryBudgetIsRejectedBeforeWorkerDispatch()
    {
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "comtool-v2-runtime-operation-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            await using var runtime = new RuntimeSupervisor(
                ["illustrator"],
                new WorkerBrokerOptions
                {
                    WorkerExecutablePath =
                        Path.Combine(stateDirectory, "does-not-exist.exe")
                },
                stateDirectory);

            using var input = JsonDocument.Parse("{}");
            var result = await runtime.ExecuteAsync(
                new OperationRequest
                {
                    ProtocolVersion = ProtocolVersion.Current,
                    Id = "invalid-retry-budget-runtime-route",
                    Operation = "core.runtime.health",
                    Input = input.RootElement.Clone(),
                    Policy = new OperationPolicy(
                        RetryBudgetMs:
                            OperationPolicy.MaxRetryBudgetMs + 1)
                });

            Assert.False(result.Ok);
            Assert.Equal(OperationStatus.InvalidRequest, result.Status);
            Assert.Equal("invalid_retry_budget", result.Error?.Kind);
            Assert.Equal(
                ExecutionState.NotStarted,
                result.Error?.Execution);
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
                Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(
        "com.set",
        """{"path":"ActiveDocument.Layers[0].Name","value":42}""",
        "com_inventory_type_mismatch")]
    [InlineData(
        "com.call",
        """{"path":"ActiveDocument.ProcessGesture","args":[]}""",
        "com_inventory_arity_mismatch")]
    [InlineData(
        "com.call.read",
        """{"path":"ActiveDocument.Artboards.GetActiveArtboardIndex","args":[1]}""",
        "com_inventory_arity_mismatch")]
    [InlineData(
        "com.call.read",
        """{"path":"ActiveDocument.Artboards.GetByName","args":[42]}""",
        "com_inventory_type_mismatch")]
    public async Task GenericComInventoryContradictionsAreRejectedBeforeTargetOrWorker(
        string operation,
        string inputJson,
        string expectedKind)
    {
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "comtool-v2-runtime-operation-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            await using var runtime = new RuntimeSupervisor(
                ["illustrator"],
                new WorkerBrokerOptions
                {
                    WorkerExecutablePath =
                        Path.Combine(stateDirectory, "does-not-exist.exe")
                },
                stateDirectory);

            using var input = JsonDocument.Parse(inputJson);
            var result = await runtime.ExecuteAsync(
                new OperationRequest
                {
                    ProtocolVersion = ProtocolVersion.Current,
                    Id = "generic-com-inventory-rejection",
                    Target = new TargetRef(
                        "illustrator",
                        "illustrator:000000000000000000000000",
                        Generation: 0),
                    Operation = operation,
                    Input = input.RootElement.Clone()
                });

            Assert.False(result.Ok);
            Assert.Equal(OperationStatus.InvalidRequest, result.Status);
            Assert.Equal(expectedKind, result.Error?.Kind);
            Assert.Equal(
                ExecutionState.NotStarted,
                result.Error?.Execution);
            Assert.Contains(
                "knowledge.symbol",
                result.Error?.SuggestedActions ?? []);
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
                Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ScriptValidateRejectsBadInputAsNotStarted()
    {
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "comtool-v2-runtime-operation-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            await using var runtime = new RuntimeSupervisor(
                ["illustrator"],
                new WorkerBrokerOptions
                {
                    WorkerExecutablePath =
                        Path.Combine(stateDirectory, "does-not-exist.exe")
                },
                stateDirectory);

            using var input = JsonDocument.Parse(
                """{"kind":"expression","source":""}""");

            var result = await runtime.ExecuteAsync(
                new OperationRequest
                {
                    ProtocolVersion = ProtocolVersion.Current,
                    Id = "script-validate-runtime-invalid",
                    Operation = "script.validate",
                    Input = input.RootElement.Clone()
                });

            Assert.False(result.Ok);
            Assert.Equal(OperationStatus.InvalidRequest, result.Status);
            Assert.Equal(
                "invalid_script_validate_request",
                result.Error?.Kind);
            Assert.Equal(
                ExecutionState.NotStarted,
                result.Error?.Execution);
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
                Directory.Delete(stateDirectory, recursive: true);
        }
    }
}
