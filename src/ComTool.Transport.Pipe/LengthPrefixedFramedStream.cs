using System.Buffers;
using System.Buffers.Binary;

namespace ComTool.Transport.Pipe;

/// <summary>
/// Bounded length-prefixed framing over an arbitrary duplex stream.
/// Wire format: 4-byte little-endian signed payload length followed by payload bytes.
/// </summary>
public sealed class LengthPrefixedFramedStream : IAsyncDisposable
{
    public const int DefaultMaxFrameBytes = 1024 * 1024;

    private readonly Stream _stream;
    private readonly int _maxFrameBytes;
    private readonly bool _leaveOpen;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly byte[] _readHeader = new byte[sizeof(int)];
    private readonly byte[] _writeHeader = new byte[sizeof(int)];
    private int _readInProgress;
    private int _disposed;

    public LengthPrefixedFramedStream(
        Stream stream,
        int maxFrameBytes = DefaultMaxFrameBytes,
        bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));

        if (!stream.CanRead || !stream.CanWrite)
            throw new ArgumentException(
                "Framed stream requires a readable and writable duplex stream.",
                nameof(stream));

        if (maxFrameBytes < 256)
            throw new ArgumentOutOfRangeException(
                nameof(maxFrameBytes),
                "Frame limit must be at least 256 bytes.");

        _maxFrameBytes = maxFrameBytes;
        _leaveOpen = leaveOpen;
    }

    public int MaxFrameBytes => _maxFrameBytes;

    public void Write(ReadOnlySpan<byte> payload)
    {
        ThrowIfDisposed();

        if (payload.Length is <= 0 || payload.Length > _maxFrameBytes)
        {
            throw new InvalidDataException(
                $"Frame size {payload.Length} is outside allowed range 1..{_maxFrameBytes}.");
        }

        _writeGate.Wait();
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                _writeHeader,
                payload.Length);

            _stream.Write(_writeHeader);
            _stream.Write(payload);

            // Do not Flush. See WriteAsync: pipe-drain semantics can deadlock
            // request/response RPC.
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public FrameLease Read()
    {
        ThrowIfDisposed();

        if (Interlocked.Exchange(ref _readInProgress, 1) != 0)
            throw new InvalidOperationException(
                "Concurrent reads on one framed stream are not supported.");

        try
        {
            _stream.ReadExactly(_readHeader);

            var length = BinaryPrimitives.ReadInt32LittleEndian(_readHeader);
            if (length is <= 0 || length > _maxFrameBytes)
            {
                throw new InvalidDataException(
                    $"Frame size {length} is outside allowed range 1..{_maxFrameBytes}.");
            }

            var buffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                _stream.ReadExactly(buffer.AsSpan(0, length));
                return new FrameLease(buffer, length);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }
        }
        finally
        {
            Volatile.Write(ref _readInProgress, 0);
        }
    }

    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (payload.Length is <= 0 || payload.Length > _maxFrameBytes)
        {
            throw new InvalidDataException(
                $"Frame size {payload.Length} is outside allowed range 1..{_maxFrameBytes}.");
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                _writeHeader,
                payload.Length);

            await _stream
                .WriteAsync(_writeHeader, cancellationToken)
                .ConfigureAwait(false);

            await _stream
                .WriteAsync(payload, cancellationToken)
                .ConfigureAwait(false);

            // Intentionally do NOT call Flush/FlushAsync here. On Windows named
            // pipes, flushing maps to pipe-drain semantics and can block until
            // the peer consumes buffered bytes, deadlocking request/response
            // flows that await the write before beginning the peer read.
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<FrameLease> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (Interlocked.Exchange(ref _readInProgress, 1) != 0)
            throw new InvalidOperationException(
                "Concurrent reads on one framed stream are not supported.");

        try
        {
            await _stream
                .ReadExactlyAsync(_readHeader, cancellationToken)
                .ConfigureAwait(false);

            var length = BinaryPrimitives.ReadInt32LittleEndian(_readHeader);
            if (length is <= 0 || length > _maxFrameBytes)
            {
                throw new InvalidDataException(
                    $"Frame size {length} is outside allowed range 1..{_maxFrameBytes}.");
            }

            var buffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                await _stream
                    .ReadExactlyAsync(
                        buffer.AsMemory(0, length),
                        cancellationToken)
                    .ConfigureAwait(false);

                return new FrameLease(buffer, length);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }
        }
        finally
        {
            Volatile.Write(ref _readInProgress, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _writeGate.Dispose();

        if (!_leaveOpen)
            await _stream.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
    }
}

public sealed class FrameLease : IDisposable
{
    private byte[]? _buffer;

    internal FrameLease(byte[] buffer, int length)
    {
        _buffer = buffer;
        Length = length;
    }

    public int Length { get; }

    public ReadOnlyMemory<byte> Memory =>
        (_buffer ?? throw new ObjectDisposedException(nameof(FrameLease)))
        .AsMemory(0, Length);

    public ReadOnlySpan<byte> Span =>
        (_buffer ?? throw new ObjectDisposedException(nameof(FrameLease)))
        .AsSpan(0, Length);

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
            ArrayPool<byte>.Shared.Return(buffer);
    }
}
