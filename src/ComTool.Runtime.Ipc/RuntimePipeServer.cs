using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using ComTool.Protocol;
using ComTool.Transport.Pipe;

namespace ComTool.Runtime.Ipc;

public sealed class RuntimePipeServer
{
    private readonly string _pipeName;
    private readonly Func<OperationRequest, CancellationToken, Task<OperationResult>> _handler;
    private readonly int _maxFrameBytes;
    private long _connectionId;

    public RuntimePipeServer(
        Func<OperationRequest, CancellationToken, Task<OperationResult>> handler,
        string? pipeName = null,
        int maxFrameBytes = LengthPrefixedFramedStream.DefaultMaxFrameBytes)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _pipeName = pipeName ?? RuntimeEndpoint.DefaultPipeName;

        if (maxFrameBytes < 256)
            throw new ArgumentOutOfRangeException(nameof(maxFrameBytes));

        _maxFrameBytes = maxFrameBytes;
    }

    public string PipeName => _pipeName;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var active = new ConcurrentDictionary<long, Task>();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                try
                {
                    await pipe
                        .WaitForConnectionAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                var id = Interlocked.Increment(ref _connectionId);
                var task = HandleClientAsync(pipe, cancellationToken);
                active[id] = task;

                _ = task.ContinueWith(
                    completed =>
                    {
                        active.TryRemove(id, out _);
                        _ = completed.Exception;
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            var tasks = active.Values.ToArray();
            if (tasks.Length > 0)
                await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private async Task HandleClientAsync(
        NamedPipeServerStream pipe,
        CancellationToken serverCancellation)
    {
        await using (pipe.ConfigureAwait(false))
        await using (var framed = new LengthPrefixedFramedStream(
                         pipe,
                         _maxFrameBytes,
                         leaveOpen: true))
        {
            while (pipe.IsConnected && !serverCancellation.IsCancellationRequested)
            {
                FrameLease frame;
                try
                {
                    frame = await framed
                        .ReadAsync(serverCancellation)
                        .ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    return;
                }
                catch (IOException)
                {
                    return;
                }
                catch (OperationCanceledException)
                    when (serverCancellation.IsCancellationRequested)
                {
                    return;
                }

                using (frame)
                {
                    OperationRequest request;
                    try
                    {
                        request = ProtocolJson.DeserializeRequest(frame.Span);
                    }
                    catch (Exception ex)
                        when (ex is JsonException or ProtocolValidationException)
                    {
                        // Without a trusted request id there is no safe
                        // correlation response. Terminate this malformed client
                        // instead of inventing an id that a well-behaved client
                        // might mistake for another request.
                        return;
                    }

                    OperationResult result;
                    try
                    {
                        result = await _handler(request, serverCancellation)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        result = new OperationResult
                        {
                            ProtocolVersion = ProtocolVersion.Current,
                            Id = request.Id,
                            Operation = request.Operation,
                            Ok = false,
                            Status = OperationStatus.Failed,
                            TargetState = TargetState.Known,
                            Error = new ProtocolError
                            {
                                Kind = "runtime_handler_failure",
                                Message = ex.Message,
                                Retryable = false,
                                Execution = ExecutionState.NotStarted,
                                HResult = ex.HResult,
                                HResultHex =
                                    $"0x{unchecked((uint)ex.HResult):X8}",
                                SuggestedActions = ["inspect_runtime"]
                            }
                        };
                    }

                    try
                    {
                        await framed
                            .WriteAsync(
                                ProtocolJson.SerializeUtf8(result),
                                serverCancellation)
                            .ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        return;
                    }
                }
            }
        }
    }
}
