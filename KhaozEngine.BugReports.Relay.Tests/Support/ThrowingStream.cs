using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Tests.BugReports.Relay.Support;

// A non-seekable request body. With no bytes supplied, any read is a test failure.
internal sealed class ThrowingStream(byte[]? bytes = null) : Stream
{
    public int BytesRead { get; private set; }
    public int ReadCalls { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        ReadCalls++;
        if (bytes is null) throw new InvalidOperationException("The request body must not be read.");
        int count = Math.Min(buffer.Length, bytes.Length - BytesRead);
        bytes.AsSpan(BytesRead, count).CopyTo(buffer);
        BytesRead += count;
        return count;
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
