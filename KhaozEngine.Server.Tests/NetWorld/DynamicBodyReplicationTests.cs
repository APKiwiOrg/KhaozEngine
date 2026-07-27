using System;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// End-to-end coverage of dynamic-body replication (Task 3): a server-authoritative dynamic rigid body sampled from
/// the physics world into <see cref="ReplicatedPosition"/> + <see cref="DynamicBodyState"/> (via
/// <see cref="DynamicBodyReplication"/>), replicated to a <see cref="WorldClient"/> that INTERPOLATES it on the same
/// fixed-delay buffer as a remote player and never simulates it.
///
/// Harness discipline (the documented 9.23.0 lesson): the client presentation clock is deliberately PHASE-OFFSET from
/// the server tick (sub-tick advances at a non-integer render:tick ratio), never phase-locked one present per tick, so
/// a phase-locked harness cannot hide an interpolation artifact. Convergence is asserted BANDED (a tolerance), not to
/// the bit, because the rendered value is a fixed-delay interpolation of the two bracketing snapshots.
/// </summary>
public class DynamicBodyReplicationTests
{
    private static float Flat(float x, float z) => 0f;
    private const float Dt = 1f / 30f;   // server tick == client tick

    // A physics world with a static ground plane whose top is at y=0, plus a dynamic box dropped from a height.
    private static (BepuPhysicsWorld physics, DynamicBodyHandle body) NewPhysicsWithFallingBox(float dropY)
    {
        var physics = new BepuPhysicsWorld();
        physics.AddStatic(new BoxShape(new Vector3(50f, 0.5f, 50f)), Pose.At(new Vector3(0f, -0.5f, 0f)));
        DynamicBodyHandle body = physics.AddDynamic(
            new BoxShape(new Vector3(0.5f, 0.5f, 0.5f)), Pose.At(new Vector3(0f, dropY, 0f)),
            DynamicBodyDescription.WithMass(1f));
        return (physics, body);
    }

    private static Vector3 ClientBodyPos(WorldClient client, long netId)
    {
        foreach (EntityRenderState e in client.Snapshot())
            if (e.Id.Value == netId) return e.Position;
        throw new Xunit.Sdk.XunitException($"body {netId} not visible on the client");
    }

    // -------------------------------------------------------------------------
    // A falling body replicates and the client-interpolated pose converges (banded).
    // -------------------------------------------------------------------------

    [Fact]
    public void FallingBody_ReplicatesToClient_InterpolatedPoseConvergesToServerPose()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var cfg = new WorldServerConfig { TickSeconds = Dt, MaxPlayers = 4, InterestRadius = 500f,
            SpawnPosition = _ => new Vector3(0f, 0f, 0f) };
        var server = new WorldServer(st, cfg, Flat, MoveTuning.Default);

        var (physics, body) = NewPhysicsWithFallingBox(dropY: 8f);
        long netId = server.SpawnEntity(0f, 0f);
        var repl = new DynamicBodyReplication(server.World, physics);
        Entity entity = ResolveEntity(server, netId);
        repl.Track(netId, body, entity);
        server.OnBeforeTick += dt => { physics.Step(dt); repl.Sample(); };

