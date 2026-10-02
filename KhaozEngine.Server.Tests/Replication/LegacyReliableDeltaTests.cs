using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// The legacy reliable contract (#1229): both writers diff each payload from the exact viewer projection they last
/// returned for the slot, because a reliable-ordered receiver applies every returned payload in order. An
/// acknowledgement that has not arrived yet must not make a later payload diff from an older state, or a value that
/// reverts inside the ack window stays at its middle value. Every case uses the unchanged legacy
/// <see cref="ClientReplicationView"/> and acknowledges only the first send.
/// </summary>
public class LegacyReliableDeltaTests
{
    private struct Value : IComponent { public int Number; }

    private struct Tag : IComponent { }   // zero-byte payload, presence is the state

    private struct Flags : IComponent { public byte Bits; }

    private struct Extra : IComponent { public int V; }

    private struct Secret : IComponent { public int V; }   // owner-only extension

    private const ushort SecretId = ReplicationRegistry.FirstExtensionTypeId;

    public static TheoryData<string> Writers => new()
    {
        nameof(LegacyWriterFixture.WholeWorld),
        nameof(LegacyWriterFixture.Aoi),
    };

    private static ReplicationRegistry NewRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<Value>(1, (v, bw) => bw.Write(v.Number), br => new Value { Number = br.ReadInt32() });
        r.Register<Tag>(2, (_, _) => { }, _ => default);
        r.Register<Flags>(3, (f, bw) => bw.Write(f.Bits), br => new Flags { Bits = br.ReadByte() });
        r.Register<Extra>(4, (x, bw) => bw.Write(x.V), br => new Extra { V = br.ReadInt32() });
        r.Register<Secret>(SecretId, (s, bw) => bw.Write(s.V), br => new Secret { V = br.ReadInt32() },
            channels: ReplicationChannels.Replicate | ReplicationChannels.OwnerOnly);
        return r;
    }

    private static HashSet<long> Interest(params long[] ids) => new(ids);

    private static Entity SpawnValue(World world, long netId, int number)
    {
        Entity e = world.Spawn();
        world.Set(e, new NetId(netId));
        world.Set(e, new Value { Number = number });
        return e;
    }

    private static byte[] ServeAndApply(LegacyWriterFixture f, IReadOnlySet<long> interest, long? owner = null)
    {
        byte[] payload = f.Serve(0, interest, owner);
        f.Apply(payload);
        return payload;
    }

    // Serves S and acknowledges it, then serves S+1 and S+2 without acknowledging either, applying all three in order.
    // Returns the S+2 payload after asserting that it names S+1, the last sent projection, as its baseline.
    private static byte[] SendThreeAckingOnlyTheFirst(LegacyWriterFixture f,
        IReadOnlySet<long> first, Action toSecond, IReadOnlySet<long> second, Action toThird, IReadOnlySet<long> third)
    {
        int s = f.CaptureNext();
        ServeAndApply(f, first);
        f.Ack(0, s);
        toSecond();
        int secondSentSequence = f.CaptureNext();
        ServeAndApply(f, second);
        toThird();
        int thirdSequence = f.CaptureNext();
        byte[] thirdPayload = ServeAndApply(f, third);
        LegacyDeltaHeader header = LegacyDeltaWire.ReadHeader(thirdPayload);
        Assert.Equal(secondSentSequence, header.Baseline);
        Assert.Equal(thirdSequence, header.Snapshot);
        return thirdPayload;
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public void ReversionWithoutAckRestoresOriginalValue(string kind)
    {
        LegacyWriterFixture fixture = LegacyWriterFixture.Create(kind, NewRegistry());
        World clientWorld = fixture.ClientWorld;
        Entity server = SpawnValue(fixture.ServerWorld, 1, 1);
        HashSet<long> interest = Interest(1);

        int s = fixture.CaptureNext();
        ServeAndApply(fixture, interest);
        fixture.Ack(0, s);

        fixture.ServerWorld.Set(server, new Value { Number = 2 });
        int secondSentSequence = fixture.CaptureNext();
        ServeAndApply(fixture, interest);
        Assert.True(fixture.View.TryGetEntity(1, out Entity clientEntity));
        Assert.Equal(2, clientWorld.Get<Value>(clientEntity).Number);

        fixture.ServerWorld.Set(server, new Value { Number = 1 });
        fixture.CaptureNext();
        byte[] thirdPayload = ServeAndApply(fixture, interest);

        // Against an ack-relative writer S+2 diffs from S, carries no change, and the client stays at 2.
        Assert.Equal(1, clientWorld.Get<Value>(clientEntity).Number);
        Assert.Equal(secondSentSequence, LegacyDeltaWire.ReadHeader(thirdPayload).Baseline);
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public void FlagsTurnedOnThenOffWithoutAckEndOff(string kind)
    {
        LegacyWriterFixture f = LegacyWriterFixture.Create(kind, NewRegistry());
        Entity server = SpawnValue(f.ServerWorld, 1, 1);
        f.ServerWorld.Set(server, new Flags { Bits = 0 });
        HashSet<long> interest = Interest(1);

        SendThreeAckingOnlyTheFirst(f,
            interest, () => f.ServerWorld.Set(server, new Flags { Bits = 0b101 }),
            interest, () => f.ServerWorld.Set(server, new Flags { Bits = 0 }),
            interest);

        Assert.True(f.View.TryGetEntity(1, out Entity clientEntity));
        Assert.Equal(0, f.ClientWorld.Get<Flags>(clientEntity).Bits);
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public void ZeroByteTagOnThenOffWithoutAckEndsAbsent(string kind)
    {
        LegacyWriterFixture f = LegacyWriterFixture.Create(kind, NewRegistry());
        Entity server = SpawnValue(f.ServerWorld, 1, 1);
        HashSet<long> interest = Interest(1);

        SendThreeAckingOnlyTheFirst(f,
            interest, () => f.ServerWorld.Set(server, new Tag()),
            interest, () => f.ServerWorld.Remove<Tag>(server),
            interest);

        Assert.True(f.View.TryGetEntity(1, out Entity clientEntity));
        Assert.False(f.ClientWorld.TryGet<Tag>(clientEntity, out _));
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public void ZeroByteTagOffThenOnWithoutAckEndsPresent(string kind)
    {
        LegacyWriterFixture f = LegacyWriterFixture.Create(kind, NewRegistry());
        Entity server = SpawnValue(f.ServerWorld, 1, 1);
        f.ServerWorld.Set(server, new Tag());
        HashSet<long> interest = Interest(1);

        SendThreeAckingOnlyTheFirst(f,
            interest, () => f.ServerWorld.Remove<Tag>(server),
            interest, () => f.ServerWorld.Set(server, new Tag()),
            interest);

        Assert.True(f.View.TryGetEntity(1, out Entity clientEntity));
        Assert.True(f.ClientWorld.TryGet<Tag>(clientEntity, out _));
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public void ComponentRemovedThenReaddedWithOriginalBytesIsPresent(string kind)
    {
        LegacyWriterFixture f = LegacyWriterFixture.Create(kind, NewRegistry());
        Entity server = SpawnValue(f.ServerWorld, 1, 1);
        f.ServerWorld.Set(server, new Extra { V = 7 });
        HashSet<long> interest = Interest(1);

        SendThreeAckingOnlyTheFirst(f,
            interest, () => f.ServerWorld.Remove<Extra>(server),
            interest, () => f.ServerWorld.Set(server, new Extra { V = 7 }),
            interest);

        Assert.True(f.View.TryGetEntity(1, out Entity clientEntity));
        Assert.True(f.ClientWorld.TryGet(clientEntity, out Extra extra));
        Assert.Equal(7, extra.V);
    }

    [Theory]
    [MemberData(nameof(Writers))]
    public void ComponentAddedThenRemovedWithoutAckIsAbsent(string kind)
    {
        LegacyWriterFixture f = LegacyWriterFixture.Create(kind, NewRegistry());
        Entity server = SpawnValue(f.ServerWorld, 1, 1);
        HashSet<long> interest = Interest(1);

        SendThreeAckingOnlyTheFirst(f,
            interest, () => f.ServerWorld.Set(server, new Extra { V = 7 }),
            interest, () => f.ServerWorld.Remove<Extra>(server),
            interest);

        Assert.True(f.View.TryGetEntity(1, out Entity clientEntity));
        Assert.False(f.ClientWorld.TryGet<Extra>(clientEntity, out _));
        Assert.Equal(1, f.ClientWorld.Get<Value>(clientEntity).Number);
    }

    // Whole-world: the entity spawns and despawns. AOI: it is always alive and enters and leaves interest.
    [Theory]
    [MemberData(nameof(Writers))]
    public void EntityThatEntersAndLeavesWithoutAckIsGone(string kind)
    {
        LegacyWriterFixture f = LegacyWriterFixture.Create(kind, NewRegistry());
        const long intermediateOnlyNetId = 2;
        SpawnValue(f.ServerWorld, 1, 1);
        bool aoi = kind == nameof(LegacyWriterFixture.Aoi);
        Entity visitor = aoi ? SpawnValue(f.ServerWorld, intermediateOnlyNetId, 5) : default;

        SendThreeAckingOnlyTheFirst(f,
            Interest(1), () => { if (!aoi) visitor = SpawnValue(f.ServerWorld, intermediateOnlyNetId, 5); },
            Interest(1, intermediateOnlyNetId), () => { if (!aoi) f.ServerWorld.Despawn(visitor); },
            Interest(1));

        Assert.False(f.View.TryGetEntity(intermediateOnlyNetId, out _));
        Assert.True(f.View.TryGetEntity(1, out _));
    }

    // Whole-world: the entity despawns, then a new server entity with the same net id spawns. AOI: it leaves and
    // re-enters interest. Either way the receiver must hold the whole entity again, not a missing one.
    [Theory]
    [MemberData(nameof(Writers))]
    public void EntityThatLeavesAndReentersWithoutAckIsWhole(string kind)
    {
        LegacyWriterFixture f = LegacyWriterFixture.Create(kind, NewRegistry());
        SpawnValue(f.ServerWorld, 1, 1);
        Entity returning = SpawnValue(f.ServerWorld, 2, 5);
        f.ServerWorld.Set(returning, new Extra { V = 9 });
        bool aoi = kind == nameof(LegacyWriterFixture.Aoi);

        SendThreeAckingOnlyTheFirst(f,
            Interest(1, 2), () => { if (!aoi) f.ServerWorld.Despawn(returning); },
            Interest(1), () =>
            {
                if (aoi) return;
                returning = SpawnValue(f.ServerWorld, 2, 5);
                f.ServerWorld.Set(returning, new Extra { V = 9 });
            },
            Interest(1, 2));

        Assert.True(f.View.TryGetEntity(2, out Entity clientEntity));
        Assert.Equal(5, f.ClientWorld.Get<Value>(clientEntity).Number);
        Assert.True(f.ClientWorld.TryGet(clientEntity, out Extra extra));
        Assert.Equal(9, extra.V);
    }

    // Distinctive owner-only values, so a payload scan cannot match an unrelated field by accident.
    private const int FirstSecret = 0x5EC1_A701;
    private const int SecondSecret = 0x5EC2_B802;

    [Theory]
    [MemberData(nameof(Writers))]
    public void StoredOwnerProjectionIsNotReprojectedAfterOwnerChange(string kind)
    {
        LegacyWriterFixture f = LegacyWriterFixture.Create(kind, NewRegistry());
        Entity first = SpawnValue(f.ServerWorld, 1, 1);
        f.ServerWorld.Set(first, new Secret { V = FirstSecret });
        Entity second = SpawnValue(f.ServerWorld, 2, 2);
        f.ServerWorld.Set(second, new Secret { V = SecondSecret });
        HashSet<long> interest = Interest(1, 2);

        int s = f.CaptureNext();
        byte[] toFirstOwner = ServeAndApply(f, interest, owner: 1);
        f.Ack(0, s);
        Assert.True(Contains(toFirstOwner, FirstSecret));
        Assert.False(Contains(toFirstOwner, SecondSecret));
        Assert.True(f.View.TryGetEntity(1, out Entity client1));
        Assert.True(f.View.TryGetEntity(2, out Entity client2));
        Assert.Equal(FirstSecret, f.ClientWorld.Get<Secret>(client1).V);
        Assert.False(f.ClientWorld.TryGet<Secret>(client2, out _));

        // The slot's owner changes. The stored projection holds entity 1's secret, so the next payload must remove it
        // explicitly and add entity 2's, rather than re-project the old state under the new owner and send nothing.
        int next = f.CaptureNext();
        byte[] toSecondOwner = ServeAndApply(f, interest, owner: 2);

        Assert.Equal(s, LegacyDeltaWire.ReadHeader(toSecondOwner).Baseline);
        Assert.Equal(next, LegacyDeltaWire.ReadHeader(toSecondOwner).Snapshot);
        Assert.False(Contains(toSecondOwner, FirstSecret));
        Assert.True(Contains(toSecondOwner, SecondSecret));
        Assert.False(f.ClientWorld.TryGet<Secret>(client1, out _));
        Assert.Equal(SecondSecret, f.ClientWorld.Get<Secret>(client2).V);
    }

    // Forget starts the slot over with a full payload. A full legacy entity does not remove a component the receiver
    // already holds, so the full payload is valid only for a fresh receiver world and view. Reusing the old receiver
    // world after Forget is unsupported.
    [Theory]
    [MemberData(nameof(Writers))]
    public void ForgetStartsFullOnlyWithFreshReceiver(string kind)
    {
        LegacyWriterFixture f = LegacyWriterFixture.Create(kind, NewRegistry());
        Entity server = SpawnValue(f.ServerWorld, 1, 1);
        HashSet<long> interest = Interest(1);

        int s = f.CaptureNext();
        ServeAndApply(f, interest);
        f.Ack(0, s);
        f.ServerWorld.Set(server, new Value { Number = 2 });
        f.CaptureNext();
        Assert.Equal(s, LegacyDeltaWire.ReadHeader(ServeAndApply(f, interest)).Baseline);
        Assert.Equal(1, f.SlotCount);

        int before = f.CurrentSeq;
        f.Forget(0);
        Assert.Equal(0, f.SlotCount);
        Assert.Equal(before, f.CurrentSeq);

        f.NewReceiver();
        f.ServerWorld.Set(server, new Value { Number = 3 });
        int fresh = f.CaptureNext();
        byte[] full = ServeAndApply(f, interest);

        LegacyDeltaHeader header = LegacyDeltaWire.ReadHeader(full);
        Assert.Equal(-1, header.Baseline);
        Assert.Equal(fresh, header.Snapshot);
        Assert.True(f.View.TryGetEntity(1, out Entity clientEntity));
        Assert.Equal(3, f.ClientWorld.Get<Value>(clientEntity).Number);
    }

    private static bool Contains(byte[] payload, int value)
    {
        Span<byte> pattern = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(pattern, value);
        return payload.AsSpan().IndexOf(pattern) >= 0;
    }
}
