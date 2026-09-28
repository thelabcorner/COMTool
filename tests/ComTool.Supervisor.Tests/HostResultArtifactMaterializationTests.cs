using System.Text;
using System.Text.Json;
using ComTool.Hosts.Abstractions;
using ComTool.Protocol;
using ComTool.Runtime;
using ComTool.Runtime.Artifacts;
using ComTool.Supervisor;

namespace ComTool.Supervisor.Tests;

public sealed class HostResultArtifactMaterializationTests
{
    [Fact]
    public async Task LargeHostResultBecomesOpaqueArtifactBeforePublicTransport()
    {
        var stateDirectory = StateDirectory();
        try
        {
            await using var runtime = Runtime(stateDirectory);
            var original = new string('x', 48 * 1024);
            var result = SuccessResult(original);

            var materialized = runtime.MaterializeLargeHostResult(
                result,
                MutationClass.ReadOnly);

            Assert.True(materialized.Ok, materialized.Error?.Message);
            Assert.NotNull(materialized.Result);
            Assert.Equal("object", materialized.Result!.Kind);

            var envelope = materialized.Result.Value!.Value;
            Assert.True(envelope.GetProperty("offloaded").GetBoolean());
            Assert.Equal(
                "string",
                envelope.GetProperty("originalResultKind").GetString());

            var artifact = envelope.GetProperty("artifact");
            var artifactId = artifact.GetProperty("artifactId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(artifactId));
            Assert.DoesNotContain(
                stateDirectory,
                envelope.GetRawText(),
                StringComparison.OrdinalIgnoreCase);

            var descriptor = runtime.ArtifactStore.Describe(artifactId!);
            Assert.Equal(
                artifact.GetProperty("sha256").GetString(),
                descriptor.Sha256);
            Assert.Equal(
                ArtifactMediaTypes.TextPlainUtf8,
                descriptor.MediaType);
            Assert.Equal(
                ArtifactMediaTypes.Utf8,
                descriptor.Encoding);

            var stored = runtime.ArtifactStore.GetRange(artifactId!);
            var restored = Encoding.UTF8.GetString(stored.Content.Span);
            Assert.Equal(original, restored);

            Assert.Contains(
                materialized.Evidence ?? [],
                static evidence =>
                    string.Equals(
                        evidence.Kind,
                        "artifact.offload",
                        StringComparison.Ordinal));
        }
        finally
        {
            DeleteStateDirectory(stateDirectory);
        }
    }

    [Fact]
    public async Task MultiMegabyteHostResultMaterializesBelowPublicFrameLimit()
    {
        var stateDirectory = StateDirectory();
        try
        {
            await using var runtime = Runtime(stateDirectory);
            var original = new string('z', 2 * 1024 * 1024);
            var materialized = runtime.MaterializeLargeHostResult(
                SuccessResult(original),
                MutationClass.ReadOnly);

            Assert.True(materialized.Ok, materialized.Error?.Message);
            Assert.True(
                materialized.Result!.Value!.Value
                    .GetProperty("offloaded")
                    .GetBoolean());

            var publicBytes = ProtocolJson.SerializeUtf8(materialized);
            Assert.True(
                publicBytes.Length <
                ComTool.Transport.Pipe.LengthPrefixedFramedStream
                    .DefaultMaxFrameBytes,
                $"Materialized public envelope was {publicBytes.Length} bytes.");
            Assert.Equal(
                ComTool.Transport.Pipe.LengthPrefixedFramedStream
                    .DefaultMaxFrameBytes,
                new WorkerBrokerOptions
                {
                    WorkerExecutablePath = "worker.exe"
                }.MaxFrameBytes);
        }
        finally
        {
            DeleteStateDirectory(stateDirectory);
        }
    }

    [Fact]
    public async Task SmallHostResultStaysInline()
    {
        var stateDirectory = StateDirectory();
        try
        {
            await using var runtime = Runtime(stateDirectory);
            var result = SuccessResult("small");

            var materialized = runtime.MaterializeLargeHostResult(
                result,
                MutationClass.ReadOnly);

            Assert.Same(result.Result, materialized.Result);
            Assert.Equal("small", materialized.Result!.Value!.Value.GetString());
            Assert.Null(materialized.Evidence);
        }
        finally
        {
            DeleteStateDirectory(stateDirectory);
        }
    }

    [Fact]
    public async Task ExistingArtifactEnvelopeIsNeverRecursivelyOffloaded()
    {
        var stateDirectory = StateDirectory();
        try
        {
            await using var runtime = Runtime(stateDirectory);

            using var content = new MemoryStream(
                Encoding.UTF8.GetBytes("\"payload\""),
                writable: false);
            var descriptor = runtime.ArtifactStore.Put(
                new ArtifactWriteRequest
                {
                    Content = content,
                    MediaType = ArtifactMediaTypes.ApplicationJson,
                    Encoding = "utf-8"
                });
            var result = new OperationResult
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = "already-offloaded",
                Operation = "script.eval",
                Ok = true,
                Status = OperationStatus.Completed,
                TargetState = TargetState.Known,
                Result = ArtifactResultOffload.Envelope(
                    descriptor,
                    "string")
            };

            var materialized = runtime.MaterializeLargeHostResult(
                result,
                MutationClass.ExternalSideEffect);

            Assert.Same(result.Result, materialized.Result);
            Assert.Null(materialized.Evidence);
        }
        finally
        {
            DeleteStateDirectory(stateDirectory);
        }
    }

