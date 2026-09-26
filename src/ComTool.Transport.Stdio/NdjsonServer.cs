using System.Buffers;
using System.Text.Json;
using ComTool.Protocol;
using ComTool.Runtime;

namespace ComTool.Transport.Stdio;

public sealed class NdjsonServer
{
    public const int DefaultMaxFrameBytes = 1024 * 1024;
    private const int ReadBufferBytes = 16 * 1024;
    private static readonly byte[] Newline = [(byte)'\n'];

    private readonly IOperationDispatcher _dispatcher;
    private readonly int _maxFrameBytes;

    public NdjsonServer(
        IOperationDispatcher dispatcher,
        int maxFrameBytes = DefaultMaxFrameBytes)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        if (maxFrameBytes < 256)
            throw new ArgumentOutOfRangeException(
                nameof(maxFrameBytes),
                "Frame limit must be at least 256 bytes.");

        _maxFrameBytes = maxFrameBytes;
    }

    public async Task RunAsync(
        Stream input,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var readBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);
        var frame = new ArrayBufferWriter<byte>(Math.Min(4096, _maxFrameBytes));
        var oversized = false;
        long invalidSequence = 0;

        try
        {
            while (true)
            {
                var bytesRead = await input
                    .ReadAsync(readBuffer.AsMemory(0, ReadBufferBytes), cancellationToken)
                    .ConfigureAwait(false);

                if (bytesRead == 0)
                    break;

                var segmentStart = 0;

                for (var i = 0; i < bytesRead; i++)
                {
                    if (readBuffer[i] != (byte)'\n')
                        continue;

                    if (!oversized)
                    {
                        oversized = !TryAppend(
                            frame,
                            readBuffer.AsSpan(segmentStart, i - segmentStart));
                    }

                    if (oversized)
                    {
                        invalidSequence++;
                        await WriteResultAsync(
                            output,
                            InvalidTransportResult(
                                $"transport-{invalidSequence}",
                                "frame_too_large",
                                $"NDJSON frame exceeded {_maxFrameBytes} bytes."),
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        invalidSequence = await HandleFrameAsync(
                            frame.WrittenMemory,
                            output,
                            invalidSequence,
                            cancellationToken).ConfigureAwait(false);
                    }

                    frame.Clear();
                    oversized = false;
                    segmentStart = i + 1;
                }

                var remainderLength = bytesRead - segmentStart;
                if (!oversized && remainderLength > 0)
                {
                    oversized = !TryAppend(
                        frame,
                        readBuffer.AsSpan(segmentStart, remainderLength));
                }
            }

            // Accept a final non-newline-terminated frame at EOF.
            if (oversized)
            {
                invalidSequence++;
                await WriteResultAsync(
                    output,
                    InvalidTransportResult(
                        $"transport-{invalidSequence}",
                        "frame_too_large",
                        $"NDJSON frame exceeded {_maxFrameBytes} bytes."),
                    cancellationToken).ConfigureAwait(false);
            }
            else if (frame.WrittenCount > 0)
            {
                await HandleFrameAsync(
                    frame.WrittenMemory,
                    output,
                    invalidSequence,
                    cancellationToken).ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    private bool TryAppend(
        ArrayBufferWriter<byte> frame,
        ReadOnlySpan<byte> segment)
    {
        if (segment.Length == 0)
            return true;

        if (frame.WrittenCount > _maxFrameBytes - segment.Length)
            return false;

        segment.CopyTo(frame.GetSpan(segment.Length));
        frame.Advance(segment.Length);
        return true;
    }

    private async ValueTask<long> HandleFrameAsync(
        ReadOnlyMemory<byte> frame,
        Stream output,
        long invalidSequence,
        CancellationToken cancellationToken)
    {
        // Normalize CRLF without changing payload bytes.
        if (frame.Length > 0 && frame.Span[^1] == (byte)'\r')
            frame = frame[..^1];

        OperationRequest request;
        try
        {
            request = ProtocolJson.DeserializeRequest(frame.Span);
        }
        catch (ProtocolValidationException ex)
        {
            invalidSequence++;
            var identity = ExtractIdentity(frame, invalidSequence);
            await WriteResultAsync(
                output,
                InvalidTransportResult(
                    identity.Id,
                    ex.Kind,
                    ex.Message,
                    identity.Operation),
                cancellationToken).ConfigureAwait(false);
            return invalidSequence;
        }
        catch (JsonException ex)
        {
            invalidSequence++;
            var identity = ExtractIdentity(frame, invalidSequence);
            await WriteResultAsync(
                output,
                InvalidTransportResult(
                    identity.Id,
                    "invalid_json",
                    ex.Message,
                    identity.Operation),
                cancellationToken).ConfigureAwait(false);
            return invalidSequence;
        }

        var result = await _dispatcher
            .ExecuteAsync(request, cancellationToken)
            .ConfigureAwait(false);

        await WriteResultAsync(output, result, cancellationToken)
            .ConfigureAwait(false);

        return invalidSequence;
    }

    private static async ValueTask WriteResultAsync(
        Stream output,
        OperationResult result,
        CancellationToken cancellationToken)
    {
        var payload = ProtocolJson.SerializeUtf8(result);
        await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(Newline, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static OperationResult InvalidTransportResult(
        string id,
        string kind,
        string message,
        string operation = "_invalid") =>
        new()
        {
            ProtocolVersion = ProtocolVersion.Current,
            Id = id,
            Operation = operation,
            Ok = false,
            Status = ComTool.Protocol.OperationStatus.InvalidRequest,
            TargetState = TargetState.Known,
            Error = new ProtocolError
            {
                Kind = kind,
                Message = message,
                Retryable = false,
                Execution = ExecutionState.NotStarted,
                SuggestedActions = ["fix_request_and_retry"]
            }
        };

    private static (string Id, string Operation) ExtractIdentity(
        ReadOnlyMemory<byte> frame,
        long invalidSequence)
    {
        var fallback = ($"transport-{invalidSequence}", "_invalid");

        try
        {
            using var document = JsonDocument.Parse(frame);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return fallback;

            var root = document.RootElement;
            var id = root.TryGetProperty("id", out var idProperty) &&
                     idProperty.ValueKind == JsonValueKind.String
                ? idProperty.GetString()
                : null;

            var operation = root.TryGetProperty("operation", out var operationProperty) &&
                            operationProperty.ValueKind == JsonValueKind.String
                ? operationProperty.GetString()
                : null;

            return (
                string.IsNullOrWhiteSpace(id) ? fallback.Item1 : id!,
                string.IsNullOrWhiteSpace(operation) ? fallback.Item2 : operation!);
        }
        catch (JsonException)
        {
            return fallback;
        }
    }
}
