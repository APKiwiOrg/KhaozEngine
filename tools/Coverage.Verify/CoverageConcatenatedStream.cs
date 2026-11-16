using System;
using System.Collections.Generic;
using System.IO;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Reads files in order as one forward-only stream, opening one file at a time.</summary>
internal sealed class CoverageConcatenatedStream(IReadOnlyList<string> paths) : Stream
{
    int next;
    FileStream? current;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            if (current == null)
            {
                if (next == paths.Count) return 0;
                current = File.OpenRead(paths[next++]);
            }
            int count = current.Read(buffer);
            if (count > 0) return count;
            current.Dispose();
            current = null;
        }
        return 0;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) current?.Dispose();
        current = null;
        base.Dispose(disposing);
    }
}
