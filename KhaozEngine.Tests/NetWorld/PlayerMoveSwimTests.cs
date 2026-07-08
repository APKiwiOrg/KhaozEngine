using System;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Surface swim at the netcode layer: the swim flag replicates through <see cref="MovementState"/> (the vertical-axis
/// built-in, wire generation 3), so the local owner reconciles it and remotes read it (Task 3's animation source). The
/// same medium provider on both heads means the client predicts the swim (buoyancy + swim speed) in lockstep with the
/// authoritative server, and a null provider is bit-identical to the pre-swim simulator.
/// </summary>
public class PlayerMoveSwimTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    static readonly MoveTuning Unit = MoveTuning.Default with { CapsuleHalfHeight = 0.5f };

    static Func<float, float, float, MovementMedium> Water(float surfaceY, float zoneScale = 1f)
        => (x, z, feetY) => new MovementMedium(surfaceY, inWater: true, zoneScale);

    static MoveCommand Forward => new(new Vector2(0f, 1f), run: false, cameraYaw: 0f);

    // ---- Wire round-trip of the swim flag through the shared movement codec ----

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
    public void Swim_flag_round_trips_on_the_wire()
    {
        MovementState swimming = RoundTrip(new MovementState
        {
            VerticalVelocity = -1.5f, Grounded = false, TimeSinceGrounded = 3f, JumpBufferRemaining = 0f, Swimming = true,
        });
        Assert.True(swimming.Swimming);
        Assert.False(swimming.Grounded);
        Assert.Equal(-1.5f, swimming.VerticalVelocity, 5);

        MovementState land = RoundTrip(new MovementState { Grounded = true, Swimming = false });
        Assert.False(land.Swimming);
        Assert.True(land.Grounded);
    }

    [Fact]
    public void Wire_generation_bumped_for_the_swim_flag()
    {
        // The swim flag added a byte to a BUILT-IN codec (not length-prefixed), so it is a breaking wire change: the
        // generation gate must have advanced past the 10.0.0 NetId-widening line (2). Old-client rejection rides the
        // always-on WireGenerationAuthenticator (covered by VersionHandshakeTests); this pins the generation moved.
        Assert.True(MoveProtocol.WireProtocolVersion >= 3);
    }

    // ---- Loopback prediction alignment while swimming ----

    static (WorldServer server, WorldClient client, WorldServerConfig config) Connect(
        Func<float, float, float, MovementMedium>? medium, MoveTuning tuning)
    {
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        var server = new WorldServer(st, config, Flat, tuning, medium: medium);
        var client = new WorldClient(ct, Flat, tuning,
            new WorldClientConfig { TickSeconds = config.TickSeconds }, medium: medium);
        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);
        return (server, client, config);
    }

    [Fact]
    public void Client_prediction_stays_aligned_with_the_server_while_swimming()
    {
        // Deep water everywhere (surface high above spawn): both heads enter swim, settle buoyancy, and swim forward at
        // SwimSpeed off the SAME provider. The predicted local position must track the authoritative one with no
        // rubber-band, and the authoritative state must actually BE swimming (so we exercised the swim path, not wading).
        Func<float, float, float, MovementMedium> deep = Water(3.0f);
        (WorldServer server, WorldClient client, WorldServerConfig config) = Connect(deep, Unit);

        float PlanarErr(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                client.SendInput(Forward);
                server.Poll(); server.Tick(config.TickSeconds); client.Poll();
            }
            Assert.True(server.TryGetPlayerState(0, out PlayerMoveState auth));
            PlayerMoveState pred = client.LocalRenderState;
            float dx = pred.Position.X - auth.Position.X;
            float dz = pred.Position.Z - auth.Position.Z;
            return MathF.Sqrt(dx * dx + dz * dz);
        }

        float errAt60 = PlanarErr(60);
        float errAt120 = PlanarErr(60);   // another 60 ticks

        Assert.True(server.TryGetPlayerState(0, out PlayerMoveState authoritative));
        Assert.True(authoritative.Move.Swimming, "the authoritative player should be swimming in deep water");

        // Aligned, NOT diverging: the planar error stays bounded within a small fraction of the run's travel. SwimSpeed
        // (2.5 m/s) makes one tick's travel ~0.083 m, so the fixed render-smoothing offset is a touch under two ticks;
        // the load-bearing check is that it does not GROW over a second run of ticks (prediction tracks the server).
        Assert.True(errAt60 < 0.15f, $"planar prediction error too large at 60 ticks: {errAt60}");
        Assert.True(errAt120 <= errAt60 + 1e-3f, $"prediction diverged: {errAt60} -> {errAt120}");

        // And it swam forward (into -Z), so this was a live swim, not a stalled avatar.
        Assert.True(authoritative.Position.Z < -3f, $"expected forward swim progress, got {authoritative.Position.Z}");
    }

    [Fact]
    public void Swim_flag_reaches_the_server_authoritative_state_and_replicates()
    {
        // The server-side swim flag ends up on the replicated MovementState too (the remote-visible source Task 3 reads).
        Func<float, float, float, MovementMedium> deep = Water(3.0f);
        (WorldServer server, WorldClient client, WorldServerConfig config) = Connect(deep, Unit);
        for (int i = 0; i < 40; i++)
        {
            client.SendInput(Forward);
            server.Poll(); server.Tick(config.TickSeconds); client.Poll();
        }
        Assert.True(server.TryGetPlayerState(0, out PlayerMoveState st));
        Assert.True(st.Move.Swimming);
        // The client's own reconciled render state agrees it is swimming.
        Assert.True(client.LocalRenderState.Move.Swimming);
    }

    [Fact]
    public void EntityRenderState_carries_the_swim_flag_for_the_local_player()
    {
        // Task 3's animation source: the swim bit must surface on EntityRenderState (via WorldClient.Snapshot()) so a
        // replicated-animator bridge feeds it into CharacterSample.Swimming. Local player swims deep water -> its
        // render-state entry reads Swimming; a dry-land run reads not-Swimming.
        Func<float, float, float, MovementMedium> deep = Water(3.0f);
        (WorldServer server, WorldClient client, WorldServerConfig config) = Connect(deep, Unit);
        for (int i = 0; i < 40; i++)
        {
            client.SendInput(Forward);
            server.Poll(); server.Tick(config.TickSeconds); client.Poll();
        }

        EntityRenderState local = default;
        bool found = false;
        foreach (EntityRenderState e in client.Snapshot())
            if (e.IsLocal) { local = e; found = true; }
        Assert.True(found, "the local player must be in the snapshot");
        Assert.True(local.Swimming, "the local render state should carry the swim flag while swimming");

        // A dry-land client (no medium) never surfaces Swimming.
        (WorldServer s2, WorldClient c2, WorldServerConfig cfg2) = Connect(medium: null, tuning: Unit);
        for (int i = 0; i < 20; i++)
        {
            c2.SendInput(Forward);
            s2.Poll(); s2.Tick(cfg2.TickSeconds); c2.Poll();
        }
        foreach (EntityRenderState e in c2.Snapshot())
            Assert.False(e.Swimming, "a dry-land entity never reads Swimming");
    }

    [Fact]
    public void EntityRenderState_swim_flag_defaults_false_via_the_legacy_ctors()
    {
        // The added Swimming field defaults false on the pre-swim constructors, so pre-swim callers are unchanged.
        Assert.False(new EntityRenderState(new NetId(1), Vector3.Zero, isLocal: true).Swimming);
        Assert.False(new EntityRenderState(new NetId(1), Vector3.Zero, isLocal: true, "name").Swimming);
        Assert.False(new EntityRenderState(new NetId(1), Vector3.Zero, isLocal: true, "name", grounded: true, verticalVelocity: 0f).Swimming);
        // The new ctor carries it through.
        Assert.True(new EntityRenderState(new NetId(1), Vector3.Zero, isLocal: true, "name", grounded: false, verticalVelocity: 0f, swimming: true).Swimming);
    }

    // ---- Null provider bit-identity at the simulator level ----

    [Fact]
    public void Null_medium_simulator_is_bit_identical_and_never_swims()
    {
        var withoutMedium = new PlayerMoveSimulator(Flat, Unit);
        var withNull = new PlayerMoveSimulator(Flat, Unit, medium: null);
        var s0 = new PlayerMoveState { Position = new Vector3(0f, 0.5f, 0f), Grounded = true };
        var a = s0; var b = s0;
        for (int i = 0; i < 30; i++)
        {
            a = withoutMedium.Step(a, Forward, 1f / 30f);
            b = withNull.Step(b, Forward, 1f / 30f);
        }
        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.VerticalVelocity, b.VerticalVelocity);
        Assert.False(a.Move.Swimming);
        Assert.False(b.Move.Swimming);
    }
}
