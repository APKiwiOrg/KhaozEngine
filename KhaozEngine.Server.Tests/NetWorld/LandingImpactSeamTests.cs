using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The landing-impact server seam: <c>MoveState.LandingImpactSpeed</c> mirrored onto <see cref="MovementState"/> as a
/// SIM-LOCAL field (not replicated, not migrated) so it survives to the end of a tick on both heads, plus the
/// post-movement <c>OnAfterTick</c> hook a game reads it from. Before this there was no hook at all: a consumer had to
/// observe the landing a tick late from its own <c>OnBeforeTick</c>, by which point the impact had been overwritten.
/// </summary>
public class LandingImpactSeamTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    static MoveCommand Jump => new(Vector2.Zero, run: false, cameraYaw: 0f, jump: true);
    static MoveCommand Run => new(new Vector2(1f, 0f), run: true, cameraYaw: 0f, jump: false);

    // ---- The mirror is sim-local: it must not reach the wire ----

    [Fact]
    public void LandingImpactSpeed_IsNotOnTheWire()
    {
        // Deliberately absent from the movement codec (MoveProtocol.CreateRegistry), exactly like ClimbRateEwma and
        // CommandedVelocity: the server computes fall damage from its own authoritative step, and a remote that wants
        // landing VFX already receives Grounded + VerticalVelocity and can derive the transition. So it decodes to 0.
        ReplicationRegistry registry = MoveProtocol.CreateRegistry();
        var serverWorld = new World();
        Entity e = serverWorld.Spawn();
        serverWorld.Set(e, new NetId(1));
        serverWorld.Set(e, ReplicatedPosition.FromWorld(Vector3.Zero, WorldFrame.Origin));
        serverWorld.Set(e, new MovementState { Grounded = true, VerticalVelocity = -2f, LandingImpactSpeed = 17.5f });

        byte[] snapshot = SnapshotWriter.Write(serverWorld, registry);
        var view = new ClientReplicationView(registry);
        var clientWorld = new World();
        view.Apply(clientWorld, snapshot);

        Assert.True(view.TryGetEntity(1, out Entity ce));
        MovementState back = clientWorld.Get<MovementState>(ce);
        Assert.Equal(-2f, back.VerticalVelocity, 4);               // the replicated axis survives
        Assert.Equal(0f, back.LandingImpactSpeed);                 // the sim-local latch does not
    }

    // ---- Single-World head ----

    static (WorldServer server, NetClient client, WorldServerConfig cfg) ConnectSingle()
    {
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var cfg = new WorldServerConfig { TickSeconds = 1f / 30f, SpawnPosition = _ => Vector3.Zero };
        var server = new WorldServer(st, cfg, Flat, MoveTuning.Default);
        var client = new NetClient(ct, TestHandshake.Wire());
        for (int i = 0; i < 10; i++) { client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); }
        Assert.True(server.TryGetPlayerNetId(client.Slot, out _));
        return (server, client, cfg);
    }

    [Fact]
    public void WorldServer_OnAfterTick_FiresOncePerTick_WithPostStepStateVisible()
    {
        (WorldServer server, NetClient client, WorldServerConfig cfg) = ConnectSingle();

        int calls = 0;
        Vector3 seenInside = Vector3.Zero;
        server.OnAfterTick += _ =>
        {
            calls++;
            if (server.TryGetPlayerState(client.Slot, out PlayerMoveState st)) seenInside = st.Position;
        };

        // One tick of a running command: the position the handler sees must be THIS tick's, not last tick's.
        Assert.True(server.TryGetPlayerState(client.Slot, out PlayerMoveState before));
        client.Send(MoveProtocol.EncodeMove(0, Run), NetChannelReliability.ReliableOrdered);
        client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);

        Assert.Equal(1, calls);                                    // exactly one OnAfterTick per Tick
        Assert.True(server.TryGetPlayerState(client.Slot, out PlayerMoveState after));
        Assert.True(after.Position.X > before.Position.X + 1e-4f, "the tick did not move the player");
        Assert.Equal(after.Position.X, seenInside.X, 5);           // the handler ran AFTER movement

        for (int i = 0; i < 4; i++) { client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); }
        Assert.Equal(5, calls);
    }

    [Fact]
    public void WorldServer_LandingImpact_IsReadablePerSlot_FromInsideOnAfterTick()
    {
        (WorldServer server, NetClient client, WorldServerConfig cfg) = ConnectSingle();

        var impacts = new List<float>();
        server.OnAfterTick += _ =>
        {
            if (server.TryGetPlayerState(client.Slot, out PlayerMoveState st) && st.Move.LandingImpactSpeed != 0f)
                impacts.Add(st.Move.LandingImpactSpeed);
        };

        client.Send(MoveProtocol.EncodeMove(0, Jump), NetChannelReliability.ReliableOrdered);
        for (int i = 0; i < 90; i++)
        {
            client.Send(MoveProtocol.EncodeMove(i + 1, MoveCommand.Idle), NetChannelReliability.ReliableOrdered);
            client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);
        }

        Assert.True(server.TryGetPlayerState(client.Slot, out PlayerMoveState landed) && landed.Grounded);
        float single = Assert.Single(impacts);                     // exactly one landing across the whole arc
        // A JumpSpeed 9.79796 launch under g 25 falls back at about the launch speed.
        Assert.InRange(single, 8f, 11f);
    }

    // ---- Sharded head ----

    static (ShardedWorldServer server, NetClient client, ShardedWorldServerConfig cfg) ConnectSharded()
    {
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var cfg = new ShardedWorldServerConfig
        {
            TickSeconds = 1f / 30f, CellSize = 60f, OverlapMargin = 24f, InterestRadius = 24f,
            SpawnPosition = _ => Vector3.Zero,
        };
        var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default);
        var client = new NetClient(ct, TestHandshake.Wire());
        for (int i = 0; i < 10; i++) { client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); }
        Assert.True(server.TryGetPlayerNetId(client.Slot, out _));
        return (server, client, cfg);
    }

    [Fact]
    public void ShardedWorldServer_OnAfterTick_FiresOncePerTick_WithPostStepStateVisible()
    {
        (ShardedWorldServer server, NetClient client, ShardedWorldServerConfig cfg) = ConnectSharded();

        int calls = 0;
        Vector3 seenInside = Vector3.Zero;
        server.OnAfterTick += _ =>
        {
            calls++;
            if (server.TryGetPlayerState(client.Slot, out PlayerMoveState st)) seenInside = st.Position;
        };

        Assert.True(server.TryGetPlayerState(client.Slot, out PlayerMoveState before));
        client.Send(MoveProtocol.EncodeMove(0, Run), NetChannelReliability.ReliableOrdered);
        client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);

        Assert.Equal(1, calls);
        Assert.True(server.TryGetPlayerState(client.Slot, out PlayerMoveState after));
        Assert.True(after.Position.X > before.Position.X + 1e-4f, "the tick did not move the player");
        Assert.Equal(after.Position.X, seenInside.X, 5);           // post-movement, post-handoff

        for (int i = 0; i < 4; i++) { client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); }
        Assert.Equal(5, calls);
    }

    [Fact]
    public void ShardedWorldServer_LandingImpact_IsReadablePerSlot_FromInsideOnAfterTick()
    {
        // The sharded write-back is the path that is easy to forget: PlayerMovementSystem reconstructs a fresh MoveState
        // from the MovementState component every tick, so a step OUTPUT that is not written back out has nowhere to
        // survive to the end of ShardedWorldServer.Tick.
        (ShardedWorldServer server, NetClient client, ShardedWorldServerConfig cfg) = ConnectSharded();

        var impacts = new List<float>();
        server.OnAfterTick += _ =>
        {
            if (server.TryGetPlayerState(client.Slot, out PlayerMoveState st) && st.Move.LandingImpactSpeed != 0f)
                impacts.Add(st.Move.LandingImpactSpeed);
        };

        client.Send(MoveProtocol.EncodeMove(0, Jump), NetChannelReliability.ReliableOrdered);
        for (int i = 0; i < 90; i++)
        {
            client.Send(MoveProtocol.EncodeMove(i + 1, MoveCommand.Idle), NetChannelReliability.ReliableOrdered);
            client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);
        }

        Assert.True(server.TryGetPlayerState(client.Slot, out PlayerMoveState landed) && landed.Grounded);
        float single = Assert.Single(impacts);
        Assert.InRange(single, 8f, 11f);
    }

    [Fact]
    public void ShardedWorldServer_ASkippedEntityReportsNoImpact()
    {
        // A per-tick step OUTPUT must be cleared for an entity the cell sim skipped (a Ghost or an in-flight Migrating
        // entity), or a stale latch would read as a landing that never happened this tick - the same reason
        // CommandedVelocity is zeroed on that path.
        var sys = new PlayerMovementSystem(Flat, MoveTuning.Default);
        var ecs = new World();
        Entity e = ecs.Spawn();
        ecs.Set(e, new NetId(1));
        ecs.Set(e, ReplicatedPosition.FromWorld(new Vector3(0f, 10f, 0f), WorldFrame.Origin));
        ecs.Set(e, new MovementState { LandingImpactSpeed = 42f });
        ecs.Set(e, new PendingMove { Command = MoveCommand.Idle });
        ecs.Set(e, new KhaozEngine.Sharding.Ghost());

        sys.Update(ecs, 1f / 30f);

        Assert.Equal(0f, ecs.Get<MovementState>(e).LandingImpactSpeed);
    }

    [Fact]
    public void ShardedWorldServer_AFrameWithNoSubTick_DoesNotReReportTheLanding()
    {
        // The sharded head drives its cells through a fixed-tick accumulator (CellSim.Tick, maxTicksPerFrame: 1), so a
        // frame shorter than TickSeconds produces NO movement sub-tick at all: PlayerMovementSystem never runs, nothing
        // rewrites MovementState.LandingImpactSpeed, and a hook that fired anyway would hand a fall-damage consumer the
        // PREVIOUS landing a second time, once per short frame. The flat head steps unconditionally per Tick and never
        // had the gap, which is exactly why the two heads have to agree on one semantic: the hook fires after frames in
        // which authoritative movement RAN.
        (ShardedWorldServer server, NetClient client, ShardedWorldServerConfig cfg) = ConnectSharded();

        int calls = 0;
        var impacts = new List<float>();
        server.OnAfterTick += _ =>
        {
            calls++;
            if (server.TryGetPlayerState(client.Slot, out PlayerMoveState st) && st.Move.LandingImpactSpeed != 0f)
                impacts.Add(st.Move.LandingImpactSpeed);
        };

        // Jump, then step FULL frames (one sub-tick each) up to and including the landing tick, so the short frames
        // below start on the one tick where the latch is actually loaded.
        client.Send(MoveProtocol.EncodeMove(0, Jump), NetChannelReliability.ReliableOrdered);
        int frames = 0;
        while (impacts.Count == 0 && frames < 90)
        {
            client.Send(MoveProtocol.EncodeMove(++frames, MoveCommand.Idle), NetChannelReliability.ReliableOrdered);
            client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);
        }
        Assert.True(server.TryGetPlayerState(client.Slot, out PlayerMoveState landed) && landed.Grounded);
        Assert.InRange(Assert.Single(impacts), 8f, 11f);
        Assert.Equal(frames, calls);   // one hook call per full frame

        // Three frames of a fifth of a tick each: 0.6 of a tick between them, so no sub-tick can run. The hook must
        // not fire at all, and the landing it already reported must not be observable a second time.
        int callsAfterLanding = calls;
        for (int i = 0; i < 3; i++) { client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds * 0.2f); }
        Assert.Single(impacts);
        Assert.Equal(callsAfterLanding, calls);

        // Three more short frames DO complete a tick between them: exactly one movement sub-tick, so exactly one hook
        // call, and it observes post-step state in which the latch has been rewritten to 0.
        for (int i = 0; i < 3; i++) { client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds * 0.2f); }
        Assert.Equal(callsAfterLanding + 1, calls);
        Assert.Single(impacts);
    }

    [Fact]
    public void WorldServer_ShortFramesStillStepAndReportExactlyOneLanding()
    {
        // The mirror on the flat head, which has no accumulator: every Tick runs movement whatever the dt, so a short
        // frame IS a movement frame - it fires the hook AND it rewrites the latch. The unified semantic therefore
        // costs this head nothing, and the landing stays a single event across a run of short frames.
        (WorldServer server, NetClient client, WorldServerConfig cfg) = ConnectSingle();

        int calls = 0;
        var impacts = new List<float>();
        server.OnAfterTick += _ =>
        {
            calls++;
            if (server.TryGetPlayerState(client.Slot, out PlayerMoveState st) && st.Move.LandingImpactSpeed != 0f)
                impacts.Add(st.Move.LandingImpactSpeed);
        };

        client.Send(MoveProtocol.EncodeMove(0, Jump), NetChannelReliability.ReliableOrdered);
        int frames = 0;
        while (impacts.Count == 0 && frames < 90)
        {
            client.Send(MoveProtocol.EncodeMove(++frames, MoveCommand.Idle), NetChannelReliability.ReliableOrdered);
            client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);
        }
        Assert.True(server.TryGetPlayerState(client.Slot, out PlayerMoveState landed) && landed.Grounded);
        Assert.InRange(Assert.Single(impacts), 8f, 11f);
        Assert.Equal(frames, calls);

        for (int i = 0; i < 6; i++) { client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds * 0.2f); }
        Assert.Equal(frames + 6, calls);   // every Tick stepped movement, so every Tick fired the hook
        Assert.Single(impacts);            // and none of them re-reported the landing
    }

    // ---- Teleport ----

    [Fact]
    public void ATeleportMidFall_ReportsOnlyThePostTeleportFall()
    {
        // The admin/self-rescue teleport places the player and zeroes VerticalVelocity (and advances TeleportEpoch so the
        // client cuts rather than glides). A character yanked out of a long fall must therefore land reporting only the
        // short drop it took AFTER the teleport - never the speed it had accumulated before it.
        (WorldServer server, NetClient client, WorldServerConfig cfg) = ConnectSingle();

        var impacts = new List<float>();
        server.OnAfterTick += _ =>
        {
            if (server.TryGetPlayerState(client.Slot, out PlayerMoveState st) && st.Move.LandingImpactSpeed != 0f)
                impacts.Add(st.Move.LandingImpactSpeed);
        };

        // Drop the player from 60 m and let it build up a long fall.
        Assert.True(server.TryGetPlayerState(client.Slot, out PlayerMoveState seed));
        seed.Position = new Vector3(0f, 60f, 0f);
        seed.Grounded = false;
        server.SetPlayerState(client.Slot, seed);
        for (int i = 0; i < 40; i++)
        {
            client.Send(MoveProtocol.EncodeMove(i, MoveCommand.Idle), NetChannelReliability.ReliableOrdered);
            client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);
        }
        Assert.True(server.TryGetPlayerState(client.Slot, out PlayerMoveState falling));
        Assert.False(falling.Grounded);
        float fastFall = -falling.VerticalVelocity;
        Assert.True(fastFall > 20f, $"the pre-teleport fall was not fast enough to be a meaningful pin ({fastFall:F1} m/s)");
        Assert.Empty(impacts);
        uint epochBefore = falling.TeleportEpoch;

        // The real mechanism: the queued admin teleport, applied on the host thread at the top of the next Tick.
        server.Teleport(PlayerRef.Slot(client.Slot), new Vector3(0f, 1.5f + MoveTuning.Default.CapsuleHalfHeight, 0f));
        for (int i = 0; i < 60; i++)
        {
            client.Send(MoveProtocol.EncodeMove(100 + i, MoveCommand.Idle), NetChannelReliability.ReliableOrdered);
            client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);
        }

        Assert.True(server.TryGetPlayerState(client.Slot, out PlayerMoveState landed));
        Assert.True(landed.Grounded, "the teleported player should have landed");
        Assert.True(landed.TeleportEpoch > epochBefore, "the teleport should have advanced the epoch");
        float single = Assert.Single(impacts);
        // 1.5 m of post-teleport fall under g 25 is ~8.7 m/s, nowhere near the pre-teleport speed.
        Assert.InRange(single, 6f, 10f);
        Assert.True(single < fastFall - 10f, $"the landing reported a pre-teleport fall speed ({single:F1} m/s)");
    }
}
