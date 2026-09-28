using System.Text;
using System.Text.Json;
using ComTool.Protocol;
using ComTool.Runtime.Artifacts;
using ComTool.Supervisor;

namespace ComTool.Supervisor.Tests;

public sealed class ArtifactRuntimeOperationTests
{
    [Fact]
    public async Task RuntimeDescribesAndReadsByOpaqueIdWithoutWorkerOrPathLeak()
    {
        var stateDirectory = NewStateDirectory();

        try
        {
            await using var runtime = Runtime(stateDirectory);
            var bytes = Encoding.UTF8.GetBytes("hello artifact");
            using var content = new MemoryStream(bytes, writable: false);

            var descriptor = runtime.ArtifactStore.Put(
                new ArtifactWriteRequest
                {
                    Content = content,
                    MediaType = ArtifactMediaTypes.TextPlainUtf8
                });

            var describe = await runtime.ExecuteAsync(
                Request(
                    ArtifactRuntimeOperations.DescribeOperation,
                    JsonSerializer.Serialize(new
                    {
                        artifactId = descriptor.ArtifactId
                    })));

            Assert.True(describe.Ok, describe.Error?.Message);
            Assert.Equal(OperationStatus.Completed, describe.Status);
            var described = Value(describe).GetProperty("artifact");
            Assert.Equal(
                descriptor.ArtifactId,
                described.GetProperty("artifactId").GetString());
            Assert.Equal(
                descriptor.Sha256,
                described.GetProperty("sha256").GetString());
            Assert.Equal(
                bytes.Length,
                described.GetProperty("byteCount").GetInt64());
            Assert.False(described.TryGetProperty("path", out _));
            Assert.False(described.TryGetProperty("root", out _));

            var read = await runtime.ExecuteAsync(
                Request(
                    ArtifactRuntimeOperations.ReadOperation,
                    JsonSerializer.Serialize(new
                    {
                        artifactId = descriptor.ArtifactId,
                        offset = 6,
                        length = 8
                    })));

            Assert.True(read.Ok, read.Error?.Message);
            Assert.Equal(OperationStatus.Completed, read.Status);
            var payload = Value(read);
            Assert.Equal(6, payload.GetProperty("offset").GetInt64());
            Assert.Equal(
                8,
                payload.GetProperty("returnedByteCount").GetInt32());
            Assert.True(payload.GetProperty("isPartial").GetBoolean());
            Assert.Equal(
                "artifact",
                Encoding.UTF8.GetString(
                    Convert.FromBase64String(
                        payload.GetProperty("contentBase64").GetString()!)));
            Assert.False(payload.TryGetProperty("path", out _));
        }
        finally
        {
            DeleteStateDirectory(stateDirectory);
        }
    }

    [Fact]
    public async Task ProtocolReadCeilingRequiresExplicitBoundedRanges()
    {
        var stateDirectory = NewStateDirectory();

        try
        {
            await using var runtime = Runtime(stateDirectory);
            var bytes = new byte[600 * 1024];
            for (var index = 0; index < bytes.Length; index++)
                bytes[index] = (byte)(index % 251);

            using var content = new MemoryStream(bytes, writable: false);
            var descriptor = runtime.ArtifactStore.Put(
                new ArtifactWriteRequest
                {
                    Content = content,
                    MediaType = ArtifactMediaTypes.ApplicationOctetStream
                });

            var whole = await runtime.ExecuteAsync(
                Request(
                    ArtifactRuntimeOperations.ReadOperation,
                    JsonSerializer.Serialize(new
                    {
                        artifactId = descriptor.ArtifactId
                    })));

            Assert.False(whole.Ok);
            Assert.Equal(OperationStatus.InvalidRequest, whole.Status);
            Assert.Equal("artifact_range_required", whole.Error?.Kind);
            Assert.Equal(ExecutionState.NotStarted, whole.Error?.Execution);

            var bounded = await runtime.ExecuteAsync(
                Request(
                    ArtifactRuntimeOperations.ReadOperation,
                    JsonSerializer.Serialize(new
                    {
                        artifactId = descriptor.ArtifactId,
                        offset = 0,
                        length =
                            ArtifactRuntimeOperations.MaxProtocolReadByteCount
                    })));

            Assert.True(bounded.Ok, bounded.Error?.Message);
            var base64 = Value(bounded)
                .GetProperty("contentBase64")
                .GetString()!;
            Assert.True(
                Encoding.UTF8.GetByteCount(base64) < 1024 * 1024,
                "Base64 payload must remain below the 1 MiB transport frame.");
            var publicFrame = ProtocolJson.SerializeUtf8(bounded);
            Assert.True(
                publicFrame.Length <
                    ComTool.Transport.Pipe.LengthPrefixedFramedStream
                        .DefaultMaxFrameBytes,
                $"Maximum artifact read serialized to {publicFrame.Length} bytes, " +
                "which must remain below the public transport frame ceiling.");

            var oversized = await runtime.ExecuteAsync(
                Request(
                    ArtifactRuntimeOperations.ReadOperation,
                    JsonSerializer.Serialize(new
                    {
                        artifactId = descriptor.ArtifactId,
                        offset = 0,
                        length =
                            ArtifactRuntimeOperations.MaxProtocolReadByteCount + 1
                    })));

            Assert.False(oversized.Ok);
            Assert.Equal(
                OperationStatus.InvalidRequest,
                oversized.Status);
            Assert.Equal(
                "artifact_range_too_large",
                oversized.Error?.Kind);
            Assert.Equal(
                ExecutionState.NotStarted,
                oversized.Error?.Execution);
        }
        finally
        {
            DeleteStateDirectory(stateDirectory);
        }
    }

