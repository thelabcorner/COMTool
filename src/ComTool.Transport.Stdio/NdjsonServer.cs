using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using ComTool.Protocol;
using ComTool.Runtime;

namespace ComTool.Transport.Stdio;

public sealed class NdjsonServer
{
    public const int DefaultMaxFrameBytes = 1024 * 1024;
    public const int DefaultMaxConcurrentRequests = 32;

    private const int ReadBufferBytes = 16 * 1024;
    private static readonly byte[] Newline = [(byte)'\n'];

    private readonly IOperationDispatcher _dispatcher;
    private readonly int _maxFrameBytes;
    private readonly int _maxConcurrentRequests;

    public NdjsonServer(
        IOperationDispatcher dispatcher,
        int maxFrameBytes = DefaultMaxFrameBytes,
        int maxConcurrentRequests = DefaultMaxConcurrentRequests)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        if (maxFrameBytes < 256)
            throw new ArgumentOutOfRangeException(
                nameof(maxFrameBytes),
                "Frame limit must be at least 256 bytes.");

        if (maxConcurrentRequests is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrentRequests),
                "Concurrent request limit must be between 1 and 1024.");

        _maxFrameBytes = maxFrameBytes;
        _maxConcurrentRequests = maxConcurrentRequests;
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
        long dispatchSequence = 0;

        using var outputGate = new SemaphoreSlim(1, 1);
        using var dispatchSlots = new SemaphoreSlim(
            _maxConcurrentRequests,
            _maxConcurrentRequests);
        using var runCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var active = new ConcurrentDictionary<long, Task>();
        var dispatchFault =
            new TaskCompletionSource<Exception>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            while (true)
            {
                var bytesRead = await input
                    .ReadAsync(
                        readBuffer.AsMemory(0, ReadBufferBytes),
                        runCts.Token)
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
                            readBuffer.AsSpan(
                                segmentStart,
                                i - segmentStart));
                    }

                    if (oversized)
                    {
                        invalidSequence++;
                        await WriteResultAsync(
                                output,
                                outputGate,
                                InvalidTransportResult(
                                    $"transport-{invalidSequence}",
                                    "frame_too_large",
                                    $"NDJSON frame exceeded {_maxFrameBytes} bytes."),
                                runCts.Token)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        invalidSequence = await QueueFrameAsync(
                                frame.WrittenMemory,
                                output,
                                outputGate,
                                dispatchSlots,
                                active,
                                dispatchFault,
                                runCts,
                                dispatchSequence:
                                    Interlocked.Increment(
                                        ref dispatchSequence),
                                invalidSequence,
                                runCts.Token)
                            .ConfigureAwait(false);
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
                        readBuffer.AsSpan(
                            segmentStart,
                            remainderLength));
                }
            }

            // Accept a final non-newline-terminated frame at EOF.
            if (oversized)
            {
                invalidSequence++;
                await WriteResultAsync(
                        output,
                        outputGate,
                        InvalidTransportResult(
                            $"transport-{invalidSequence}",
                            "frame_too_large",
                            $"NDJSON frame exceeded {_maxFrameBytes} bytes."),
                        runCts.Token)
                    .ConfigureAwait(false);
            }
            else if (frame.WrittenCount > 0)
            {
                invalidSequence = await QueueFrameAsync(
                        frame.WrittenMemory,
                        output,
                        outputGate,
                        dispatchSlots,
                        active,
                        dispatchFault,
                        runCts,
                        dispatchSequence:
                            Interlocked.Increment(
                                ref dispatchSequence),
                        invalidSequence,
                        runCts.Token)
                    .ConfigureAwait(false);
            }

            await AwaitActiveAsync(active).ConfigureAwait(false);

            if (dispatchFault.Task.IsCompletedSuccessfully)
            {
                ExceptionDispatchInfo
                    .Capture(dispatchFault.Task.Result)
                    .Throw();
            }

            await output
                .FlushAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (
                !cancellationToken.IsCancellationRequested &&
                dispatchFault.Task.IsCompletedSuccessfully)
        {
            await AwaitActiveAsync(active).ConfigureAwait(false);

            ExceptionDispatchInfo
                .Capture(dispatchFault.Task.Result)
                .Throw();
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
                runCts.Cancel();

            try
            {
                await AwaitActiveAsync(active).ConfigureAwait(false);
            }
            catch
            {
                // A dispatch fault is surfaced through dispatchFault above.
                // External cancellation is surfaced by the main read/write path.
            }

            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    private async ValueTask<long> QueueFrameAsync(
        ReadOnlyMemory<byte> frame,
        Stream output,
        SemaphoreSlim outputGate,
        SemaphoreSlim dispatchSlots,
        ConcurrentDictionary<long, Task> active,
        TaskCompletionSource<Exception> dispatchFault,
        CancellationTokenSource runCts,
        long dispatchSequence,
        long invalidSequence,
        CancellationToken cancellationToken)
    {
        // Normalize CRLF without changing payload bytes.
        if (frame.Length > 0 &&
            frame.Span[^1] == (byte)'\r')
            frame = frame[..^1];

        OperationRequest request;
        try
        {
            request = ProtocolJson.DeserializeRequest(
                frame.Span);
        }
        catch (ProtocolValidationException ex)
        {
            invalidSequence++;
            var identity =
                ExtractIdentity(
                    frame,
                    invalidSequence);

            await WriteResultAsync(
                    output,
                    outputGate,
                    InvalidTransportResult(
                        identity.Id,
                        ex.Kind,
                        ex.Message,
                        identity.Operation),
                    cancellationToken)
                .ConfigureAwait(false);

            return invalidSequence;
        }
        catch (JsonException ex)
        {
            invalidSequence++;
            var identity =
                ExtractIdentity(
                    frame,
                    invalidSequence);

            await WriteResultAsync(
                    output,
                    outputGate,
                    InvalidTransportResult(
                        identity.Id,
                        "invalid_json",
                        ex.Message,
                        identity.Operation),
                    cancellationToken)
                .ConfigureAwait(false);

            return invalidSequence;
        }

        await dispatchSlots
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        var task = DispatchAndWriteAsync(
            request,
            output,
            outputGate,
            dispatchSlots,
            cancellationToken);

        active[dispatchSequence] = task;

        _ = task.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted)
                {
                    var exception =
                        completed.Exception?.GetBaseException() ??
                        new InvalidOperationException(
                            "NDJSON request dispatch failed.");

                    if (dispatchFault.TrySetResult(exception))
                        runCts.Cancel();
                }

                active.TryRemove(
                    dispatchSequence,
                    out _);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return invalidSequence;
    }

    private async Task DispatchAndWriteAsync(
        OperationRequest request,
        Stream output,
        SemaphoreSlim outputGate,
        SemaphoreSlim dispatchSlots,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _dispatcher
                .ExecuteAsync(
                    request,
                    cancellationToken)
                .ConfigureAwait(false);

            await WriteResultAsync(
                    output,
                    outputGate,
                    result,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            dispatchSlots.Release();
        }
    }

    private bool TryAppend(
        ArrayBufferWriter<byte> frame,
        ReadOnlySpan<byte> segment)
    {
        if (segment.Length == 0)
            return true;

        if (frame.WrittenCount >
            _maxFrameBytes - segment.Length)
            return false;

        segment.CopyTo(
            frame.GetSpan(segment.Length));
        frame.Advance(segment.Length);
        return true;
    }

    private static async Task AwaitActiveAsync(
        ConcurrentDictionary<long, Task> active)
    {
        while (!active.IsEmpty)
        {
            var tasks = active.Values.ToArray();
            if (tasks.Length == 0)
            {
                await Task.Yield();
                continue;
            }

            try
            {
                await Task
                    .WhenAll(tasks)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The first dispatch exception is recorded by the task
                // continuation and surfaced by RunAsync.
            }
        }
    }

    private static async ValueTask WriteResultAsync(
        Stream output,
        SemaphoreSlim outputGate,
        OperationResult result,
        CancellationToken cancellationToken)
    {
        await outputGate
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var payload =
                ProtocolJson.SerializeUtf8(result);

            await output
                .WriteAsync(
                    payload,
                    cancellationToken)
                .ConfigureAwait(false);

            await output
                .WriteAsync(
                    Newline,
                    cancellationToken)
                .ConfigureAwait(false);

            await output
                .FlushAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            outputGate.Release();
        }
    }

    private static OperationResult InvalidTransportResult(
        string id,
        string kind,
        string message,
        string operation = "_invalid") =>
        new()
        {
            ProtocolVersion =
                ProtocolVersion.Current,
            Id = id,
            Operation = operation,
            Ok = false,
            Status =
                ComTool.Protocol.OperationStatus.InvalidRequest,
            TargetState =
                TargetState.Known,
            Error = new ProtocolError
            {
                Kind = kind,
                Message = message,
                Retryable = false,
                Execution =
                    ExecutionState.NotStarted,
                SuggestedActions =
                    ["fix_request_and_retry"]
            }
        };

    private static (string Id, string Operation)
        ExtractIdentity(
            ReadOnlyMemory<byte> frame,
            long invalidSequence)
    {
        var fallback =
            ($"transport-{invalidSequence}", "_invalid");

        try
        {
            using var document =
                JsonDocument.Parse(frame);

            if (document.RootElement.ValueKind !=
                JsonValueKind.Object)
                return fallback;

            var root =
                document.RootElement;

            var id =
                root.TryGetProperty(
                    "id",
                    out var idProperty) &&
                idProperty.ValueKind ==
                    JsonValueKind.String
                    ? idProperty.GetString()
                    : null;

            var operation =
                root.TryGetProperty(
                    "operation",
                    out var operationProperty) &&
                operationProperty.ValueKind ==
                    JsonValueKind.String
                    ? operationProperty.GetString()
                    : null;

            return (
                string.IsNullOrWhiteSpace(id)
                    ? fallback.Item1
                    : id!,
                string.IsNullOrWhiteSpace(operation)
                    ? fallback.Item2
                    : operation!);
        }
        catch (JsonException)
        {
            return fallback;
        }
    }
}
