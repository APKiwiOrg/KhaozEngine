using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

public class ReplicationCaptureReentrancyTests
{
    private readonly record struct Payload(int Value) : IComponent;

    private static readonly (long Id, int Value)[] Outer = [(11, 101), (12, 102)];
    private static readonly (long Id, int Value)[] Inner = [(21, 201), (22, 202)];
    private const int LastOuterPayload = 102;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlatCaptureInterruptedByResetDoesNotPublish(bool captureReplacement)
    {
        Action? callback = null;
        var writer = new ServerReplicator(Registry(_ => callback?.Invoke()));
        World retired = NewWorld((11, 11));
        World replacement = NewWorld((22, 22));
        var empty = new World();
        writer.SeedLegacySequenceForTest(int.MaxValue - 1);
        callback = () =>
        {
            callback = null;
            Assert.Equal(int.MaxValue, writer.Capture(empty));
            writer.ResetAfterLegacySequenceExhaustion();
            if (captureReplacement) Assert.Equal(1, writer.Capture(replacement));
        };

        Assert.Throws<InvalidOperationException>(() => writer.Capture(retired));

        Assert.Equal(captureReplacement ? 1 : 0, writer.CurrentSeq);
        Assert.Equal(captureReplacement ? 1 : 0, writer.LegacyHistoryCount);
        if (!captureReplacement)
        {
            Assert.Throws<InvalidOperationException>(() => writer.WriteFor(0));
            Assert.Equal(1, writer.Capture(replacement));
        }
        Assert.Equal(ExpectedBody(1, null, (22, 22)), writer.WriteFor(0));
    }

    [Fact]
    public void FlatCaptureThatLosesTheLastSequenceToANestedCaptureDoesNotPublish()
    {
        Action? callback = null;
        var writer = new ServerReplicator(Registry(_ => callback?.Invoke()));
        World outer = NewWorld((11, 11));
        World inner = NewWorld((22, 22));
        writer.SeedLegacySequenceForTest(int.MaxValue - 1);
        callback = () =>
        {
            callback = null;
            Assert.Equal(int.MaxValue, writer.Capture(inner));
        };

        Assert.Throws<InvalidOperationException>(() => writer.Capture(outer));

        Assert.Equal(int.MaxValue, writer.CurrentSeq);
        Assert.True(writer.LegacySequenceExhausted);
        Assert.Equal(ExpectedBody(int.MaxValue, null, (22, 22)), writer.WriteFor(0));
    }

    [Fact]
    public void NestedFlatCaptureKeepsBothBodiesIntact()
    {
        Action? captureNested = null;
        var writer = new ServerReplicator(Registry(value =>
        {
            if (value == LastOuterPayload) captureNested?.Invoke();
        }));
        World outerWorld = NewWorld(Outer);
        World innerWorld = NewWorld(Inner);
        byte[]? innerBody = null;
        captureNested = () =>
        {
            Assert.Equal(1, writer.Capture(innerWorld));
            innerBody = writer.WriteFor(1);
        };

        Assert.Equal(2, writer.Capture(outerWorld));
        byte[] outerBody = writer.WriteFor(0);

        Assert.Equal(ExpectedBody(1, null, Inner), Assert.IsType<byte[]>(innerBody));
        Assert.Equal(ExpectedBody(2, null, Outer), outerBody);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NestedAoiCaptureKeepsBothBodiesIntact(bool outerRebuild, bool innerRebuild)
    {
        Action? captureNested = null;
        var writer = new AoiDeltaReplicator(Registry(value =>
        {
            if (value == LastOuterPayload) captureNested?.Invoke();
        }));
        World outerWorld = NewWorld(Outer);
        World innerWorld = NewWorld(Inner);
        var outerInterest = new HashSet<long> { 11, 12 };
        var innerInterest = new HashSet<long> { 21, 22 };
        if (outerRebuild) writer.StartRebuild(0, 7, new DeltaRebuildOptions());
        if (innerRebuild) writer.StartRebuild(1, 9, new DeltaRebuildOptions());
        byte[]? innerBody = null;
        captureNested = () => innerBody = Build(innerRebuild, 1, innerWorld, innerInterest);

        writer.BeginTick();
        byte[] outerBody = Build(outerRebuild, 0, outerWorld, outerInterest);

        Assert.Equal(2, writer.WorldScanCount);
        Assert.Equal(ExpectedBody(1, innerRebuild ? 9ul : null, Inner), Assert.IsType<byte[]>(innerBody));
        Assert.Equal(ExpectedBody(1, outerRebuild ? 7ul : null, Outer), outerBody);

        byte[] Build(bool rebuild, int slot, World world, HashSet<long> interest) =>
            rebuild ? writer.BuildRebuildFor(slot, world, interest).Bytes.ToArray() : writer.WriteFor(slot, world, interest);
    }

    private static ReplicationRegistry Registry(Action<int> beforeWrite)
    {
        var registry = new ReplicationRegistry();
        registry.Register<Payload>(ReplicationRegistry.FirstExtensionTypeId, (payload, writer) =>
        {
            beforeWrite(payload.Value);
            writer.Write(payload.Value);
        }, static reader => new Payload(reader.ReadInt32()));
        return registry;
    }

    private static World NewWorld(params (long Id, int Value)[] entities)
    {
        var world = new World();
        foreach ((long id, int value) in entities)
        {
            Entity entity = world.Spawn();
            world.Set(entity, new NetId(id));
            world.Set(entity, new Payload(value));
        }
        return world;
    }

    // Write the documented wire layout independently of either replication encoder.
    private static byte[] ExpectedBody(int sequence, ulong? epoch, params (long Id, int Value)[] entities)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        if (epoch is ulong epochNumber)
        {
            writer.Write((byte)2);
            writer.Write((byte)1); // Keyframe.
            writer.Write(epochNumber);
            writer.Write((uint)sequence);
            writer.Write(0u);
        }
        else
        {
            writer.Write(-1);
            writer.Write(sequence);
        }
        writer.Write(0); // Removed entities.
        writer.Write(entities.Length);
        foreach ((long id, int value) in entities)
        {
            writer.Write(id);
            writer.Write(true); // Full entry.
            writer.Write(0); // Removed components.
            writer.Write(ReplicationRegistry.FirstExtensionTypeId);
            writer.Write7BitEncodedInt(sizeof(int));
            writer.Write(value);
            writer.Write((ushort)0);
        }
        writer.Flush();
        return stream.ToArray();
    }
}