    [Fact]
    public async Task LargeRuntimeResultIsOffloadedAndRetrievableBeforePublicTransport()
    {
        var stateDirectory = NewStateDirectory();

        try
        {
            await using var runtime = Runtime(stateDirectory);
            var source = string.Join(
                '\n',
                Enumerable.Repeat("var f = () => 1;", 200));
            var input = JsonSerializer.Serialize(new
            {
                kind = "code",
                source
            });

            var result = await runtime.ExecuteAsync(
                Request("script.validate", input));

            Assert.True(result.Ok, result.Error?.Message);
            var envelope = Value(result);
            Assert.True(envelope.GetProperty("offloaded").GetBoolean());
            Assert.Equal(
                "object",
                envelope.GetProperty("originalResultKind").GetString());

            var artifact = envelope.GetProperty("artifact");
            var artifactId = artifact.GetProperty("artifactId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(artifactId));
            Assert.True(
                artifact.GetProperty("byteCount").GetInt64() >=
                    ArtifactResultOffload.DefaultThresholdByteCount);
            Assert.Equal(
                ArtifactMediaTypes.ApplicationJson,
                artifact.GetProperty("mediaType").GetString());

            var publicFrame = ProtocolJson.SerializeUtf8(result);
            Assert.True(
                publicFrame.Length <
                    ComTool.Transport.Pipe.LengthPrefixedFramedStream
                        .DefaultMaxFrameBytes,
                $"Artifact envelope serialized to {publicFrame.Length} bytes.");

            var read = await runtime.ExecuteAsync(
                Request(
                    ArtifactRuntimeOperations.ReadOperation,
                    JsonSerializer.Serialize(new
                    {
                        artifactId
                    })));

            Assert.True(read.Ok, read.Error?.Message);
            var restoredBytes = Convert.FromBase64String(
                Value(read).GetProperty("contentBase64").GetString()!);
            using var restored = JsonDocument.Parse(restoredBytes);
            Assert.Equal(
                200,
                restored.RootElement
                    .GetProperty("findingCount")
                    .GetInt32());
            Assert.Equal(
                200,
                restored.RootElement
                    .GetProperty("findings")
                    .GetArrayLength());
        }
        finally
        {
            DeleteStateDirectory(stateDirectory);
        }
    }

    [Fact]
    public async Task RetrievalRejectsPathLikeAndUnknownInputsBeforeReading()
    {
        var stateDirectory = NewStateDirectory();

        try
        {
            await using var runtime = Runtime(stateDirectory);

            var result = await runtime.ExecuteAsync(
                Request(
                    ArtifactRuntimeOperations.ReadOperation,
                    """
                    {
                      "artifactId":"../../secret",
                      "path":"C:/secret"
                    }
                    """));

            Assert.False(result.Ok);
            Assert.Equal(OperationStatus.InvalidRequest, result.Status);
            Assert.Equal("invalid_artifact_request", result.Error?.Kind);
            Assert.Equal(ExecutionState.NotStarted, result.Error?.Execution);
        }
        finally
        {
            DeleteStateDirectory(stateDirectory);
        }
    }

    private static RuntimeSupervisor Runtime(string stateDirectory) =>
        new(
            ["illustrator"],
            new WorkerBrokerOptions
            {
                WorkerExecutablePath =
                    Path.Combine(stateDirectory, "does-not-exist.exe")
            },
            stateDirectory);

    private static OperationRequest Request(
        string operation,
        string inputJson)
    {
        using var input = JsonDocument.Parse(inputJson);
        return new OperationRequest
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = "artifact-runtime-operation-test",
            Operation = operation,
            Input = input.RootElement.Clone()
        };
    }

    private static JsonElement Value(OperationResult result)
    {
        Assert.NotNull(result.Result);
        Assert.True(result.Result!.Value.HasValue);
        return result.Result.Value.Value;
    }

    private static string NewStateDirectory() =>
        Path.Combine(
            Path.GetTempPath(),
            "comtool-v2-artifact-runtime-tests",
            Guid.NewGuid().ToString("N"));

    private static void DeleteStateDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        Directory.Delete(path, recursive: true);
    }
}
