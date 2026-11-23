using System;
using System.IO;

namespace KhaozEngine.Replication;

/// <summary>
/// A read-only window onto part of a snapshot buffer, re-pointed per component so an extension codec's
/// <see cref="BinaryReader"/> sees EXACTLY its own length-prefixed payload and nothing behind it.
/// <para>Without that bound a codec reads through the reader over the WHOLE snapshot, so a payload that lies
/// about an internal declared length is satisfied out of the FOLLOWING components' bytes whenever anything rides
/// behind it, and the apply succeeds carrying a value rebuilt from bytes that belong to someone else. The lie is
/// then caught only when it happens to run off the end of the snapshot, which in a real multi-entity snapshot is
/// the rare case. Bounding the reader is what turns every other case into a short read the codec can refuse.</para>
/// <para>One instance is reused for a whole apply: the view is single-threaded and no codec re-enters the read
/// loop, so framing a payload allocates nothing. Holds no unmanaged resource, so it is never disposed.</para>
/// </summary>
internal sealed class FramedPayloadStream : Stream
{
    private byte[] buffer = Array.Empty<byte>();
    private int origin;
    private int count;
    private int position;

    /// <summary>Points the window at <paramref name="length"/> bytes of <paramref name="source"/> starting at
    /// <paramref name="offset"/>, rewound to the start of that window.</summary>
    public void Retarget(byte[] source, int offset, int length)
    {
        buffer = source;
        origin = offset;
        count = length;
        position = 0;
    }

    /// <summary>Drops the reference to the snapshot buffer, so a finished apply does not keep it alive.</summary>
    public void Release() => Retarget(Array.Empty<byte>(), 0, 0);

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => count;

    /// <summary>Position WITHIN the window. A seek outside it clamps rather than throwing: the window exists to
    /// bound a hostile payload, and clamping a forward seek to the end is the bound doing its job.</summary>
    public override long Position
    {
        get => position;
        set => position = (int)Math.Clamp(value, 0, count);
    }

    public override int Read(byte[] target, int offset, int length)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        int n = Math.Min(length, count - position);
        if (n <= 0) return 0;
        Array.Copy(buffer, origin + position, target, offset, n);
        position += n;
        return n;
    }

    public override int Read(Span<byte> target)
    {
        int n = Math.Min(target.Length, count - position);
        if (n <= 0) return 0;
        buffer.AsSpan(origin + position, n).CopyTo(target);
        position += n;
        return n;
    }

    public override int ReadByte() => position < count ? buffer[origin + position++] : -1;

    public override long Seek(long offset, SeekOrigin loc)
    {
        Position = loc switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => count + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(loc)),
        };
        return position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] source, int offset, int length) => throw new NotSupportedException();
}
