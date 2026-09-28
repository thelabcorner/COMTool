using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ComTool.Broker.Protocol;
using ComTool.Protocol;
using ComTool.Runtime;
using ComTool.Supervisor;
using ComTool.Transport.Pipe;

namespace ComTool.Supervisor.Tests;

public sealed class WorkerBrokerOutcomeTests
{
    [Fact]
    public void ArtifactChunkFrameFitsDefaultOneMiBBrokerBound()
    {
        var frame = BrokerJson.Serialize(
            new BrokerResponse
            {
                BrokerVersion = BrokerVersion.Current,
                Sequence = 1,
                RequestId = "large-result",
                Kind = BrokerResponseKind.ArtifactChunk,
                Ok = true,
                ArtifactChunk = new BrokerArtifactChunk
                {
                    TransferId = Guid.NewGuid().ToString("N"),
                    ChunkIndex = 127,
                    Data = new byte[
                        BrokerArtifactTransferLimits.ChunkByteCount]
                }
            });

        Assert.True(
            frame.Length <
            LengthPrefixedFramedStream.DefaultMaxFrameBytes,
            $"Chunk frame was {frame.Length} bytes.");
    }

    [Fact]
    public void ArtifactTransferAccumulatorRestoresCanonicalProtocolValue()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                message = "large ✓ payload",
                values = Enumerable.Range(0, 100).ToArray()
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var transferId = Guid.NewGuid().ToString("N");
        var split = payload.Length / 2;
        var accumulator = new BrokerArtifactTransferAccumulator();

        accumulator.Append(
            Chunk(
                transferId,
                0,
                payload[..split]));
        accumulator.Append(
            Chunk(
                transferId,
                1,
                payload[split..]));

        var completed = accumulator.Complete(
            FinalTransfer(
                transferId,
                payload,
                chunkCount: 2,
                originalResultKind: "object"));

