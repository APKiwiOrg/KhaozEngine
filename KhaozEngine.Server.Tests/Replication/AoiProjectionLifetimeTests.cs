using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

public class AoiProjectionLifetimeTests
{
    private readonly record struct Secret(int Value) : IComponent;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SupersededOrFailedProjectionDoesNotRetainItsCapture(bool fail)
    {
        (AoiDeltaReplicator writer, World world, HashSet<long> interest) = NewSource();
        writer.BeginTick();
        writer.WriteFor(0, world, interest);
        writer.BeginTick();
        if (fail)
        {
            var fault = new IOException();
            writer.FailNextLegacyBodyCompletionForTests(fault);
            Assert.Throws<IOException>(() => writer.WriteFor(0, world, interest));
        }
        else writer.WriteFor(0, world, interest);
        var old = writer.CaptureReferencesForTests(world, 1);
        Assert.NotNull(old.Public);

        writer.BeginTick();
        // Advance the capture cache. In the failure case, slot 0 still holds the tick 1 baseline.
        writer.WriteFor(fail ? 1 : 0, world, new HashSet<long>());

        Collect();
        Assert.False(Alive(old.Source));
        Assert.False(Alive(old.Public));
        Assert.False(Alive(old.Payload));
        GC.KeepAlive(writer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SequenceResetReleasesTheLastCaptureAfterProjectionExits(bool failDuringSelection)
    {
        (AoiDeltaReplicator writer, World world, HashSet<long> interest) = NewSource();
        writer.SeedLegacySequenceForTest(int.MaxValue - 1);
        writer.BeginTick();
        if (failDuringSelection)
        {
            var fault = new IOException();
            var failing = new CallbackInterest(interest, () => throw fault);
            Assert.Same(fault, Record.Exception(() => writer.WriteFor(0, world, failing)));
        }
        else writer.WriteFor(0, world, interest);
        var old = writer.CaptureReferencesForTests(world, 1);

        writer.ResetAfterLegacySequenceExhaustion();

        Collect();
        Assert.False(Alive(old.Source));
        Assert.False(Alive(old.Public));
        Assert.False(Alive(old.Payload));
        byte[] restarted = WriteNext(writer, 0, world, interest);
        using var reader = new BinaryReader(new MemoryStream(restarted));
        Assert.Equal(-1, reader.ReadInt32());
        Assert.Equal(1, reader.ReadInt32());
    }

    [Fact]
    public void ForgetReleasesAnIdleSlotsLastCaptureAfterTheCacheMovesOn()
    {
        (AoiDeltaReplicator writer, World world, HashSet<long> interest) = NewSource();
        WriteNext(writer, 0, world, interest);
        var old = writer.CaptureReferencesForTests(world, 1);
        Assert.NotNull(old.Public);
        WriteNext(writer, 1, world, interest);

        Collect();
        Assert.True(Alive(old.Public));
        Assert.True(Alive(old.Payload));

        writer.Forget(0);

        Collect();
        Assert.False(Alive(old.Public));
        Assert.False(Alive(old.Payload));
        GC.KeepAlive(writer);
    }

    [Fact]
    public void ReentrantSameSlotSelectionCommitsBothCompleteProjections()
    {
        var world = new World();
        foreach (long id in new long[] { 1, 2, 3 }) world.Set(world.Spawn(), new NetId(id));
        var writer = new AoiDeltaReplicator(new ReplicationRegistry());
        WriteNext(writer, 0, world, [1]);
        WriteNext(writer, 0, world, [1]);

        byte[]? nested = null;
        var outerView = new CallbackInterest([3, 1], () => nested = writer.WriteFor(0, world, new HashSet<long> { 2 }));
        writer.BeginTick();
        byte[] outer = writer.WriteFor(0, world, outerView);

        AssertBody(outer, 3, [2], [1, 3]);
        WriteNext(writer, 0, world, [2]);
        AssertBody(Assert.IsType<byte[]>(nested), 2, [1], [2]);
    }

    private static void AssertBody(byte[] body, int baseline, long[] removed, long[] added)
    {
        using var reader = new BinaryReader(new MemoryStream(body));
        Assert.Equal(baseline, reader.ReadInt32());
        Assert.Equal(3, reader.ReadInt32());
        Assert.Equal(removed.Length, reader.ReadInt32());
        foreach (long id in removed) Assert.Equal(id, reader.ReadInt64());
        Assert.Equal(added.Length, reader.ReadInt32());
        foreach (long id in added)
        {
            Assert.Equal(id, reader.ReadInt64());
            Assert.True(reader.ReadBoolean());
            Assert.Equal(0, reader.ReadInt32());
            Assert.Equal((ushort)0, reader.ReadUInt16());
        }
        Assert.Equal(body.Length, reader.BaseStream.Position);
    }

    private static (AoiDeltaReplicator Writer, World World, HashSet<long> Interest) NewSource()
    {
        var registry = new ReplicationRegistry();
        registry.Register<Secret>(ReplicationRegistry.FirstExtensionTypeId, static (s, w) => w.Write(s.Value),
            static r => new Secret(r.ReadInt32()), channels: ReplicationChannels.Replicate | ReplicationChannels.OwnerOnly);
        var world = new World();
        Entity entity = world.Spawn();
        world.Set(entity, new NetId(1));
        world.Set(entity, new Secret(138));
        return (new AoiDeltaReplicator(registry), world, [1]);
    }

    private static byte[] WriteNext(AoiDeltaReplicator writer, int slot, World world, HashSet<long> interest)
    {
        writer.BeginTick();
        return writer.WriteFor(slot, world, interest);
    }

    private static void Collect() => GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Alive<T>(WeakReference<T>? reference) where T : class =>
        reference is not null && reference.TryGetTarget(out _);

    // A derived set may supply its own interface enumerator. Reenter after the first captured entity is selected.
    private sealed class CallbackInterest(IEnumerable<long> ids, Action callback) : HashSet<long>(ids), IReadOnlySet<long>
    {
        IEnumerator<long> IEnumerable<long>.GetEnumerator()
        {
            bool called = false;
            foreach (long id in this)
            {
                yield return id;
                if (called) continue;
                called = true;
                callback();
            }
        }
    }
}
