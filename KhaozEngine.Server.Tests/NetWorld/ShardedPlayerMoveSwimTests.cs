using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;
using KhaozEngine.Netcode;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Surface swim on the SHARDED authoritative path. The per-cell <see cref="PlayerMovementSystem"/> reconstructs a
/// <see cref="MoveState"/> from the replicated <see cref="MovementState"/> component every tick, so the swim flag
/// (and its enter/exit hysteresis carry, which lives in <see cref="MoveState.Swimming"/>) must survive BOTH the
/// reconstruction (input) AND the write-back (output). This is the sharded analog of <see cref="PlayerMoveSwimTests"/>:
/// that suite only drove the non-sharded <see cref="WorldServer"/> path, which wraps the whole MoveState opaquely, so
/// it stayed green while the sharded path silently dropped the bit. The player SPAWNS DRY and swims INTO water on
/// purpose - a deep-water spawn masks the bug, because <see cref="ShardedWorldServer"/> seeds the entity's
/// MovementState from a swimming spawn step, leaving the flag stuck true rather than at its real dry-land false.
/// </summary>
public class ShardedPlayerMoveSwimTests
{
    static readonly MoveTuning Unit = MoveTuning.Default with { CapsuleHalfHeight = 0.5f };
    static readonly MoveCommand Forward = new(new Vector2(0f, 1f), run: true, cameraYaw: 0f);   // travels toward -Z

    // Flat ground at 0, except a raised lakebed shelf at 2.4 m (the swimmer's buoyancy waterline, see below) once the
    // character is well into the water at z < 21. Both heads must sample the same ground (the same both-heads contract
    // the medium carries), so this is threaded into the server and every client.
    static float Ground(float x, float z) => z < 21f ? 2.4f : 0f;

    // Dry land at spawn; water (surface 3 m) once the character crosses forward past z = 25. Over the deep stretch
    // (25 > z > 21, lakebed at 0) submersion is ~3 body-heights, well past the swim-ENTER threshold, so the character
    // starts swimming. On the shelf (z < 21) the lakebed rises to the swimmer's resting waterline: submersion there is
    // exactly the buoyancy fraction 0.6 of body height, which sits INSIDE the enter/exit hysteresis band [0.55, 0.65].
    // So at the shelf the swim decision is pinned to the CARRIED flag: a swimmer floats there and stays swimming (exit
    // 0.55), while a character that dropped the flag grounds on the shelf and never re-enters (enter 0.65). That makes
    // the shelf sample fail if EITHER the input carry or the write-back is missing. Identical provider on every head.
    static MovementMedium Water(float x, float z, float feetY)
        => z < 25f ? new MovementMedium(3.0f, inWater: true) : MovementMedium.Dry;

    [Fact]
    public void Swim_flag_reaches_TryGetPlayerState_and_a_remote_observers_render_state()
    {
        var hub = new InMemoryTransportHub();
        var cfg = new ShardedWorldServerConfig
        {
            // One big cell so the swimmer and observer stay co-owned with no handoff in the middle of the test.
            TickSeconds = 1f / 30f, CellSize = 400f, OverlapMargin = 30f, InterestRadius = 30f,
            MaxPlayers = 8, SpawnPosition = _ => new Vector3(30f, 0f, 30f),   // dry land, safely inside cell (0,0)
        };
        var server = new ShardedWorldServer(hub.Server, cfg, Ground, Unit, medium: Water);

        // Two co-located clients (both inside each other's AoI): the swimmer drives input, the observer only watches -
        // decoding the swimmer's remote replicated MovementState into an EntityRenderState.
        var swimmer = new WorldClient(hub.CreateClient(), Ground, Unit,
            new WorldClientConfig { TickSeconds = cfg.TickSeconds }, medium: Water);
        var observer = new WorldClient(hub.CreateClient(), Ground, Unit,
            new WorldClientConfig { TickSeconds = cfg.TickSeconds }, medium: Water);

        for (int i = 0; i < 30; i++) { swimmer.Poll(); observer.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); }
        Assert.True(swimmer.Joined);
        Assert.True(observer.Joined);

        // Drive the swimmer forward off dry land, through the deep stretch (enters swim), and onto the shelf, holding
        // long enough for the buoyancy settle to reach the resting waterline and for the state to stabilise there.
        for (int i = 0; i < 150; i++)
        {
            swimmer.SendInput(Forward);
            observer.Poll(); swimmer.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);
        }

        // Precondition: the swimmer actually reached the hysteresis-band shelf (z < 21), where the swim decision is
        // carried, not re-derivable - otherwise the test would not exercise the input carry.
        long swimmerNet = swimmer.LocalNetId;
        int swimmerSlot = SlotOf(server, swimmerNet, cfg.MaxPlayers);
        Assert.True(server.TryGetPlayerState(swimmerSlot, out PlayerMoveState st));
        Assert.True(st.Position.Z < 21f, $"swimmer must reach the shelf for the test to bite; got z={st.Position.Z}");

        // 1) The authoritative sharded state reads Swimming - the write-back landed on the ECS MovementState, and the
        //    input carry held the swim state across the shelf's hysteresis band.
        Assert.True(st.Move.Swimming, "the sharded authoritative player must still be swimming on the shelf");

        // 2) A second observer decodes that swim bit off the wire-replicated MovementState (the remote animation source).
        observer.Poll();
        EntityRenderState remote = default;
        bool found = false;
        foreach (EntityRenderState e in observer.Snapshot())
            if (!e.IsLocal && e.Id.Value == swimmerNet) { remote = e; found = true; }
        Assert.True(found, "the observer must see the swimmer as a remote entity");
        Assert.True(remote.Swimming, "a remote observer must decode the swimmer's replicated swim flag");
    }

    static int SlotOf(ShardedWorldServer server, long netId, int maxPlayers)
    {
        for (int slot = 0; slot < maxPlayers; slot++)
            if (server.TryGetPlayerNetId(slot, out long n) && n == netId) return slot;
        throw new Xunit.Sdk.XunitException($"no joined slot owns netId {netId}");
    }
}