        Assert.Null(completed.ArtifactTransfer);
        Assert.Equal(
            "object",
            completed.OperationResult?.Result?.Kind);
        Assert.Equal(
            "large ✓ payload",
            completed.OperationResult?.Result?.Value?
                .GetProperty("message")
                .GetString());
    }

    [Fact]
    public void ArtifactTransferAccumulatorRejectsOutOfOrderChunk()
    {
        var accumulator = new BrokerArtifactTransferAccumulator();
        var transferId = Guid.NewGuid().ToString("N");

        var error = Assert.Throws<InvalidDataException>(
            () => accumulator.Append(
                Chunk(
                    transferId,
                    1,
                    Encoding.UTF8.GetBytes(
                        JsonSerializer.Serialize("payload")))));

        Assert.Contains(
            "expected 0",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ArtifactTransferAccumulatorRejectsDigestMismatch()
    {
        var payload = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize("payload"));
        var transferId = Guid.NewGuid().ToString("N");
        var accumulator = new BrokerArtifactTransferAccumulator();
        accumulator.Append(Chunk(transferId, 0, payload));

        var final = FinalTransfer(
            transferId,
            payload,
            chunkCount: 1,
            originalResultKind: "string") with
        {
            ArtifactTransfer = new BrokerArtifactTransfer
            {
                TransferId = transferId,
                ChunkCount = 1,
                PayloadByteCount = payload.LongLength,
                PayloadSha256 = new string('0', 64),
                OriginalResultKind = "string"
            }
        };

        Assert.Throws<InvalidDataException>(
            () => accumulator.Complete(final));
    }

    [Fact]
    public void ArtifactTransferAccumulatorRejectsChunkFloodBeyondArtifactCeiling()
    {
        var transferId = Guid.NewGuid().ToString("N");
        var accumulator = new BrokerArtifactTransferAccumulator();
        var maxChunks = checked(
            (int)((ComTool.Runtime.Artifacts.ArtifactStoreOptions
                       .DefaultMaxArtifactByteCount +
                   BrokerArtifactTransferLimits.ChunkByteCount - 1) /
                  BrokerArtifactTransferLimits.ChunkByteCount));

        for (var index = 0; index < maxChunks; index++)
            accumulator.Append(Chunk(transferId, index, [0x2A]));

        var error = Assert.Throws<InvalidDataException>(
            () => accumulator.Append(
                Chunk(transferId, maxChunks, [0x2A])));

        Assert.Contains(
            "chunk-count ceiling",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedMutationDiagnosticFailurePreservesKnownChangedState()
    {
        var state = new TargetStateMachine();

        WorkerBrokerClient.ApplyOperationOutcome(
            state,
            MutationClass.ExternalSideEffect,
            new OperationResult
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = "plugin-message-oversize",
                Operation = "plugin.message",
                Ok = false,
                Status = OperationStatus.Failed,
                TargetState = TargetState.KnownChanged,
                Error = new ProtocolError
                {
                    Kind = "plugin_response_too_large",
                    Message = "The plug-in call completed but its reply exceeded the inline limit.",
                    Retryable = false,
                    Execution = ExecutionState.Completed
                }
            });

        var snapshot = state.Snapshot();
        Assert.Equal(TargetState.KnownChanged, snapshot.State);
        Assert.Null(snapshot.IncidentKind);
    }

    [Fact]
    public void AmbiguousMutationStillOverridesReportedKnownChangedState()
    {
        var state = new TargetStateMachine();

        WorkerBrokerClient.ApplyOperationOutcome(
            state,
            MutationClass.ExternalSideEffect,
            new OperationResult
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = "plugin-message-ambiguous",
                Operation = "plugin.message",
                Ok = false,
                Status = OperationStatus.ReconciliationRequired,
                TargetState = TargetState.KnownChanged,
                Error = new ProtocolError
                {
                    Kind = "com_failure",
                    Message = "Transport failed after dispatch.",
                    Retryable = false,
                    Execution = ExecutionState.Ambiguous
                }
            });

        var snapshot = state.Snapshot();
        Assert.Equal(
            TargetState.ReconciliationRequired,
            snapshot.State);
        Assert.Equal("com_failure", snapshot.IncidentKind);
    }

    [Fact]
    public void StartedMutationEscalatesTargetStateToReconciliationRequired()
    {
        var state = new TargetStateMachine();

        WorkerBrokerClient.ApplyOperationOutcome(
            state,
            MutationClass.IdempotentWrite,
            new OperationResult
            {
                ProtocolVersion = ProtocolVersion.Current,
                Id = "typed-put-started",
                Operation = "illustrator.layer.setVisible",
                Ok = false,
                Status = OperationStatus.Failed,
                TargetState = TargetState.Known,
                Error = new ProtocolError
                {
                    Kind = "typed_mutation_selector_read_failed",
                    Message = "Host operation started but the effect boundary could not be certified.",
                    Retryable = false,
                    Execution = ExecutionState.Started
                }
            });

        var snapshot = state.Snapshot();
        Assert.Equal(
            TargetState.ReconciliationRequired,
            snapshot.State);
        Assert.Equal(
            "typed_mutation_selector_read_failed",
            snapshot.IncidentKind);
    }

    private static BrokerResponse Chunk(
        string transferId,
        int index,
        byte[] data) =>
        new()
        {
            BrokerVersion = BrokerVersion.Current,
            Sequence = 1,
            RequestId = "transfer-test",
            Kind = BrokerResponseKind.ArtifactChunk,
            Ok = true,
            ArtifactChunk = new BrokerArtifactChunk
            {
                TransferId = transferId,
                ChunkIndex = index,
                Data = data
            }
        };

    private static BrokerResponse FinalTransfer(
        string transferId,
        byte[] payload,
        int chunkCount,
        string originalResultKind)
    {
        var result = new OperationResult
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = "transfer-test",
            Operation = "script.eval",
            Ok = true,
            Status = OperationStatus.Completed,
            TargetState = TargetState.Known
        };

        return new BrokerResponse
        {
            BrokerVersion = BrokerVersion.Current,
            Sequence = 1,
            RequestId = "transfer-test",
            Kind = BrokerResponseKind.Operation,
            Ok = true,
            OperationResult = result,
            ArtifactTransfer = new BrokerArtifactTransfer
            {
                TransferId = transferId,
                ChunkCount = chunkCount,
                PayloadByteCount = payload.LongLength,
                PayloadSha256 = Convert
                    .ToHexString(SHA256.HashData(payload))
                    .ToLowerInvariant(),
                OriginalResultKind = originalResultKind
            }
        };
    }
}
