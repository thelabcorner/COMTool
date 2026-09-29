using System.Collections.Concurrent;
using ComTool.Protocol;

namespace ComTool.Runtime.Ipc;

/// <summary>
/// A bounded idle pool of single-flight <see cref="RuntimePipeClient"/>
/// connections.
///
/// RuntimePipeClient deliberately serializes one request/response stream because
/// a single named-pipe connection is ordered. This pool preserves that simple
/// correlation model while allowing independent in-flight operations to use
/// independent server connections. That is required for out-of-band control
/// operations (for example host-generation termination) to remain reachable
/// while another runtime request is blocked.
/// </summary>
public sealed class RuntimePipeClientPool : IAsyncDisposable
{
    public const int DefaultMaxIdleClients = 32;

    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;
    private readonly int _maxIdleClients;
    private readonly bool _selfHeal;
    private readonly ConcurrentBag<RuntimePipeClient> _idle = [];

    private int _idleCount;
    private int _disposed;

    private RuntimePipeClientPool(
        string pipeName,
        TimeSpan connectTimeout,
        int maxIdleClients,
        bool selfHeal,
        RuntimePipeClient initialClient)
    {
        _pipeName = pipeName;
        _connectTimeout = connectTimeout;
        _maxIdleClients = maxIdleClients;
        _selfHeal = selfHeal;
        _idle.Add(initialClient);
        _idleCount = 1;
    }

    public static async Task<RuntimePipeClientPool> ConnectAsync(
        string? pipeName = null,
        TimeSpan? connectTimeout = null,
        int maxIdleClients = DefaultMaxIdleClients,
        bool selfHeal = true,
        CancellationToken cancellationToken = default)
    {
        if (maxIdleClients is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxIdleClients),
                "Idle client limit must be between 1 and 1024.");
        }

        var resolvedPipe =
            RuntimeEndpoint.ResolvePipeName(pipeName);
        var resolvedTimeout =
            connectTimeout ?? TimeSpan.FromSeconds(5);

        if (resolvedTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(connectTimeout),
                "Connect timeout must be positive.");
        }

        var initialClient =
            await RuntimePipeClient
                .ConnectAsync(
                    resolvedPipe,
                    resolvedTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

        return new RuntimePipeClientPool(
            resolvedPipe,
            resolvedTimeout,
            maxIdleClients,
            selfHeal,
            initialClient);
    }

    public async Task<OperationResult> ExecuteAsync(
        OperationRequest request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        RuntimePipeClient? client = null;

        try
        {
            if (_idle.TryTake(out client))
            {
                Interlocked.Decrement(
                    ref _idleCount);
            }
            else
            {
                client = await RuntimePipeClient
                    .ConnectAsync(
                        _pipeName,
                        _connectTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var result = await client
                .ExecuteAsync(
                    request,
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);

            if (Volatile.Read(ref _disposed) == 0 &&
                TryReturn(client))
            {
                client = null;
            }

            return result;
        }
        catch (RuntimeRequestInterruptedException) when (_selfHeal)
        {
            // The active request remains ambiguous and is never replayed.
            // Drop every idle handle from the same runtime generation so the
            // next independent request connects fresh instead of walking a
            // pool full of broken pipes one failure at a time.
            await InvalidateIdleAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (client is not null)
            {
                await client
                    .DisposeAsync()
                    .ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(
                ref _disposed,
                1) != 0)
            return;

        while (_idle.TryTake(out var client))
        {
            Interlocked.Decrement(
                ref _idleCount);

            await client
                .DisposeAsync()
                .ConfigureAwait(false);
        }
    }

    private bool TryReturn(
        RuntimePipeClient client)
    {
        var count =
            Interlocked.Increment(
                ref _idleCount);

        if (count > _maxIdleClients ||
            Volatile.Read(ref _disposed) != 0)
        {
            Interlocked.Decrement(
                ref _idleCount);
            return false;
        }

        _idle.Add(client);
        return true;
    }

    private async Task InvalidateIdleAsync()
    {
        while (_idle.TryTake(out var client))
        {
            Interlocked.Decrement(
                ref _idleCount);
            await client
                .DisposeAsync()
                .ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
}