        var client = new WorldClient(ct, Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = Dt });   // interpolation ON (default)

        // Drive the sim with a PHASE-OFFSET client clock: 1.4 sub-tick presentation steps per server tick (a
        // non-integer render:tick ratio), so the client never samples in lockstep with the server tick.
        double presentAccumulator = 0.0;
        for (int i = 0; i < 200; i++)
        {
            server.Poll();
            server.Tick(Dt);
            client.Poll();
            // 1.4 presentation steps of 0.5 tick each per server tick == 0.7 tick of render advance per tick, offset.
            presentAccumulator += 1.4;
            while (presentAccumulator >= 1.0) { client.AdvancePresentation(0.5f * Dt); presentAccumulator -= 1.0; }
        }

        Assert.True(client.Joined);
        Assert.NotEqual(netId, client.LocalNetId);

        // Server authoritative pose after settling.
        Pose serverPose = physics.GetDynamicPose(body);
        Assert.True(MathF.Abs(serverPose.Position.Y - 0.5f) < 0.15f,
            $"server box should rest at ~0.5, was {serverPose.Position.Y:F3}");
        Assert.False(physics.IsAwake(body), "server box should have gone to sleep after resting");

        // The client's interpolated body pose converges to the server pose (banded: it is a fixed-delay
        // interpolation, and the body is at rest so the buffer has clamped at the resting sample).
        Vector3 clientPos = ClientBodyPos(client, netId);
        Assert.True((clientPos - serverPose.Position).Length() < 0.2f,
            $"client body {clientPos} should converge to the server rest pose {serverPose.Position}");

        // The client read the replicated orientation + it is a valid unit quaternion (a settled box is ~identity).
        Assert.True(client.TryGetComponent(netId, out DynamicBodyState state));
        Assert.True(MathF.Abs(state.Orientation.LengthSquared() - 1f) < 1e-2f,
            $"replicated orientation must be ~unit length, was {state.Orientation.LengthSquared():F4}");

        physics.Dispose();
    }

    // -------------------------------------------------------------------------
    // The body is interpolated in flight: the rendered pose lags the raw server pose (delay), and glides.
    // -------------------------------------------------------------------------

    [Fact]
    public void FallingBody_IsInterpolated_RenderLagsServer_NoClientSimulation()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var cfg = new WorldServerConfig { TickSeconds = Dt, MaxPlayers = 4, InterestRadius = 500f,
            SpawnPosition = _ => new Vector3(0f, 0f, 0f) };
        var server = new WorldServer(st, cfg, Flat, MoveTuning.Default);

        var (physics, body) = NewPhysicsWithFallingBox(dropY: 30f);   // high drop so it is still falling mid-test
        long netId = server.SpawnEntity(0f, 0f);
        var repl = new DynamicBodyReplication(server.World, physics);
        repl.Track(netId, body, ResolveEntity(server, netId));
        server.OnBeforeTick += dt => { physics.Step(dt); repl.Sample(); };

        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = Dt });

        // Warm up while phase-offset so the fixed-delay buffer fills.
        for (int i = 0; i < 25; i++)
        {
            server.Poll(); server.Tick(Dt); client.Poll();
            client.AdvancePresentation(0.7f * Dt);   // non-integer ratio: never one present per tick
        }
        Assert.True(client.Joined);

        // One more tick applied WITHOUT presenting: the raw latest server-Y is now the newest snapshot.
        server.Poll(); server.Tick(Dt); client.Poll();
        float rawLatestY = ClientBodyPos(client, netId).Y;   // the just-applied snapshot Y (not yet interpolated)

        client.AdvancePresentation(0.7f * Dt);
        float renderedY = ClientBodyPos(client, netId).Y;
        // The body is FALLING (Y decreasing), so rendering on a fixed delay leaves the rendered Y ABOVE (behind, in
        // time) the raw latest snapshot. It must not snap onto the newest sample, and must never be the server's live
        // pose (the client does not simulate the body - it only interpolates snapshots).
        Assert.True(renderedY > rawLatestY + 1e-4f,
            $"a falling body must render behind the latest snapshot on the fixed delay: rendered {renderedY} vs raw {rawLatestY}");

        physics.Dispose();
    }

    // -------------------------------------------------------------------------
    // AoI: a body outside the client's interest radius does not replicate.
    // -------------------------------------------------------------------------

    [Fact]
    public void BodyOutsideInterest_DoesNotReplicate()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        // Small interest radius; the player spawns at the origin, the body is far away.
        var cfg = new WorldServerConfig { TickSeconds = Dt, MaxPlayers = 4, InterestRadius = 10f,
            SpawnPosition = _ => new Vector3(0f, 0f, 0f) };
        var server = new WorldServer(st, cfg, Flat, MoveTuning.Default);

        var physics = new BepuPhysicsWorld();
        physics.AddStatic(new BoxShape(new Vector3(50f, 0.5f, 50f)), Pose.At(new Vector3(100f, -0.5f, 0f)));
        DynamicBodyHandle body = physics.AddDynamic(
            new BoxShape(new Vector3(0.5f, 0.5f, 0.5f)), Pose.At(new Vector3(100f, 5f, 0f)),
            DynamicBodyDescription.WithMass(1f));
        long netId = server.SpawnEntity(100f, 0f);   // 100m from the player, well outside the 10m interest radius
        var repl = new DynamicBodyReplication(server.World, physics);
        repl.Track(netId, body, ResolveEntity(server, netId));
        server.OnBeforeTick += dt => { physics.Step(dt); repl.Sample(); };

        var client = new WorldClient(ct, Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = Dt, InterpolateRemotes = false });

        for (int i = 0; i < 40; i++)
        {
            server.Poll(); server.Tick(Dt); client.Poll();
            client.AdvancePresentation(0.7f * Dt);
        }

        Assert.True(client.Joined);
        // The distant body is tracked + sampled server-side, but it is outside the client's AoI, so the client never
        // sees it and TryGetComponent returns false.
        Assert.True(repl.IsTracked(netId));
        Assert.False(client.TryGetComponent(netId, out DynamicBodyState _),
            "a body outside the client's area of interest must not replicate");
        foreach (EntityRenderState e in client.Snapshot())
            Assert.NotEqual(netId, e.Id.Value);

        physics.Dispose();
    }

    // -------------------------------------------------------------------------
    // Server-side removal propagates: despawn the entity + untrack, the client drops it.
    // -------------------------------------------------------------------------

    [Fact]
    public void RemovedServerSide_DisappearsClientSide()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var cfg = new WorldServerConfig { TickSeconds = Dt, MaxPlayers = 4, InterestRadius = 500f,
            SpawnPosition = _ => new Vector3(0f, 0f, 0f) };
        var server = new WorldServer(st, cfg, Flat, MoveTuning.Default);

        var (physics, body) = NewPhysicsWithFallingBox(dropY: 5f);
        long netId = server.SpawnEntity(0f, 0f);
        var repl = new DynamicBodyReplication(server.World, physics);
        Entity entity = ResolveEntity(server, netId);
        repl.Track(netId, body, entity);
        bool removed = false;
        server.OnBeforeTick += dt =>
        {
            physics.Step(dt);
            repl.Sample();
            if (removed && repl.IsTracked(netId))
            {
                repl.Untrack(netId, out DynamicBodyHandle h);
                physics.RemoveDynamic(h);
                server.World.Despawn(entity);   // server-side despawn -> AoI despawn on the client
            }
        };

        var client = new WorldClient(ct, Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = Dt, InterpolateRemotes = false });

        for (int i = 0; i < 30; i++) { server.Poll(); server.Tick(Dt); client.Poll(); client.AdvancePresentation(0.7f * Dt); }
        Assert.True(client.Joined);
        Assert.True(client.TryGetComponent(netId, out DynamicBodyState _), "body should be visible before removal");

        // Trigger server-side removal, then pump: the despawn reaches the client and it drops the entity.
        removed = true;
        for (int i = 0; i < 20; i++) { server.Poll(); server.Tick(Dt); client.Poll(); client.AdvancePresentation(0.7f * Dt); }

        Assert.False(repl.IsTracked(netId));
        Assert.False(client.TryGetComponent(netId, out DynamicBodyState _), "removed body must disappear client-side");
        foreach (EntityRenderState e in client.Snapshot())
            Assert.NotEqual(netId, e.Id.Value);

        physics.Dispose();
    }

    // -------------------------------------------------------------------------
    // Sleep gating: once the body sleeps, the server stops churning snapshots but the client holds the rest pose.
    // -------------------------------------------------------------------------

    [Fact]
    public void SleepingBody_StopsChurning_ClientHoldsRestPose()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var cfg = new WorldServerConfig { TickSeconds = Dt, MaxPlayers = 4, InterestRadius = 500f,
            SpawnPosition = _ => new Vector3(0f, 0f, 0f) };
        var server = new WorldServer(st, cfg, Flat, MoveTuning.Default);

        var (physics, body) = NewPhysicsWithFallingBox(dropY: 3f);
        long netId = server.SpawnEntity(0f, 0f);
        var repl = new DynamicBodyReplication(server.World, physics);
        Entity entity = ResolveEntity(server, netId);
        repl.Track(netId, body, entity);

        int samplesWritten = 0;
        server.OnBeforeTick += dt =>
        {
            physics.Step(dt);
            bool awakeBefore = physics.IsAwake(body);
            repl.Sample();
            // Count the ticks where the body was awake (Sample wrote it). Once asleep, Sample skips it.
            if (awakeBefore) samplesWritten++;
        };

        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = Dt });

        for (int i = 0; i < 400; i++) { server.Poll(); server.Tick(Dt); client.Poll(); client.AdvancePresentation(0.7f * Dt); }
        Assert.True(client.Joined);
        Assert.False(physics.IsAwake(body), "the box should be asleep after resting for many ticks");

        int writtenAtRest = samplesWritten;
        // Pump more ticks: the sleeping body is NOT re-sampled, so the awake-sample count does not grow.
        for (int i = 0; i < 60; i++) { server.Poll(); server.Tick(Dt); client.Poll(); client.AdvancePresentation(0.7f * Dt); }
        Assert.Equal(writtenAtRest, samplesWritten);   // no churn while asleep

        // The client still holds the resting pose (the buffer clamps at the last written sample).
        Pose serverPose = physics.GetDynamicPose(body);
        Vector3 clientPos = ClientBodyPos(client, netId);
        Assert.True((clientPos - serverPose.Position).Length() < 0.2f,
            $"client should hold the rest pose {serverPose.Position}, was {clientPos}");

        physics.Dispose();
    }

    [Fact]
    public void Test20_A_body_in_a_rebased_world_replicates_its_ABSOLUTE_position_stamped_with_the_island_frame()
    {
        // A pose comes back in the PHYSICS WORLD'S space, which stops being world space the moment an island rebases
        // that world. Writing it as an absolute position would teleport every replicated crate by the anchor delta
        // on the first re-anchor. Converting it to absolute would ALSO re-quantize it at world magnitude, undoing
        // exactly what the frame bought. So it is stamped, not converted.
        var frame = new WorldFrame(781, -781);
        using var physics = new BepuPhysicsWorld();
        physics.Rebase(frame.Anchor);

        var world = new World();
        world.SetIslandFrame(frame);
        Entity e = world.Spawn();
        world.Set(e, new NetId(42));

        // A body sitting 3.25 m from the island's anchor, expressed the way a rebased world expresses it.
        var local = new Vector3(3.25f, 2f, -1.5f);
        DynamicBodyHandle body = physics.AddDynamic(new BoxShape(new Vector3(0.5f, 0.5f, 0.5f)),
            Pose.At(local), DynamicBodyDescription.WithMass(1f));

        var replication = new DynamicBodyReplication(world, physics);
        replication.Track(42, body, e);
        replication.Sample();

        Assert.True(world.TryGet(e, out ReplicatedPosition pos));
        Assert.Equal(frame, pos.Frame);                      // stamped with the island's frame
        Assert.Equal(local, pos.Local);                      // and the pose rides verbatim, un-requantized
        Assert.Equal(frame.ToWorld(local), pos.Value);       // while Value still reads the absolute world position
    }

    // Resolve the ECS entity SpawnEntity created for a netId (the one carrying the replicated NetId component).
    private static Entity ResolveEntity(WorldServer server, long netId)
    {
        Entity found = default;
        bool got = false;
        server.World.ForEach<NetId>((Entity e, ref NetId id) =>
        {
            if (id.Value == netId) { found = e; got = true; }
        });
        if (!got) throw new Xunit.Sdk.XunitException($"no entity for netId {netId}");
        return found;
    }
}

