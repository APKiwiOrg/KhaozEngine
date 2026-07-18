using System;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

// The SHARDED-head climb-signal EXPORT regression + cross-head parity. The single-World WorldServer keeps the full
// PlayerMoveState per slot and writes MovementState.From(state) every tick, so ClimbRateQ (the quantized step-climb
// rate a remote reads to glide) rides the wire. The SHARDED head (ShardedWorldServer -> PlayerMovementSystem, the head
// Ruinborne actually runs) reconstructs a MoveState per tick from the MovementState component and, before this fix,
// never wrote ClimbRateQ back nor carried the sim-local ascent EWMA across ticks - so a remote player on a sharded
// server saw ClimbRateQ == 0 for a whole stair climb (no glide), and the reconcile-seed fix decoded a wire 0.
//
// Test 1 pins the sharded export: nonzero through a climb, and within one wire quantum of the single-World head (the
// parity-across-heads pin). RED on current code (the sharded head never writes ClimbRateQ, so it is identically 0).
public class ShardedClimbSignalExportTests
{
    const float Riser = 0.30f, Tread = 0.40f;
    const int Risers = 33;
    const float Dt = 1f / 30f;

    static MoveTuning Tuning() => MoveTuning.Default with { WalkSpeed = 3f, RunSpeed = 6f, CapsuleRadius = 0.4f };

    // A solid-box staircase climbing in -Z, approached head-on from +Z (yaw 0 => forward -Z). Same fixture as
    // ClimbSignalTests / StairGlideReconcileParityTests (grade 0.75 riser/tread, the TestStaircase scale).
    static void AddStairs(IPhysicsWorld world)
    {
        float backZ = -Tread * Risers - 2f;
        const float halfX = 20f;
        for (int i = 0; i < Risers; i++)
        {
            float treadTop = Riser * (i + 1);
            float centerZ = 0.5f * (-Tread * i + backZ);
            float depth = -Tread * i - backZ;
            world.AddStatic(new BoxShape(new Vector3(halfX, treadTop * 0.5f, depth * 0.5f)),
                Pose.At(new Vector3(0f, treadTop * 0.5f, centerZ)));
        }
    }

    static float Ground(float x, float z) => 0f;
    static Vector3 Normal(float x, float z) => Vector3.UnitY;

    // Drive the SHARDED per-cell step (PlayerMovementSystem, physics-fed, bounds/medium null, exactly as
    // ShardedWorldServer wires it) up the staircase, and IN LOCKSTEP a single-World head reference chain (the full
    // MoveState carried tick-to-tick, exported as MovementState.From(state).ClimbRateQ). Same static Bepu world (queried
    // read-only by both), same seed, same command, so the ONLY thing that can differ is whether the sharded head
    // actually exports the signal.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShardedExport_ClimbRateQ_NonzeroThroughClimb_AndMatchesSingleWorldHead(bool run)
    {
        MoveTuning t = Tuning();
        float halfH = t.CapsuleHalfHeight;
        using IPhysicsWorld world = new BepuPhysicsWorld();
        AddStairs(world);
        world.Step(Dt);

        var cmd = new MoveCommand(new Vector2(0f, 1f), run, cameraYaw: 0f, jump: false);
        var seed = new Vector3(0f, halfH, 1.0f);

        // The sharded head: exactly PlayerMovementSystem the way ShardedWorldServer constructs it.
        var sys = new PlayerMovementSystem(Ground, t, Normal, bounds: null, physics: world, medium: null);
        var ecs = new World();
        Entity e = ecs.Spawn();
        ecs.Set(e, new NetId(1));
        ecs.Set(e, new ReplicatedPosition { Value = seed });
        ecs.Set(e, new MovementState());
        ecs.Set(e, new PendingMove { Command = cmd });

        // The single-World head reference: carry the full MoveState (as WorldServer keeps a PlayerMoveState per slot)
        // and export MovementState.From(state).ClimbRateQ each tick.
        var refState = new MoveState { Position = seed, Grounded = true };

        int ticks = (int)(1.6f * (Tread * Risers + 3f) / (0.5f * (run ? t.RunSpeed : t.WalkSpeed) * Dt));
        int maxAbsSharded = 0, climbTicks = 0, maxGapUnits = 0;
        for (int i = 0; i < ticks; i++)
        {
            refState = CharacterMovement.Step(refState, cmd, Dt, Ground, t, Normal, world);
            sbyte refQ = MovementState.QuantizeClimbRate(refState.ClimbRate);

            sys.Update(ecs, Dt);
            sbyte shardedQ = ecs.Get<MovementState>(e).ClimbRateQ;

            maxAbsSharded = Math.Max(maxAbsSharded, Math.Abs((int)shardedQ));
            if (refQ != 0) climbTicks++;
            maxGapUnits = Math.Max(maxGapUnits, Math.Abs((int)shardedQ - (int)refQ));
        }

        Assert.True(climbTicks > 20, $"run={run}: too few climb ticks in the reference stream ({climbTicks})");
        // RED on current code: the sharded head never writes ClimbRateQ back, so it stays 0 through the whole climb
        // and no remote ever glides on a sharded server.
        Assert.True(maxAbsSharded > 0,
            $"run={run}: sharded ClimbRateQ was 0 for the entire climb (remote players never glide on a sharded server)");
        // Parity across heads: both heads run the identical CharacterMovement.Step over the identical carried state and
        // query the same static world, so the exported ClimbRateQ is BIT-IDENTICAL (gap 0). The contract the feature
        // needs is only "within one wire quantum"; the exact-0 pin is the stronger fact that actually holds and guards a
        // future drift between the two heads' export paths.
        Assert.True(maxGapUnits == 0,
            $"run={run}: sharded vs single-World ClimbRateQ diverged by {maxGapUnits} unit(s) (must stay within one wire quantum; is bit-identical today)");
    }

