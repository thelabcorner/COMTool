using System.IO.Pipes;
using ComTool.Protocol;
using ComTool.Transport.Pipe;

namespace ComTool.Runtime.Ipc;

public sealed class RuntimePipeClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly LengthPrefixedFramedStream _framed;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    private RuntimePipeClient(
        NamedPipeClientStream pipe,
        LengthPrefixedFramedStream framed)
    {
        _pipe = pipe;
        _framed = framed;
    }

    public static async Task<RuntimePipeClient> ConnectAsync(
        string? pipeName = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            RuntimeEndpoint.ResolvePipeName(pipeName),
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            connectCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));

            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);

            return new RuntimePipeClient(
                pipe,
                new LengthPrefixedFramedStream(pipe, leaveOpen: true));
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<OperationResult> ExecuteAsync(
        OperationRequest request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        ProtocolJson.ValidateRequest(request);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var operationCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            operationCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));

            await _framed
                .WriteAsync(
                    ProtocolJson.SerializeUtf8(request),
                    operationCts.Token)
                .ConfigureAwait(false);

            using var responseFrame = await _framed
                .ReadAsync(operationCts.Token)
                .ConfigureAwait(false);

            var result = ProtocolJson.DeserializeResult(responseFrame.Span);

            if (result.ProtocolVersion != ProtocolVersion.Current ||
                !string.Equals(result.Id, request.Id, StringComparison.Ordinal) ||
                !string.Equals(
                    result.Operation,
                    request.Operation,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Runtime response correlation/version check failed.");
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _gate.Dispose();
        await _framed.DisposeAsync().ConfigureAwait(false);
        await _pipe.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
}