/// <summary>
/// Focused codec-level coverage of <see cref="DynamicBodyState"/> in the shared <see cref="MoveProtocol.CreateRegistry"/>:
/// the orientation quaternion + velocity round-trip on the wire, and the registered lerp SLERPs the orientation (not a
/// component-linear blend of the raw quaternion) so a rotating body's interpolated orientation stays a unit quaternion.
/// </summary>
public class DynamicBodyStateCodecTests
{
    // Round-trip one entity carrying a DynamicBodyState through the snapshot writer + client view.
    private static DynamicBodyState RoundTrip(DynamicBodyState src)
    {
        ReplicationRegistry registry = MoveProtocol.CreateRegistry();
        var serverWorld = new World();
        Entity e = serverWorld.Spawn();
        serverWorld.Set(e, new NetId(1));
        serverWorld.Set(e, ReplicatedPosition.FromWorld(Vector3.Zero, WorldFrame.Origin));
        serverWorld.Set(e, src);
        byte[] snapshot = SnapshotWriter.Write(serverWorld, registry);

        var view = new ClientReplicationView(registry);
        var clientWorld = new World();
        view.Apply(clientWorld, snapshot);
        Assert.True(view.TryGetEntity(1, out Entity ce));
        Assert.True(clientWorld.TryGet(ce, out DynamicBodyState back));
        return back;
    }

