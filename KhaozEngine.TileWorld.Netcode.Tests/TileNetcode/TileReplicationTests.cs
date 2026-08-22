using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        ReplicationRegistry reg = TileProtocol.CreateRegistry(r =>
            r.Register<TileIdentity>(TileProtocol.FirstGameTypeId, (v, w) => w.Write(v.DisplayName ?? ""),
                br => new TileIdentity { DisplayName = br.ReadString() }));
        Assert.NotNull(reg);
        Assert.True(ReplicationRegistry.IsExtension(TileProtocol.FirstGameTypeId));
        Assert.True(TileProtocol.FirstGameTypeId > TileProtocol.TileIdentityTypeId);
    }

    [Fact]
    public void A_route_longer_than_the_cap_is_truncated_rather_than_refused()
    {
        ReplicationRegistry reg = TileProtocol.CreateRegistry();
        var steps = new TileDirection[TileProtocol.MaxRouteSteps + 10];
        for (int i = 0; i < steps.Length; i++) steps[i] = TileDirection.E;

        var server = new World();
        Entity e = server.Spawn();
        server.Set(e, new NetId(1));
        server.Set(e, TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.E));
        server.Set(e, new TileRouteState { Remaining = steps });

        var client = new World();
        new ClientReplicationView(reg).Apply(client, SnapshotWriter.WriteFiltered(server, reg,
            new HashSet<long> { 1 }, ReplicationChannels.Replicate, ownerNetId: 1));
        Entity got = client.Query().With<TileRouteState>().Entities().Single();
        Assert.Equal(TileProtocol.MaxRouteSteps, client.Get<TileRouteState>(got).Remaining!.Length);
    }

    [Fact]
    public void A_display_name_over_the_cap_is_clamped_on_the_wire()
    {
        ReplicationRegistry reg = TileProtocol.CreateRegistry();
        var server = new World();
        Entity e = server.Spawn();
        server.Set(e, new NetId(1));
        server.Set(e, TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.E));
        server.Set(e, new TileIdentity { DisplayName = new string('n', TileProtocol.MaxDisplayNameBytes + 20) });

        var client = new World();
        new ClientReplicationView(reg).Apply(client, SnapshotWriter.WriteFiltered(server, reg,
            new HashSet<long> { 1 }, ReplicationChannels.Replicate, ownerNetId: 1));
        Entity got = client.Query().With<TileIdentity>().Entities().Single();
        Assert.Equal(TileProtocol.MaxDisplayNameBytes, client.Get<TileIdentity>(got).DisplayName!.Length);
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
        // total (a divide by zero in StepFraction), an out-of-range step direction, and a route that declares far
        // more steps than the frame holds. Every one of them has to come back as a clamp, because this runs inside
        // ClientReplicationView.Apply on a client's frame loop.
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(1);                                  // entity count
        w.Write(1L);                                 // net id

        w.Write(TileProtocol.TileMoveStateTypeId);
        byte[] move = MoveBytes(5, 6, plane: 0, facing: 200, mode: 200, stepTicks: 9, stepTotal: 0);
        w.Write7BitEncodedInt(move.Length);
        w.Write(move);

        w.Write(TileProtocol.TileRouteStateTypeId);
        byte[] route = { 0xE8, 0x03, 200, 3 };       // declares 1000 steps, carries two, one of them not a direction
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
        Assert.Equal(0f, got.StepFraction);
        Assert.All(client.Get<TileRouteState>(e).Remaining!, d => Assert.Contains(d, TileDirections.All));
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
