using System.Threading.Channels;

namespace Plural.Platform.Apple;

/// <summary>
/// Duplex stream over a callback-driven transport (IOBluetooth RFCOMM channel, External Accessory session): the transport
/// pushes received bytes with <see cref="Enqueue"/> and ends the stream with <see cref="Complete"/>; writes go to a delegate.
/// Supports blocking reads (MikeSession's reader thread) and async reads (remote links).
/// </summary>
internal sealed class QueueStream(Func<ReadOnlyMemory<byte>, Task> write, Action close) : Stream
{
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private byte[]? _current;
    private int _offset;
    private int _closed;

    public void Enqueue(ReadOnlySpan<byte> data)
    {
        if (!data.IsEmpty)
        {
            _incoming.Writer.TryWrite(data.ToArray());
        }
    }

    /// <summary>Ends the stream: reads return 0 once queued data is consumed, or throw <paramref name="error"/>.</summary>
    public void Complete(Exception? error = null) => _incoming.Writer.TryComplete(error);

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        while (_current is null)
        {
            if (_incoming.Reader.TryRead(out var chunk))
            {
                _current = chunk;
                _offset = 0;
            }
            else if (!_incoming.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                return 0;
            }
        }
        return Take(buffer);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_current is null)
        {
            if (_incoming.Reader.TryRead(out var chunk))
            {
                _current = chunk;
                _offset = 0;
            }
            else if (!await _incoming.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }
        }
        return Take(buffer.Span);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Take(Span<byte> buffer)
    {
        var n = Math.Min(buffer.Length, _current!.Length - _offset);
        _current.AsSpan(_offset, n).CopyTo(buffer);
        _offset += n;
        if (_offset == _current.Length)
        {
            _current = null;
        }
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_closed != 0, this);
        write(buffer.ToArray()).GetAwaiter().GetResult();
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_closed != 0, this);
        return new ValueTask(write(buffer.ToArray()).WaitAsync(cancellationToken));
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _closed, 1) == 0)
        {
            Complete();
            close();
        }
        base.Dispose(disposing);
    }
}