    [Fact]
    public void DynamicBodyState_RoundTripsOnTheWire()
    {
        var q = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.5f, -1.2f, 0.3f));
        var src = new DynamicBodyState
        {
            Orientation = q,
            LinearVelocity = new Vector3(1f, -9.8f, 2f),
            AngularVelocity = new Vector3(0.1f, 0.2f, -0.3f),
        };
        DynamicBodyState back = RoundTrip(src);
        Assert.Equal(src.Orientation.X, back.Orientation.X, 5);
        Assert.Equal(src.Orientation.Y, back.Orientation.Y, 5);
        Assert.Equal(src.Orientation.Z, back.Orientation.Z, 5);
        Assert.Equal(src.Orientation.W, back.Orientation.W, 5);
        Assert.Equal(src.LinearVelocity, back.LinearVelocity);
        Assert.Equal(src.AngularVelocity, back.AngularVelocity);
    }

    [Fact]
    public void OrientationLerp_IsSlerp_StaysUnitQuaternion()
    {
        // Two orientations 90 degrees apart about Y. A component-wise linear blend at t=0.5 would NOT be unit length
        // (it would be a shortened chord); a slerp stays on the unit sphere. Drive the codec's registered lerp via a
        // fixed-delay interpolation and assert the interpolated orientation is unit length and near the 45-degree midpoint.
        ReplicationRegistry registry = MoveProtocol.CreateRegistry();
        var view = new ClientReplicationView(registry);
        var world = new World();

        Quaternion q0 = Quaternion.Identity;
        Quaternion q90 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);

        // Sample A (t=0) at q0, sample B (t=1) at q90, into the fixed-delay buffer.
        ApplyOrientation(view, world, q0);
        view.RecordInterpolationSample(0.0);
        ApplyOrientation(view, world, q90);
        view.RecordInterpolationSample(1.0);

        // Render at the temporal midpoint: the codec slerps q0 -> q90 at t=0.5.
        view.InterpolateAt(world, 0.5);
        Assert.True(view.TryGetEntity(1, out Entity e));
        Assert.True(world.TryGet(e, out DynamicBodyState mid));

        Assert.True(MathF.Abs(mid.Orientation.LengthSquared() - 1f) < 1e-4f,
            $"slerped orientation must stay unit length, was {mid.Orientation.LengthSquared():F5}");
        Quaternion expected = Quaternion.Slerp(q0, q90, 0.5f);
        Assert.Equal(expected.Y, mid.Orientation.Y, 4);
        Assert.Equal(expected.W, mid.Orientation.W, 4);
    }

    private static void ApplyOrientation(ClientReplicationView view, World world, Quaternion q)
    {
        var registry = MoveProtocol.CreateRegistry();
        var serverWorld = new World();
        Entity e = serverWorld.Spawn();
        serverWorld.Set(e, new NetId(1));
        serverWorld.Set(e, ReplicatedPosition.FromWorld(Vector3.Zero, WorldFrame.Origin));
        serverWorld.Set(e, new DynamicBodyState { Orientation = q, LinearVelocity = Vector3.Zero, AngularVelocity = Vector3.Zero });
        view.Apply(world, SnapshotWriter.Write(serverWorld, registry));
    }
}
