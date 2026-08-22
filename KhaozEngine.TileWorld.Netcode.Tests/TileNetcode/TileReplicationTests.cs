using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileReplicationTests
{
    static TileMoveState Walking()
    {
        TileMoveState s = TileMoveState.At(new TileCoord(4, 9, 1), TileDirection.NE);
        s.Mode = TileMoveMode.Run;
        s.StepTotal = 2;
        s.StepTicks = 1;
        s.Epoch = 3;
        s.InteractTarget = 77;
        s.Route = new TileRoute(new[] { new TileCoord(5, 10, 1), new TileCoord(6, 10, 1) }, 0);
        return s;
    }

    static (World world, Entity e) Spawn(long netId, in TileMoveState state)
    {
        var w = new World();
        Entity e = w.Spawn();
        w.Set(e, new NetId(netId));
        w.Set(e, state);
        w.Set(e, new TileRouteState { Remaining = state.Route.RemainingSteps(state.Tile) });
        w.Set(e, new TileIdentity { DisplayName = "Ari" });
        return (w, e);
    }

    [Fact]
    public void A_state_round_trips_through_the_registry_without_its_route()
    {
        ReplicationRegistry reg = TileProtocol.CreateRegistry();
        TileMoveState sent = Walking();
        (World server, _) = Spawn(1, sent);
        byte[] snap = SnapshotWriter.WriteFiltered(server, reg, new HashSet<long> { 1 },
            ReplicationChannels.Replicate, ownerNetId: 1);

        var client = new World();
        new ClientReplicationView(reg).Apply(client, snap);
        Entity e = client.Query().With<TileMoveState>().Entities().First();
        TileMoveState got = client.Get<TileMoveState>(e);

        Assert.Equal(sent.Tile, got.Tile);
        Assert.Equal(sent.Facing, got.Facing);
        Assert.Equal(sent.Mode, got.Mode);
        Assert.Equal(sent.StepTicks, got.StepTicks);
        Assert.Equal(sent.StepTotal, got.StepTotal);
        Assert.Equal(sent.Epoch, got.Epoch);
        Assert.Equal(sent.InteractTarget, got.InteractTarget);
        Assert.Equal("Ari", client.Get<TileIdentity>(e).DisplayName);
        Assert.Equal(sent.Route, TileRoute.FromSteps(got.Tile, client.Get<TileRouteState>(e).Remaining));

        // The route rides TileRouteState, never the move state, so what lands in the component itself is standing.
        Assert.True(got.Route.IsIdle);
    }

    [Fact]
    public void The_route_reaches_its_owner_and_nobody_else()
    {
        ReplicationRegistry reg = TileProtocol.CreateRegistry();
        (World server, _) = Spawn(1, Walking());
        var interest = new HashSet<long> { 1 };

        var owner = new World();
        new ClientReplicationView(reg).Apply(owner,
            SnapshotWriter.WriteFiltered(server, reg, interest, ReplicationChannels.Replicate, ownerNetId: 1));
        var watcher = new World();
        new ClientReplicationView(reg).Apply(watcher,
            SnapshotWriter.WriteFiltered(server, reg, interest, ReplicationChannels.Replicate, ownerNetId: 2));

        Assert.Single(owner.Query().With<TileRouteState>().Entities());
        Assert.Empty(watcher.Query().With<TileRouteState>().Entities());
        Assert.Single(watcher.Query().With<TileMoveState>().Entities());
    }

    [Fact]
    public void The_route_also_rides_the_persist_and_migrate_channels()
    {
        // A basis with no route stands the player still, so a cell handoff or a restart that dropped the route
        // would resume a walking player standing on the tile the walk had reached.
        ReplicationRegistry reg = TileProtocol.CreateRegistry();
        (World server, _) = Spawn(1, Walking());
        var interest = new HashSet<long> { 1 };

        foreach (ReplicationChannels channel in new[] { ReplicationChannels.Persist, ReplicationChannels.Migrate })
        {
            var restored = new World();
            new ClientReplicationView(reg).Apply(restored,
                SnapshotWriter.WriteFiltered(server, reg, interest, channel, ownerNetId: null));
            Entity e = restored.Query().With<TileRouteState>().Entities().Single();
            Assert.Equal(new[] { TileDirection.NE, TileDirection.E }, restored.Get<TileRouteState>(e).Remaining);
        }
    }

    [Fact]
    public void The_three_component_ids_are_extension_ids_and_do_not_collide()
    {
        Assert.True(ReplicationRegistry.IsExtension(TileProtocol.TileMoveStateTypeId));
        Assert.True(ReplicationRegistry.IsExtension(TileProtocol.TileRouteStateTypeId));
        Assert.True(ReplicationRegistry.IsExtension(TileProtocol.TileIdentityTypeId));
        var ids = new HashSet<ushort>
        {
            TileProtocol.TileMoveStateTypeId, TileProtocol.TileRouteStateTypeId, TileProtocol.TileIdentityTypeId,
        };
        Assert.Equal(3, ids.Count);
    }

    [Fact]
    public void A_consumer_registers_its_own_components_above_the_engines()
    {
        // The seam under test is the callback, so it is asserted through the registry's own view of what it holds.
        // Asserting the ids and non-nullness alone passes with registerExtensions never invoked at all, which is
        // the entire thing this test is named for. A DISTINCT component type, not a second registration of an
        // engine one: the registry keys by id, so registering TileIdentity twice would write it twice per entity
        // and hand a consumer copying this test the wrong shape.
        Assert.False(TileProtocol.CreateRegistry().IsRegistered(TileProtocol.FirstGameTypeId));

        ReplicationRegistry reg = TileProtocol.CreateRegistry(r =>
            r.Register<GameHealth>(TileProtocol.FirstGameTypeId, (v, w) => w.Write(v.Value),
                br => new GameHealth { Value = br.ReadInt32() }));
        Assert.True(reg.IsRegistered(TileProtocol.FirstGameTypeId));
        Assert.True(reg.IsRegistered(TileProtocol.TileMoveStateTypeId));
        Assert.True(ReplicationRegistry.IsExtension(TileProtocol.FirstGameTypeId));
        Assert.True(TileProtocol.FirstGameTypeId > TileProtocol.TileIdentityTypeId);
    }

    /// <summary>A game's own component, standing in for one in this package's tests. Distinct from every engine
    /// component so a registration of it can only have come from the extension callback.</summary>
    struct GameHealth : IComponent
    {
        public int Value;
    }

    [Fact]
    public void A_route_at_the_cap_rides_the_wire_intact_and_a_longer_one_is_refused_by_the_encoder()
    {
        ReplicationRegistry reg = TileProtocol.CreateRegistry();
        var steps = new TileDirection[TileProtocol.MaxRouteSteps];
        for (int i = 0; i < steps.Length; i++) steps[i] = TileDirection.E;

        var client = new World();
        new ClientReplicationView(reg).Apply(client, Snapshot(reg, steps));
        Entity got = client.Query().With<TileRouteState>().Entities().Single();
        Assert.Equal(steps, client.Get<TileRouteState>(got).Remaining);

        // One step over, built by hand: REFUSED at the server's own encode rather than truncated. Truncating here
        // would move TileRoute.End, which is the destination, so the owner would be told it is walking to a tile
        // nobody routed it to, with a fresh wrong answer every snapshot. The cap lives in TileMoveSimulator, so a
        // route this long cannot come from one, and a hand-built one is a local bug worth the stack.
        var tooLong = new TileDirection[TileProtocol.MaxRouteSteps + 1];
        Assert.Throws<ArgumentException>(() => Snapshot(reg, tooLong));
    }

    static byte[] Snapshot(ReplicationRegistry reg, TileDirection[] steps)
    {
        var server = new World();
        Entity e = server.Spawn();
        server.Set(e, new NetId(1));
        server.Set(e, TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.E));
        server.Set(e, new TileRouteState { Remaining = steps });
        return SnapshotWriter.WriteFiltered(server, reg, new HashSet<long> { 1 },
            ReplicationChannels.Replicate, ownerNetId: 1);
    }

    [Fact]
    public void A_display_name_over_the_cap_is_cut_at_a_codepoint_boundary()
    {
        // 62 ASCII characters plus a 4-byte emoji is 66 UTF-8 bytes, so the 64-byte cap falls INSIDE the emoji.
        // Cut there, the receiver decodes U+FFFD and the name arrives visibly broken. An ASCII-only fixture can
        // never see this, and neither can an assertion that compares a CHAR count against a BYTE cap, which is
        // what this test used to do.
        ReplicationRegistry reg = TileProtocol.CreateRegistry();
        string straddling = new string('a', 62) + "\U0001F600";
        string ascii = new string('n', TileProtocol.MaxDisplayNameBytes + 20);

        Assert.Equal(new string('a', 62), OverTheWire(reg, straddling));
        Assert.DoesNotContain("\uFFFD", OverTheWire(reg, straddling));
        Assert.Equal(TileProtocol.MaxDisplayNameBytes,
            Encoding.UTF8.GetByteCount(OverTheWire(reg, ascii)));
        Assert.True(Encoding.UTF8.GetByteCount(OverTheWire(reg, straddling)) <= TileProtocol.MaxDisplayNameBytes);
    }

    static string OverTheWire(ReplicationRegistry reg, string displayName)
    {
        var server = new World();
        Entity e = server.Spawn();
        server.Set(e, new NetId(1));
        server.Set(e, TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.E));
        server.Set(e, new TileIdentity { DisplayName = displayName });

        var client = new World();
        new ClientReplicationView(reg).Apply(client, SnapshotWriter.WriteFiltered(server, reg,
            new HashSet<long> { 1 }, ReplicationChannels.Replicate, ownerNetId: 1));
        Entity got = client.Query().With<TileIdentity>().Entities().Single();
        return client.Get<TileIdentity>(got).DisplayName!;
    }

    [Fact]
    public void A_null_route_and_a_null_name_survive_the_wire_as_empty_rather_than_throwing()
    {
        ReplicationRegistry reg = TileProtocol.CreateRegistry();
        var server = new World();
        Entity e = server.Spawn();
        server.Set(e, new NetId(1));
        server.Set(e, TileMoveState.At(new TileCoord(2, 3, 0), TileDirection.S));
        server.Set(e, new TileRouteState { Remaining = null });
        server.Set(e, new TileIdentity { DisplayName = null });

        var client = new World();
        new ClientReplicationView(reg).Apply(client, SnapshotWriter.WriteFiltered(server, reg,
            new HashSet<long> { 1 }, ReplicationChannels.Replicate, ownerNetId: 1));
        Entity got = client.Query().With<TileRouteState>().Entities().Single();
        Assert.Empty(client.Get<TileRouteState>(got).Remaining!);
        Assert.Equal("", client.Get<TileIdentity>(got).DisplayName);
    }

    [Fact]
    public void The_registry_writes_the_same_bytes_for_the_same_state_every_time()
    {
        var interest = new HashSet<long> { 1 };
        byte[] First()
        {
            ReplicationRegistry reg = TileProtocol.CreateRegistry();
            (World server, _) = Spawn(1, Walking());
            return SnapshotWriter.WriteFiltered(server, reg, interest, ReplicationChannels.Replicate, ownerNetId: 1);
        }
        Assert.Equal(First(), First());
    }

    [Fact]
    public void A_hostile_component_payload_is_clamped_rather_than_thrown_on()
    {
        // Hand-rolled wire bytes no encoder here would ever emit: an out-of-range facing and mode, a zero step
        // total (a divide by zero in StepFraction), a step counter far past that total, and a step direction that
        // is not one. Each is a field whose whole byte range is meaningful, so each comes back CLAMPED rather than
        // refused, because this runs inside ClientReplicationView.Apply on a client's frame loop. A field that
        // declares a LENGTH is the other case and is refused instead, see the test below.
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(1);                                  // entity count
        w.Write(1L);                                 // net id

        w.Write(TileProtocol.TileMoveStateTypeId);
        byte[] move = MoveBytes(5, 6, plane: 0, facing: 200, mode: 200, stepTicks: 250, stepTotal: 2);
        w.Write7BitEncodedInt(move.Length);
        w.Write(move);

        w.Write(TileProtocol.TileRouteStateTypeId);
        byte[] route = { 2, 0, 200, 3 };             // two steps, one of them not a direction
        w.Write7BitEncodedInt(route.Length);
        w.Write(route);

        w.Write((ushort)0);                          // end of entity
        w.Flush();

        var client = new World();
        new ClientReplicationView(TileProtocol.CreateRegistry()).Apply(client, ms.ToArray());
        Entity e = client.Query().With<TileMoveState>().Entities().Single();

        TileMoveState got = client.Get<TileMoveState>(e);
        Assert.Contains(got.Facing, TileDirections.All);
        Assert.True(got.Mode is TileMoveMode.Walk or TileMoveMode.Run);
        Assert.True(got.StepTotal >= 1);
        Assert.All(client.Get<TileRouteState>(e).Remaining!, d => Assert.Contains(d, TileDirections.All));

        // StepTicks has to be asserted the way the OWNER will hold this state: with the route merged back in to
        // build a reconcile basis. A replicated move state always decodes idle, and StepFraction short-circuits on
        // an idle route, so asserting it here without the merge passes with the clamp deleted. Unclamped, 250 ticks
        // into a 2 tick step reads as a step fraction of 125 and a position 125 tiles out, straight into the
        // reconcile error and the hard-snap gate.
        Assert.True(got.StepTicks < got.StepTotal);
        got.Route = TileRoute.FromSteps(got.Tile, client.Get<TileRouteState>(e).Remaining);
        Assert.False(got.Route.IsIdle);
        Assert.InRange(got.StepFraction, 0f, 1f);
        Assert.Equal(0.5f, got.StepFraction);
    }

    [Fact]
    public void A_component_frame_that_lies_about_its_length_fails_the_apply_rather_than_reading_short()
    {
        // 21 bytes, whose route header claims 65535 steps behind the two it carries. Read as a short route it
        // answers TRUE and rebuilds a walk out of whatever bytes followed, and the clamp-and-skip path this
        // replaces allocated the 64 KB the ushort asked for while doing it, per component, per entity. Nothing
        // legitimate declares a length it does not carry, so the frame is malformed and the apply says so, which
        // is the caller's disconnect signal.
        byte[] route = OneEntity(TileProtocol.TileRouteStateTypeId, 0xFF, 0xFF, 3, 3);
        Assert.Equal(21, route.Length);
        Assert.False(new ClientReplicationView(TileProtocol.CreateRegistry())
            .TryApply(new World(), route, out string? error));
        Assert.NotNull(error);

        byte[] name = OneEntity(TileProtocol.TileIdentityTypeId, 0xFF, 0xFF, (byte)'x');
        Assert.False(new ClientReplicationView(TileProtocol.CreateRegistry())
            .TryApply(new World(), name, out _));
    }

    [Fact]
    public void An_invalid_utf8_display_name_substitutes_rather_than_throwing()
    {
        // The LENGTH is checked, the bytes are not: Encoding.UTF8.GetString substitutes U+FFFD for an invalid
        // sequence instead of throwing, which is what keeps the reader total. Pinned so a later switch to a strict
        // decoder goes red here rather than in a client's apply loop.
        var client = new World();
        Assert.True(new ClientReplicationView(TileProtocol.CreateRegistry())
            .TryApply(client, OneEntity(TileProtocol.TileIdentityTypeId, 2, 0, 0xFF, 0xFE), out _));
        Entity e = client.Query().With<TileIdentity>().Entities().Single();
        Assert.Equal("\uFFFD\uFFFD", client.Get<TileIdentity>(e).DisplayName);
    }

    // One entity carrying one extension component, framed exactly as SnapshotWriter frames it, so a payload can be
    // hand-rolled without an encoder refusing to produce it.
    static byte[] OneEntity(ushort typeId, params byte[] payload)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(1);              // entity count
        w.Write(1L);             // net id
        w.Write(typeId);
        w.Write7BitEncodedInt(payload.Length);
        w.Write(payload);
        w.Write((ushort)0);      // end of entity
        w.Flush();
        return ms.ToArray();
    }

    static byte[] MoveBytes(int x, int z, byte plane, byte facing, byte mode, byte stepTicks, byte stepTotal)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(x);
        w.Write(z);
        w.Write(plane);
        w.Write(facing);
        w.Write(mode);
        w.Write(stepTicks);
        w.Write(stepTotal);
        w.Write(1u);      // epoch
        w.Write(2L);      // interact target
        w.Flush();
        return ms.ToArray();
    }
}
