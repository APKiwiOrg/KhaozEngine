using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Replication;

/// <summary>A read-only, seekable view of one stored sample for the duration of a codec callback.</summary>
internal sealed class SamplePayloadStream : Stream
{
    private byte[]? sample;
    private int position;
    private Task<int>? lastArrayRead;

    private byte[] ActiveSample => sample ?? throw new ObjectDisposedException(nameof(SamplePayloadStream));

    internal void Retarget(byte[] bytes)
    {
        sample = bytes;
        position = 0;
    }

    internal void Release() => sample = null;

    internal void EnsureOpen() => _ = ActiveSample;

    public override bool CanRead => sample is not null;
    public override bool CanSeek => sample is not null;
    public override bool CanWrite => false;
    public override long Length => ActiveSample.Length;

    public override long Position
    {
        get
        {
            EnsureOpen();
            return position;
        }
        set
        {
            EnsureOpen();
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, (long)int.MaxValue);
            position = (int)value;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        byte[] bytes = ActiveSample;
        int start = origin switch
        {
            SeekOrigin.Begin => 0,
            SeekOrigin.Current => position,
            SeekOrigin.End => bytes.Length,
            _ => throw new ArgumentException("Invalid seek origin.", nameof(origin)),
        };

        // Check the upper bound before adding. The nonnegative start cannot underflow a long.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, (long)int.MaxValue - start);
        long target = start + offset;
        if (target < 0) throw new IOException("Cannot seek before the sample.");
        position = (int)target;
        return position;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        byte[] bytes = ActiveSample;
        int count = Math.Min(bytes.Length - position, buffer.Length);
        if (count <= 0) return 0;
        bytes.AsSpan(position, count).CopyTo(buffer);
        position += count;
        return count;
    }

    public override int ReadByte()
    {
        byte[] bytes = ActiveSample;
        return position < bytes.Length ? bytes[position++] : -1;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled<int>(cancellationToken);
        try
        {
            EnsureOpen();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        catch (Exception exception)
        {
            return ValueTask.FromException<int>(exception);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<int>(cancellationToken);
        try
        {
            int read = Read(buffer.AsSpan(offset, count));
            // Keep the last completed count across borrows. The task holds no reference to the payload.
            if (lastArrayRead is { } cached && cached.Result == read) return cached;
            return lastArrayRead = Task.FromResult(read);
        }
        catch (Exception exception)
        {
            return Task.FromException<int>(exception);
        }
    }

    public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    {
        EnsureOpen();
        return base.BeginRead(buffer, offset, count, callback, state);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Release();
        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
    public override void WriteByte(byte value) => throw new NotSupportedException();
}
