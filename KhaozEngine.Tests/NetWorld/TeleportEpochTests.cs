using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The authoritative teleport epoch: a monotonic counter the server stamps on <see cref="MovementState"/> (the
/// vertical-axis reconcile-basis built-in, wire generation 4) at teleport sites, so an in-session teleport CUTS on
/// the client regardless of distance and the client surfaces one uniform "a local teleport landed" signal. Normal
/// movement never advances it. Mirrors the swim-flag coverage (<see cref="PlayerMoveSwimTests"/>): a wire round-trip
/// plus loopback server/client behaviour on both the single-World and sharded heads.
/// </summary>
public class TeleportEpochTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    static readonly MoveTuning Unit = MoveTuning.Default with { CapsuleHalfHeight = 0.5f };
    static MoveCommand Forward => new(new Vector2(0f, 1f), run: false, cameraYaw: 0f);

    // ---- Wire round-trip of the epoch through the shared movement codec ----

    private static MovementState RoundTrip(MovementState src)
    {
        ReplicationRegistry registry = MoveProtocol.CreateRegistry();
        var serverWorld = new World();
        Entity e = serverWorld.Spawn();
        serverWorld.Set(e, new NetId(1));
        serverWorld.Set(e, new ReplicatedPosition { Value = Vector3.Zero });
        serverWorld.Set(e, src);
        byte[] snapshot = SnapshotWriter.Write(serverWorld, registry);

        var view = new ClientReplicationView(registry);
        var clientWorld = new World();
        view.Apply(clientWorld, snapshot);
        Assert.True(view.TryGetEntity(1, out Entity ce));
        Assert.True(clientWorld.TryGet(ce, out MovementState back));
        return back;
    }

    [Fact]
    public void Teleport_epoch_round_trips_on_the_wire()
    {
        MovementState back = RoundTrip(new MovementState
        {
            VerticalVelocity = -1.5f, Grounded = true, Swimming = false, TeleportEpoch = 42u,
        });
        Assert.Equal(42u, back.TeleportEpoch);
        // The other movement fields still round-trip alongside it (the epoch is an additional built-in field).
        Assert.Equal(-1.5f, back.VerticalVelocity, 5);
        Assert.True(back.Grounded);
    }

    [Fact]
    public void Wire_generation_bumped_for_the_teleport_epoch()
    {
        // The epoch added 4 bytes to a BUILT-IN codec (not length-prefixed), so it is a breaking wire change: the
        // generation gate must have advanced past the swim-flag line (3). Old-client rejection rides the always-on
        // WireGenerationAuthenticator (covered elsewhere); this pins that the generation moved forward.
        Assert.True(MoveProtocol.WireProtocolVersion >= 4);
    }

    [Fact]
    public void The_epoch_travels_through_the_From_converters()
    {
        var pms = new PlayerMoveState { Position = new Vector3(1, 2, 3), TeleportEpoch = 9u };
        MovementState ms = MovementState.From(pms);
        Assert.Equal(9u, ms.TeleportEpoch);

        PlayerMoveState rebuilt = PlayerMoveState.From(new Vector3(1, 2, 3), ms);
        Assert.Equal(9u, rebuilt.TeleportEpoch);
    }

    [Fact]
    public void The_simulator_step_preserves_the_epoch_as_a_movement_only_transform()
    {
        // The epoch is not a movement quantity: a step must carry it forward unchanged (only position/vertical change),
        // so a teleport marker set on the authoritative state survives the next per-tick sim step (single-World head).
        var sim = new PlayerMoveSimulator(Flat, Unit);
        var s0 = new PlayerMoveState { Position = new Vector3(0, 0.5f, 0), Grounded = true, TeleportEpoch = 5u };
        PlayerMoveState s1 = sim.Step(s0, Forward, 1f / 30f);
        Assert.Equal(5u, s1.TeleportEpoch);
    }

    [Fact]
    public void WithPosition_and_render_state_preserve_the_epoch()
    {
        // The rendered/derived state must not drop the epoch (the IPredictedState copies rebuild the struct).
        IPredictedState<PlayerMoveState> pms = new PlayerMoveState { Position = new Vector3(1, 2, 3), TeleportEpoch = 11u };
        Assert.Equal(11u, pms.WithPosition(new Vector2(4, 5)).TeleportEpoch);
        Assert.Equal(11u, pms.WithRenderState(new Vector2(4, 5), 6f).TeleportEpoch);
    }

    // ---- Server stamps the epoch at teleport sites; the client surfaces the signal (loopback) ----

    static (WorldServer server, WorldClient client, WorldServerConfig config) Connect()
    {
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        var server = new WorldServer(st, config, Flat, Unit);
        var client = new WorldClient(ct, Flat, Unit, new WorldClientConfig { TickSeconds = config.TickSeconds });
        for (int i = 0; i < 10 && !client.Joined; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);
        return (server, client, config);
    }

    static (ShardedWorldServer server, WorldClient client, ShardedWorldServerConfig config) ConnectSharded(ShardedWorldServerConfig config)
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var server = new ShardedWorldServer(st, config, Flat, Unit);
        var client = new WorldClient(ct, Flat, Unit, new WorldClientConfig { TickSeconds = config.TickSeconds });
        for (int i = 0; i < 30 && !client.Joined; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);
        return (server, client, config);
    }

    [Fact]
    public void Admin_teleport_advances_the_epoch_and_the_client_observes_a_local_teleport()
    {
        (WorldServer server, WorldClient client, WorldServerConfig config) = Connect();
        Assert.True(server.TryGetPlayerState(0, out PlayerMoveState before));
        uint clientEpochBefore = client.LocalTeleportEpoch;
        int fired = 0;
        client.LocalTeleported += () => fired++;

        server.Teleport(PlayerRef.Slot(0), new Vector3(120f, 0f, 60f));
        for (int i = 0; i < 5; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }

        Assert.True(server.TryGetPlayerState(0, out PlayerMoveState after));
        Assert.True(after.TeleportEpoch > before.TeleportEpoch,
            $"the server epoch must advance on an admin teleport ({before.TeleportEpoch} -> {after.TeleportEpoch})");
        Assert.True(client.LocalTeleportEpoch > clientEpochBefore, "the client must observe a local teleport");
        Assert.True(fired >= 1, "the LocalTeleported event should fire");
        Assert.Equal(120f, after.Position.X, 1);   // and it actually moved to the destination
        Assert.Equal(60f, after.Position.Z, 1);
    }

    [Fact]
    public void Normal_movement_does_not_advance_the_epoch()
    {
        (WorldServer server, WorldClient client, WorldServerConfig config) = Connect();
        Assert.True(server.TryGetPlayerState(0, out PlayerMoveState before));
        int fired = 0;
        client.LocalTeleported += () => fired++;   // subscribe AFTER the join seed so only in-session teleports count

        for (int i = 0; i < 60; i++) { client.SendInput(Forward); server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }

        Assert.True(server.TryGetPlayerState(0, out PlayerMoveState after));
        Assert.Equal(before.TeleportEpoch, after.TeleportEpoch);   // ordinary movement never bumps the epoch
        Assert.Equal(0, fired);                                    // and never fires the teleport signal
    }

    [Fact]
    public void Self_rescue_advances_the_epoch_on_the_sharded_server()
    {
        // Ruinborne runs on the sharded stack, and self-rescue funnels through the same admin Teleport path. The epoch
        // must advance and reach the client there too (the sharded head stores it in the cell world's MovementState).
        var config = new ShardedWorldServerConfig
        {
            TickSeconds = 1f / 30f, MaxPlayers = 8, SelfRescueDestination = _ => new Vector3(80f, 0f, -80f),
        };
        (ShardedWorldServer server, WorldClient client, _) = ConnectSharded(config);
        int slot = server.JoinedSlots.First();
        Assert.True(server.TryGetPlayerState(slot, out PlayerMoveState before));
        uint clientEpochBefore = client.LocalTeleportEpoch;

        Assert.True(client.RequestSelfRescue());
        for (int i = 0; i < 12; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }

        Assert.True(server.TryGetPlayerState(slot, out PlayerMoveState after));
        Assert.True(after.TeleportEpoch > before.TeleportEpoch,
            $"self-rescue must advance the epoch on the sharded server ({before.TeleportEpoch} -> {after.TeleportEpoch})");
        Assert.True(client.LocalTeleportEpoch > clientEpochBefore, "the client must observe the self-rescue teleport");
    }
}
