using System;
using System.IO;
using System.Runtime.CompilerServices;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

// #1886: the sample reader borrows the bytes only for the duration of the synchronous read.
[Collection("AllocSensitive")]
public sealed class ClientSampleReaderBorrowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReturnedSampleReaderRejectsReads(bool fail)
    {
        (BinaryReader reader, _) = Decode(fail);

        Stream stream = reader.BaseStream;
        byte[] bytes = new byte[1];
        char[] chars = new char[1];

        Assert.Throws<ObjectDisposedException>(() => reader.ReadByte());
        Assert.Throws<ObjectDisposedException>(() => reader.Read());
        Assert.Throws<ObjectDisposedException>(() => reader.PeekChar());
        Assert.Throws<ObjectDisposedException>(() => reader.ReadBytes(1));
        Assert.Throws<ObjectDisposedException>(() => reader.ReadBytes(0));
        Assert.Throws<ObjectDisposedException>(() => reader.ReadChars(0));
        Assert.Throws<ObjectDisposedException>(() => reader.Read(bytes.AsSpan()));
        Assert.Throws<ObjectDisposedException>(() => reader.Read(Span<byte>.Empty));
        Assert.Throws<ObjectDisposedException>(() => reader.Read(chars.AsSpan()));
        Assert.Throws<ObjectDisposedException>(() => reader.Read(Span<char>.Empty));
        Assert.Throws<ObjectDisposedException>(() => reader.Read(bytes, 0, bytes.Length));
        Assert.Throws<ObjectDisposedException>(() => reader.Read(chars, 0, chars.Length));
        Assert.Throws<ObjectDisposedException>(() => reader.Read(chars, 0, 0));
        Assert.Throws<ObjectDisposedException>(() => reader.ReadExactly(Span<byte>.Empty));
        Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
        Assert.Throws<ObjectDisposedException>(() => stream.Read(bytes.AsSpan()));
        Assert.Throws<ObjectDisposedException>(() => stream.Read(bytes, 0, bytes.Length));
        Assert.Throws<ObjectDisposedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<ObjectDisposedException>(() => stream.Position);
        Assert.Throws<ObjectDisposedException>(() => { stream.Position = 0; });
        Assert.Throws<ObjectDisposedException>(() => stream.Length);
        Assert.Throws<ObjectDisposedException>(() => stream.Flush());
        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.False(stream.CanTimeout);
        stream.ReadExactly(Span<byte>.Empty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReturnedSampleReaderReleasesPayload(bool fail)
    {
        (BinaryReader reader, WeakReference<byte[]> payload) = Decode(fail);

        // Collect after the decoding frame has exited. Keeping the escaped reader alive roots the stream too.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        bool stillHeld = payload.TryGetTarget(out _);
        GC.KeepAlive(reader);

        Assert.False(stillHeld);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (BinaryReader Reader, WeakReference<byte[]> Payload) Decode(bool fail)
    {
        BinaryReader? kept = null;
        var registry = new ReplicationRegistry();
        registry.Register<Sample>(16, static (value, writer) => writer.Write(value.Value), reader =>
        {
            kept = reader;
            byte value = reader.ReadByte();
            if (fail) reader.ReadInt32();
            return new Sample(value);
        }, discreteSample: true);
        Assert.True(registry.TryGet(16, out ComponentCodec codec));
        var world = new World();
        Entity entity = world.Spawn();
        byte[] bytes = [37];
        if (fail)
        {
            Assert.Throws<EndOfStreamException>(() => codec.SetFromBytes!(world, entity, bytes));
        }
        else
        {
            codec.SetFromBytes!(world, entity, bytes);
            Assert.Equal(new Sample(37), world.Get<Sample>(entity));
        }

        Assert.NotNull(kept);
        return (kept, new WeakReference<byte[]>(bytes));
    }

    private readonly record struct Sample(byte Value) : IComponent;
}