    [Fact]
    public async Task MutatingLargeResultUsesMaximumBoundedRetention()
    {
        var stateDirectory = StateDirectory();
        try
        {
            await using var runtime = Runtime(stateDirectory);
            var result = SuccessResult(new string('m', 40 * 1024));

            var materialized = runtime.MaterializeLargeHostResult(
                result,
                MutationClass.IdempotentWrite);

            Assert.True(materialized.Ok, materialized.Error?.Message);
            var artifact = materialized.Result!.Value!.Value
                .GetProperty("artifact");
            var createdAt = artifact.GetProperty("createdAt")
                .GetDateTimeOffset();
            var expiresAt = artifact.GetProperty("expiresAt")
                .GetDateTimeOffset();

            Assert.InRange(
                expiresAt - createdAt,
                TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1),
                TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1));
        }
        finally
        {
            DeleteStateDirectory(stateDirectory);
        }
    }

    [Fact]
    public async Task MutationLedgerKeepsCanonicalResultAndReplayCanRematerialize()
    {
        var stateDirectory = StateDirectory();
        try
        {
            await using var runtime = Runtime(stateDirectory);
            var ledger = new MutationLedger(
                Path.Combine(stateDirectory, "ledger-proof"));
            var target = Target();
            var request = MutationRequest(target);
            var originalText = new string('r', 48 * 1024);
            var original = SuccessResult(originalText) with
            {
                Id = request.Id,
                Operation = request.Operation
            };

            var begin = ledger.Begin(
                target,
                request,
                MutationClass.IdempotentWrite);
            Assert.NotNull(begin.Record);

            ledger.Finalize(begin.Record!, original);

            var replay = ledger.Probe(
                target,
                request,
                MutationClass.IdempotentWrite);
            Assert.Equal(
                MutationLedgerBeginDisposition.ReplayCompleted,
                replay.Disposition);
            Assert.NotNull(replay.StoredResult);
            Assert.Equal(
                "string",
                replay.StoredResult!.Result?.Kind);
            Assert.Equal(
                originalText,
                replay.StoredResult.Result?.Value?.GetString());

            var delivered = runtime.MaterializeLargeHostResult(
                replay.StoredResult,
                MutationClass.IdempotentWrite);
            Assert.True(delivered.Ok, delivered.Error?.Message);
            Assert.True(
                delivered.Result!.Value!.Value
                    .GetProperty("offloaded")
                    .GetBoolean());
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
                    Path.Combine(stateDirectory, "unused-worker.exe")
            },
            stateDirectory);

    private static OperationResult SuccessResult(string value) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = "artifact-materialization-test",
            Operation = "script.eval",
            Ok = true,
            Status = OperationStatus.Completed,
            TargetState = TargetState.Known,
            Result = ProtocolValue.From(
                JsonSerializer.SerializeToElement(value))
        };

    private static HostTargetDescriptor Target()
    {
        var identity = new HostTargetIdentity
        {
            Host = "illustrator",
            ProcessId = 4242,
            ProcessStartedAt =
                DateTimeOffset.Parse("2026-09-27T12:00:00Z"),
            ExecutablePath = @"C:\Adobe\Illustrator.exe",
            HostVersion = "30.6.0",
            AdapterVersion = "test",
            EndpointIdentity = "Illustrator.Application"
        };

        return new HostTargetDescriptor
        {
            Identity = identity,
            Target = new TargetRef(
                identity.Host,
                identity.TargetId,
                Generation: 0),
            Capabilities = [],
            Running = true
        };
    }

    private static OperationRequest MutationRequest(
        HostTargetDescriptor target) =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = "artifact-ledger-replay-test",
            Target = target.Target,
            Operation = "illustrator.layer.setOpacity",
            Input = JsonSerializer.SerializeToElement(
                new
                {
                    document = new { index = 0 },
                    layer = new { index = 0 },
                    value = 50
                })
        };

    private static string StateDirectory() =>
        Path.Combine(
            Path.GetTempPath(),
            "comtool-v2-host-result-artifact-tests",
            Guid.NewGuid().ToString("N"));

    private static void DeleteStateDirectory(string stateDirectory)
    {
        if (Directory.Exists(stateDirectory))
            Directory.Delete(stateDirectory, recursive: true);
    }
}
