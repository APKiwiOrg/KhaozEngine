using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Replication;

/// <summary>A read-only, seekable window over bytes borrowed for synchronous decoding.</summary>
internal class BorrowedPayloadStream(string objectName = nameof(BorrowedPayloadStream)) : Stream
{
    private ReadOnlyMemory<byte>? source;
    private int position;

    private ReadOnlyMemory<byte> ActiveSource => source ?? throw new ObjectDisposedException(objectName);

    internal void Retarget(ReadOnlyMemory<byte> bytes)
    {
        source = bytes;
        position = 0;
    }

    internal void Release() => source = null;

    internal void EnsureOpen() => _ = ActiveSource;

    public override bool CanRead => source.HasValue;
    public override bool CanSeek => source.HasValue;
    public override bool CanWrite => false;
    public override long Length => ActiveSource.Length;

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
            if (value > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(value), "The position exceeds the stream length limit.");
            position = (int)value;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ReadOnlySpan<byte> bytes = ActiveSource.Span;
        int start = origin switch
        {
            SeekOrigin.Begin => 0,
            SeekOrigin.Current => position,
            SeekOrigin.End => bytes.Length,
            _ => throw new ArgumentException("Invalid seek origin."),
        };

        // Check the upper bound before adding. The nonnegative start cannot underflow a long.
        if (offset > (long)int.MaxValue - start)
            throw new ArgumentOutOfRangeException(nameof(offset), "The seek target exceeds the stream length limit.");
        long target = start + offset;
        if (target < 0) throw new IOException("Cannot seek before the start of the window.");
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
        ReadOnlySpan<byte> bytes = ActiveSource.Span;
        int count = Math.Min(bytes.Length - position, buffer.Length);
        if (count <= 0) return 0;
        bytes.Slice(position, count).CopyTo(buffer);
        position += count;
        return count;
    }

    public override int ReadByte()
    {
        ReadOnlySpan<byte> bytes = ActiveSource.Span;
        return position < bytes.Length ? bytes[position++] : -1;
    }

    public override void Flush() => EnsureOpen();

    // A decode borrow has no deferred operations. Refuse at the entry point before any stream work starts.
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) =>
        throw new NotSupportedException();

    public override int EndRead(IAsyncResult asyncResult) => throw new NotSupportedException();

    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public override Task FlushAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
    public override void WriteByte(byte value) => throw new NotSupportedException();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) =>
        throw new NotSupportedException();

    public override void EndWrite(IAsyncResult asyncResult) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override int ReadTimeout
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int WriteTimeout
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Release();
    }
}
