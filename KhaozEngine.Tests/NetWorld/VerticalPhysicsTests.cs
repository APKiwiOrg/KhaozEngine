using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Vertical physics across the NetWorld movement stack: the simulator (this file's first tests), the replicated
/// <see cref="MovementState"/> wire round-trip, and the authoritative servers. All headless.
/// </summary>
public class VerticalPhysicsTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    static readonly MoveCommand Idle = new(Vector2.Zero, run: false, cameraYaw: 0f);

    [Fact]
    public void Simulator_drops_an_airborne_player()
    {
        var sim = new PlayerMoveSimulator(Flat, MoveTuning.Default);
        var s = new PlayerMoveState { Position = new Vector3(0f, 20f, 0f) };   // above ground, grounded=false
        var a = sim.Step(s, Idle, 1f / 30f);
        var b = sim.Step(a, Idle, 1f / 30f);
        Assert.True(a.VerticalVelocity < 0f);
        Assert.True(b.Position.Y < a.Position.Y && a.Position.Y < 20f);
        Assert.False(b.Grounded);
    }

    [Fact]
    public void Simulator_jump_launches_then_lands()
    {
        var sim = new PlayerMoveSimulator(Flat, MoveTuning.Default);
        var s = new PlayerMoveState { Position = Vector3.Zero };
        s = sim.Step(s, Idle, 1f / 30f);                              // settle grounded
        Assert.True(s.Grounded);

        var jump = new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: true);
        s = sim.Step(s, jump, 1f / 30f);
        Assert.True(s.VerticalVelocity > 0f && !s.Grounded);          // launched

        for (int i = 0; i < 120; i++) s = sim.Step(s, Idle, 1f / 30f);
        Assert.True(s.Grounded);
        Assert.Equal(MoveTuning.Default.CapsuleHalfHeight, s.Position.Y, 4);   // landed on flat ground
        Assert.Equal(0f, s.VerticalVelocity, 4);
    }

    [Fact]
    public void Simulator_bounds_clamp_keeps_an_airborne_player_airborne()
    {
        // Guards the fix: the play-area clamp must clamp XZ only, NOT re-snap Y to the ground (which would
        // teleport a jumping player down at the wall).
        var bounds = new CircleBounds(new Vector2(0f, 0f), 5f);
        var sim = new PlayerMoveSimulator(Flat, MoveTuning.Default, groundNormal: null, bounds: bounds);
        var s = new PlayerMoveState { Position = new Vector3(4.9f, 0f, 0f) };
        s = sim.Step(s, Idle, 1f / 30f);                              // settle grounded at the edge

        var eastJump = new MoveCommand(new Vector2(1f, 0f), run: true, cameraYaw: 0f, jump: true);
        var eastRun = new MoveCommand(new Vector2(1f, 0f), run: true, cameraYaw: 0f);
        s = sim.Step(s, eastJump, 1f / 30f);                          // jump and push into the wall
        for (int i = 0; i < 4; i++) s = sim.Step(s, eastRun, 1f / 30f);

        Assert.True(bounds.Contains(s.Position.X, s.Position.Z), $"escaped bounds to {s.Position}");
        Assert.True(s.Position.X <= 5f + 1e-3f);
        Assert.False(s.Grounded);
        Assert.True(s.Position.Y > MoveTuning.Default.CapsuleHalfHeight + 0.1f,
            $"airborne Y was snapped to ground at the wall: {s.Position.Y}");
    }

    [Fact]
    public void MovementState_round_trips_through_the_replication_registry()
    {
        ReplicationRegistry registry = MoveProtocol.CreateRegistry();
        var server = new World();
        Entity e = server.Spawn();
        server.Set(e, new NetId(7));
        server.Set(e, new ReplicatedPosition { Value = new Vector3(1f, 2f, 3f) });
        server.Set(e, new MovementState
        {
            VerticalVelocity = 5.5f, Grounded = true, TimeSinceGrounded = 0.2f, JumpBufferRemaining = 0.05f,
        });

        byte[] snapshot = SnapshotWriter.WriteFiltered(server, registry, new HashSet<int> { 7 });

        var view = new ClientReplicationView(registry);
        var client = new World();
        view.Apply(client, snapshot);

        Assert.True(view.TryGetEntity(7, out Entity ce));
        MovementState ms = client.Get<MovementState>(ce);
        Assert.Equal(5.5f, ms.VerticalVelocity, 4);
        Assert.True(ms.Grounded);
        Assert.Equal(0.2f, ms.TimeSinceGrounded, 4);
        Assert.Equal(0.05f, ms.JumpBufferRemaining, 4);
    }

    // Reads the (single) player's replicated vertical velocity straight off a server's authoritative world.
    static float ServerVerticalVelocity(World world)
    {
        float v = float.NaN;
        world.ForEach<NetId, MovementState>((Entity e, ref NetId _, ref MovementState ms) => v = ms.VerticalVelocity);
        return v;
    }

    [Fact]
    public void WorldServer_replicates_a_jump_then_landing()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var cfg = new WorldServerConfig { TickSeconds = 1f / 30f, SpawnPosition = _ => Vector3.Zero };
        var server = new WorldServer(st, cfg, Flat, MoveTuning.Default);
        var client = new NetClient(ct);
        for (int i = 0; i < 10; i++) { client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); }
        Assert.True(server.TryGetPlayerNetId(client.Slot, out _));

        var jump = new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: true);
        client.Send(MoveProtocol.EncodeMove(0, jump), NetChannelReliability.ReliableOrdered);
        bool launched = false;
        for (int i = 0; i < 5; i++)
        {
            client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);
            if (ServerVerticalVelocity(server.World) > 0f) launched = true;   // catch the launch tick
        }
        Assert.True(launched, "server should replicate the launch (MovementState)");
        Assert.True(server.TryGetPlayerState(client.Slot, out PlayerMoveState s1) && !s1.Grounded);

        for (int i = 0; i < 120; i++)
        {
            client.Send(MoveProtocol.EncodeMove(i + 1, MoveCommand.Idle), NetChannelReliability.ReliableOrdered);
            client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);
        }
        Assert.True(server.TryGetPlayerState(client.Slot, out PlayerMoveState s2));
        Assert.True(s2.Grounded, "should land");
        Assert.Equal(0f, s2.VerticalVelocity, 3);
    }

    [Fact]
    public void ShardedWorldServer_launches_on_jump()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var cfg = new ShardedWorldServerConfig
        {
            TickSeconds = 1f / 30f, CellSize = 60f, OverlapMargin = 24f, InterestRadius = 24f,
            SpawnPosition = _ => Vector3.Zero,
        };
        var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default);
        var client = new NetClient(ct);
        for (int i = 0; i < 10; i++) { client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); }
        Assert.True(server.TryGetPlayerNetId(client.Slot, out _));

        var jump = new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: true);
        bool launched = false;
        for (int i = 0; i < 5; i++)
        {
            client.Send(MoveProtocol.EncodeMove(i, i == 0 ? jump : MoveCommand.Idle), NetChannelReliability.ReliableOrdered);
            client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);
            if (server.TryGetPlayerState(client.Slot, out PlayerMoveState s) && s.VerticalVelocity > 0f) launched = true;
        }
        Assert.True(launched, "sharded server should launch the player on jump and expose it via TryGetPlayerState");
    }

    [Fact]
    public void ShardedWorldServer_keeps_vertical_state_across_a_cell_handoff()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var cfg = new ShardedWorldServerConfig
        {
            TickSeconds = 1f / 30f, CellSize = 8f, OverlapMargin = 4f, InterestRadius = 4f,
            SpawnPosition = _ => Vector3.Zero,
        };
        var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default);
        var client = new NetClient(ct);
        for (int i = 0; i < 10; i++) { client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); }
        Assert.True(server.TryGetPlayerNetId(client.Slot, out int netId));

        // Hold jump + run east: the player auto-bhops across cell boundaries, so most boundary crossings happen
        // while airborne. The vertical state must survive the handoff (registered component), not reset to grounded.
        var cmd = new MoveCommand(new Vector2(1f, 0f), run: true, cameraYaw: 0f, jump: true);
        CellCoord? prevCell = null;
        int handoffs = 0;
        bool airborneAcrossHandoff = false;
        for (int i = 0; i < 400; i++)
        {
            client.Send(MoveProtocol.EncodeMove(i, cmd), NetChannelReliability.ReliableOrdered);
            client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);
            if (!server.Host.TryGetOwner(netId, out CellSim cell, out Entity e)) continue;
            MovementState ms = cell.World.Get<MovementState>(e);
            if (prevCell is CellCoord pc && pc != cell.Coord)
            {
                handoffs++;
                if (!ms.Grounded && MathF.Abs(ms.VerticalVelocity) > 0.01f) airborneAcrossHandoff = true;
            }
            prevCell = cell.Coord;
        }
        Assert.True(handoffs >= 2, $"expected several cell handoffs, saw {handoffs}");
        Assert.True(airborneAcrossHandoff, "an airborne crosser must keep its vertical state across the handoff");
    }
}