    static bool PosAccessor(World w, Entity e, out float x, out float y)
    {
        if (w.TryGet(e, out ReplicatedPosition p)) { x = p.Value.X; y = p.Value.Z; return true; }
        x = y = 0f;
        return false;
    }

    // Test 3: shard-handoff EWMA behaviour. The sim-local ascent EWMA (MovementState.ClimbRateEwma) is DELIBERATELY
    // absent from the movement codec, and a handoff captures only the Migrate-channel codec set
    // (SnapshotWriter.WriteFiltered), so the crossing RECONSTRUCTS the entity's MovementState via the codec rather than
    // copying the struct wholesale. Result (the trace's answer): the wire ClimbRateQ SURVIVES the crossing (a remote
    // keeps seeing the climb across a cell edge) while the sim-local EWMA RESETS to 0 - an acceptable few-tick
    // re-converge transient, documented on the field.
    [Fact]
    public void ShardHandoff_CarriesWireClimbRateQ_ButResetsSimLocalEwma()
    {
        ReplicationRegistry registry = MoveProtocol.CreateRegistry();
        var host = new ShardHost(cellSize: 100f, tickSeconds: Dt, registry, interestCellSize: 100f,
            overlapMargin: 0f, positionAccessor: PosAccessor);

        Entity e = host.SpawnAt(95f, 50f, out CellSim a);
        a.World.Set(e, new NetId(7));
        a.World.Set(e, new ReplicatedPosition { Value = new Vector3(95f, 1f, 50f) });
        // A mid-run climber: a nonzero sim-local EWMA AND a nonzero wire ClimbRateQ.
        sbyte q = MovementState.QuantizeClimbRate(2.5f);
        a.World.Set(e, new MovementState { VerticalVelocity = 1.5f, Grounded = true, ClimbRateEwma = 2.5f, ClimbRateQ = q });
        Assert.Equal(new CellCoord(0, 0), a.Coord);
        Assert.NotEqual(0, (int)q);

        // Cross the east edge into cell B(1,0) and hand off.
        Assert.True(host.TryGetOwner(7, out CellSim owner, out Entity moving));
        owner.World.Set(moving, new ReplicatedPosition { Value = new Vector3(130f, 1f, 50f) });
        host.ProcessHandoffs();

        Assert.True(host.TryGetOwner(7, out CellSim b, out Entity moved));
        Assert.Equal(new CellCoord(1, 0), b.Coord);
        MovementState ms = b.World.Get<MovementState>(moved);
        // The WIRE signal is codec-migrated, so a remote keeps seeing the climb across the shard edge.
        Assert.Equal(q, ms.ClimbRateQ);
        Assert.Equal(1.5f, ms.VerticalVelocity, 3);
        // The SIM-LOCAL ascent EWMA rides no codec, so it resets to 0 on the crossing (reconstructed, not copied).
        Assert.Equal(0f, ms.ClimbRateEwma);
    }
}
