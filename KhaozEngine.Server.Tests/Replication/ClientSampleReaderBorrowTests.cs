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

        Assert.Throws<EndOfStreamException>(() => reader.ReadByte());
        Assert.Equal(-1, reader.BaseStream.ReadByte());
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
