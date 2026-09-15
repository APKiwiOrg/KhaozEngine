using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>The footprint size rides TileMoveState and its wire codec. A one-tile body keeps the exact bytes it always
/// had, and a larger one always writes the domain slot so its size byte sits at a fixed offset behind it.</summary>
public class TileFootprintWireTests
{
    [Fact]
    public void A_default_state_is_one_tile()
    {
        Assert.Equal(1, default(TileMoveState).FootprintSize);
        Assert.Equal(new TileRect(3, 4, 1, 1), TileMoveState.At(new TileCoord(3, 4, 0), TileDirection.S).Footprint);
    }

    [Fact]
    public void The_footprint_is_the_size_square_north_and_east_of_the_tile()
    {
        TileMoveState s = TileMoveState.At(new TileCoord(3, 4, 0), TileDirection.S);
        s.FootprintSize = 3;
        Assert.Equal(3, s.FootprintSize);
        Assert.Equal(new TileRect(3, 4, 3, 3), s.Footprint);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void The_setter_refuses_a_size_outside_one_to_eight(int n)
    {
        var s = default(TileMoveState);
        Assert.Throws<ArgumentOutOfRangeException>(() => s.FootprintSize = n);
        Assert.Equal(1, s.FootprintSize);
    }

    [Fact]
    public void Equality_normalizes_an_unset_size_to_one()
    {
        TileMoveState unset = TileMoveState.At(new TileCoord(5, 6, 0), TileDirection.N);
        TileMoveState one = unset;
        one.FootprintSize = 1;
        Assert.Equal(unset, one);
        Assert.True(unset == one);
        Assert.Equal(unset.GetHashCode(), one.GetHashCode());

        TileMoveState two = unset;
        two.FootprintSize = 2;
        Assert.NotEqual(unset, two);
        Assert.True(unset != two);
        Assert.False(one.Equals(two));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(8)]
    public void The_size_round_trips_through_the_codec(int n)
    {
        foreach (TileMoveState original in new[] { Sample(n, entityInteraction: true), Sample(n, entityInteraction: false) })
        {
            Assert.True(original.Route.IsIdle);
            TileMoveState decoded = Decode(Encode(original));
            Assert.Equal(n, decoded.FootprintSize);
            Assert.Equal(original.InteractDomain, decoded.InteractDomain);
            Assert.Equal(original, decoded);
        }
    }

    [Fact]
    public void A_one_tile_state_encodes_to_the_legacy_byte_count()
    {
        Assert.Equal(41, Encode(Sample(1, entityInteraction: false)).Length);
        Assert.Equal(42, Encode(Sample(1, entityInteraction: true)).Length);
    }

    [Fact]
    public void A_large_state_writes_a_zero_domain_placeholder_then_the_size()
    {
        TileMoveState large = Sample(2, entityInteraction: false);
        byte[] payload = Encode(large);
        Assert.Equal(43, payload.Length);
        Assert.Equal(0, payload[41]);
        Assert.Equal(2, payload[42]);

        TileMoveState decoded = Decode(payload);
        Assert.Equal(TileInteractionDomain.AuthoredObject, decoded.InteractDomain);
        Assert.Equal(large.InteractTarget, decoded.InteractTarget);
        Assert.Equal(2, decoded.FootprintSize);
    }

    [Fact]
    public void A_hostile_size_byte_is_clamped()
    {
        byte[] payload = Encode(Sample(2, entityInteraction: false));
        Assert.Equal(43, payload.Length);

        payload[42] = 0;
        Assert.Equal(1, Decode(payload).FootprintSize);

        payload[42] = 200;
        int clamped = Decode(payload).FootprintSize;
        Assert.Equal(8, clamped);
        Assert.Equal(TileMoveState.MaxFootprintSize, clamped);
    }

    [Fact]
    public void SetPlayerState_refuses_a_footprint_above_one()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = TileWorldServerTickTests.Server(
            TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(10, 10, 0));
        s.SpawnPlayer(slot: 0, accountId: "a", displayName: "Ari");

        TileMoveState large = TileMoveState.At(new TileCoord(12, 12, 0), TileDirection.S);
        large.FootprintSize = 2;
        Assert.Throws<ArgumentException>(() => s.SetPlayerState(0, large));
        Assert.Throws<ArgumentException>(() => s.SetPlayerState(0, large, teleport: true));

        Assert.True(s.TryGetPlayerState(0, out TileMoveState stored));
        Assert.Equal(new TileCoord(10, 10, 0), stored.Tile);
        Assert.Equal(1, stored.FootprintSize);

        // The same placement at one tile is accepted, so the refusal above was the size and nothing else.
        large.FootprintSize = 1;
        s.SetPlayerState(0, large);
        Assert.True(s.TryGetPlayerState(0, out stored));
        Assert.Equal(new TileCoord(12, 12, 0), stored.Tile);
    }

    // A state with a step in flight and every codec field off its default except the combat target, which the
    // simulator never holds beside an interaction. The route is left idle because the route never rides this
    // component.
    static TileMoveState Sample(int size, bool entityInteraction)
    {
        TileMoveState s = TileMoveState.At(new TileCoord(5, 6, 1), TileDirection.N);
        s.StepFrom = new TileCoord(5, 5, 1);
        s.Mode = TileMoveMode.Run;
        s.StepTicks = 1;
        s.StepTotal = 3;
        s.Epoch = 4;
        s.InteractTarget = entityInteraction ? 7 : -7;
        s.InteractDomain = entityInteraction ? TileInteractionDomain.Entity : TileInteractionDomain.AuthoredObject;
        s.FootprintSize = size;
        return s;
    }

    // The move component's own payload, cut out of a real snapshot so it is exactly what the registry's codec wrote.
    static byte[] Encode(TileMoveState state)
    {
        var world = new World();
        Entity e = world.Spawn();
        world.Set(e, new NetId(1L));
        world.Set(e, state);
        byte[] snapshot = SnapshotWriter.WriteFiltered(world, TileProtocol.CreateRegistry(), new HashSet<long> { 1L },
            ReplicationChannels.Replicate, ownerNetId: 1L);

        using var r = new BinaryReader(new MemoryStream(snapshot));
        Assert.Equal(1, r.ReadInt32());                              // entity count
        Assert.Equal(1L, r.ReadInt64());                             // net id
        Assert.Equal(TileProtocol.TileMoveStateTypeId, r.ReadUInt16());
        int length = r.Read7BitEncodedInt();
        byte[] payload = r.ReadBytes(length);
        Assert.Equal(length, payload.Length);
        Assert.Equal((ushort)0, r.ReadUInt16());                     // end of entity: nothing else rode along
        return payload;
    }

    // Framed exactly as SnapshotWriter frames it and applied through ClientReplicationView, so the codec reads through
    // the payload-bounded reader every real decode uses, a Migrate adoption included.
    static TileMoveState Decode(byte[] payload)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(1);                                                  // entity count
        w.Write(1L);                                                 // net id
        w.Write(TileProtocol.TileMoveStateTypeId);
        w.Write7BitEncodedInt(payload.Length);
        w.Write(payload);
        w.Write((ushort)0);                                          // end of entity
        w.Flush();

        var client = new World();
        Assert.True(new ClientReplicationView(TileProtocol.CreateRegistry()).TryApply(client, ms.ToArray(), out string? error),
            error);
        return client.Get<TileMoveState>(client.Query().With<TileMoveState>().Entities().Single());
    }
}
