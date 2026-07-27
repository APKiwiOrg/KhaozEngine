using System;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The flat head's island frame, behind <see cref="WorldServerConfig.FrameAnchoring"/> (ON by default since the wire
/// carries the frame stamp). A framed server steps on a frame-local position and rebases its physics world with it,
/// while everything it hands a consumer stays absolute world metres.
/// </summary>
public class WorldServerFrameAnchoringTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    static readonly MoveTuning Unit = MoveTuning.Default with { CapsuleHalfHeight = 0.5f };

    // 100 km out on both planar axes: the offset the whole design is sized against.
    static readonly Vector3 Far = new(100_000f, 0f, 100_000f);

    static (WorldServer server, WorldClient client, WorldServerConfig config) Connect(bool frameAnchoring, Vector3 spawn)
    {
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig
        {
            TickSeconds = 1f / 30f,
            InterestRadius = 500f,
            MaxPlayers = 8,
            FrameAnchoring = frameAnchoring,
            SpawnPosition = _ => spawn,
        };
        var server = new WorldServer(st, config, Flat, Unit);
        var client = new WorldClient(ct, Flat, Unit, new WorldClientConfig { TickSeconds = config.TickSeconds });
        for (int i = 0; i < 10 && !client.Joined; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);
        return (server, client, config);
    }

    [Fact]
    public void Turning_frame_anchoring_off_keeps_the_island_at_the_world_origin()
    {
        (WorldServer server, WorldClient client, WorldServerConfig config) = Connect(frameAnchoring: false, Far);
        using (client)
        {
            server.Tick(config.TickSeconds);
            Assert.Equal(WorldFrame.Origin, server.IslandFrame);
            Assert.True(server.TryGetPlayerState(0, out PlayerMoveState state));
            Assert.Equal(Vector2.Zero, state.FrameAnchor);
            Assert.Equal(Far.X, state.Position.X);
        }
    }

    [Fact]
    public void A_far_spawn_re_anchors_the_island_and_the_local_position_becomes_small()
    {
        (WorldServer server, WorldClient client, WorldServerConfig config) = Connect(frameAnchoring: true, Far);
        using (client)
        {
            server.Tick(config.TickSeconds);

            WorldFrame frame = server.IslandFrame;
            Assert.NotEqual(WorldFrame.Origin, frame);
            Assert.Equal(WorldFrame.Nearest(Far), frame);

            // The component is framed, and its local is inside the re-anchor radius rather than at 100 km. That is
            // the whole point: the carried state stops accumulating at world magnitude.
            Assert.True(server.World.TryGet(PlayerEntity(server), out ReplicatedPosition pos));
            Assert.Equal(frame, pos.Frame);
            Assert.True(MathF.Abs(pos.Local.X) <= WorldFrame.Grid / 2f, $"local X was {pos.Local.X}");
            Assert.True(MathF.Abs(pos.Local.Z) <= WorldFrame.Grid / 2f, $"local Z was {pos.Local.Z}");

            // And every absolute-facing surface still reads absolute.
            Assert.Equal(Far.X, pos.Value.X);
            Assert.True(server.TryGetPlayerState(0, out PlayerMoveState state));
            Assert.Equal(Vector2.Zero, state.FrameAnchor);
            Assert.Equal(Far.X, state.Position.X);
            Assert.Equal(Far.Z, state.Position.Z);
        }
    }

    [Fact]
    public void Test22_OriginFramedAbsolute_IsAlwaysAValidRepresentation_AndTheSelfHealIsExact()
    {
        // {Origin, p} and {f, f.ToLocal(p)} denote the same world position, so an Origin-framed absolute is always a
        // VALID representation rather than a wrong one - which is what makes the self-heal a conversion rather than
        // a repair, and what let the pre-major settable Value be safe on a framed server. It still matters with the
        // setter gone: FromWorld(p, WorldFrame.Origin) is exactly what a consumer writes when it has an absolute
        // position and no frame in hand, and the island has to convert that back EXACTLY.
        (WorldServer framed, WorldClient framedClient, WorldServerConfig config) = Connect(frameAnchoring: true, Far);
        (WorldServer plain, WorldClient plainClient, _) = Connect(frameAnchoring: false, Far);
        using (framedClient)
        using (plainClient)
        {
            framed.Tick(config.TickSeconds);
            plain.Tick(config.TickSeconds);
            Assert.NotEqual(WorldFrame.Origin, framed.IslandFrame);   // or the rest of this proves nothing

            // A consumer OnBeforeTick brain writing an NPC from an absolute position with no frame in hand, at 100 km.
            Vector3 p = Far + new Vector3(37.5f, 2.25f, -18.75f);
            long framedId = framed.SpawnEntity(p.X, p.Z);
            long plainId = plain.SpawnEntity(p.X, p.Z);
            Entity framedEntity = EntityOf(framed, framedId);
            Entity plainEntity = EntityOf(plain, plainId);
            framed.World.Set(framedEntity, ReplicatedPosition.FromWorld(p, WorldFrame.Origin));
            plain.World.Set(plainEntity, ReplicatedPosition.FromWorld(p, WorldFrame.Origin));

            // Half one: the write reads back bit-identically and carries the Origin stamp.
            Assert.True(framed.World.TryGet(framedEntity, out ReplicatedPosition written));
            Assert.Equal(WorldFrame.Origin, written.Frame);
            Assert.Equal(p, written.Value);

            framed.Tick(config.TickSeconds);
            plain.Tick(config.TickSeconds);

            // Half two, the load-bearing one: the self-heal moved the component into the island frame AND the
            // absolute value it reads is still bit-identical - to the write, and to the same tick on an unframed
            // server. That is what proves the conversion is exact rather than merely close.
            Assert.True(framed.World.TryGet(framedEntity, out ReplicatedPosition healed));
            Assert.Equal(framed.IslandFrame, healed.Frame);
            Assert.Equal(framed.IslandFrame.ToLocal(p), healed.Local);
            Assert.Equal(p, healed.Value);
            Assert.True(plain.World.TryGet(plainEntity, out ReplicatedPosition unframed));
            Assert.Equal(unframed.Value, healed.Value);
        }
    }

    [Fact]
    public void A_framed_walk_at_100Km_tracks_the_same_walk_at_the_origin_and_an_unframed_one_does_not()
    {
        // The whole feature, in the smallest form that can show it: the same 300-tick command stream walked three
        // ways, and what is compared is the DISTANCE TRAVELLED, which is what the coordinate's quantum degrades.
        //
        // The origin run is a reference rather than ground truth (it accumulates its own float32 error as the
        // player walks away from zero, which is why the design doc's full acceptance test carries a double-precision
        // trajectory instead). That is exactly why the assertion below is a RATIO and not an absolute tolerance:
        // what it pins is that framing collapses the far run's deviation to a small fraction of the unframed one's,
        // which is the claim the release actually makes.
        float atOrigin = TravelledZ(frameAnchoring: false, Vector3.Zero);
        float framed = TravelledZ(frameAnchoring: true, Far);
        float unframed = TravelledZ(frameAnchoring: false, Far);

        Assert.True(atOrigin > 10f, $"the players must actually have walked, but only moved {atOrigin:F3} m");
        float framedError = MathF.Abs(framed - atOrigin);
        float unframedError = MathF.Abs(unframed - atOrigin);

        Assert.True(unframedError > 0.05f,
            $"the unframed run at 100 km deviated by only {unframedError * 1000f:F1} mm, so this scenario no longer " +
            "distinguishes the two paths and the assertion below proves nothing: walk further, or longer.");
        Assert.True(framedError * 10f < unframedError,
            $"framed deviated {framedError * 1000f:F1} mm from the origin run against the unframed run's " +
            $"{unframedError * 1000f:F1} mm: the island frame is not buying the precision it exists for.");

        float TravelledZ(bool frameAnchoring, Vector3 spawn)
        {
            var forward = new MoveCommand(new Vector2(0f, 1f), run: true, cameraYaw: 0f);
            (WorldServer server, WorldClient client, WorldServerConfig config) = Connect(frameAnchoring, spawn);
            using (client)
            {
                for (int i = 0; i < 300; i++)
                {
                    client.SendInput(forward);
                    server.Poll();
                    server.Tick(config.TickSeconds);
                    client.Poll();
                }
                Assert.True(server.TryGetPlayerState(0, out PlayerMoveState state));
                if (frameAnchoring)
                {
                    // And it really was stepping small: the walk stayed inside one frame's local radius, which is
                    // the mechanism the precision above comes from.
                    Assert.True(server.World.TryGet(PlayerEntity(server), out ReplicatedPosition pos));
                    Assert.True(MathF.Abs(pos.Local.Z) <= WorldFrame.ReanchorRadius + 1f, $"local Z was {pos.Local.Z}");
                }
                return MathF.Abs(state.Position.Z - spawn.Z);
            }
        }
    }

    [Fact]
    public void An_unrebasable_physics_world_is_refused_rather_than_queried_from_the_wrong_space()
    {
        (LoopbackTransport st, _) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { FrameAnchoring = true };
        Assert.Throws<ArgumentException>(() =>
            new WorldServer(st, config, Flat, Unit, physics: new UnrebasableWorld()));

        // Without a physics world at all it is fine: there is no second space to disagree with.
        var ok = new WorldServer(st, config, Flat, Unit);
        Assert.Equal(WorldFrame.Origin, ok.IslandFrame);
    }

    [Fact]
    public void WorldClient_FrameAnchoringOn_ByDefault_RefusesAnUnrebasablePhysicsWorld()
    {
        // The mirror of the server's own guard (An_unrebasable_physics_world_is_refused...), proving
        // WorldClientConfig.FrameAnchoring's new gate did not accidentally loosen the default-on behaviour.
        (LoopbackTransport st, _) = LoopbackTransport.CreatePair();
        Assert.Throws<ArgumentException>(() =>
            new WorldClient(st, Flat, Unit, new WorldClientConfig(), physics: new UnrebasableWorld()));
    }

    [Fact]
    public void WorldClient_FrameAnchoringOff_AllowsAnUnrebasablePhysicsWorld()
    {
        // A consumer whose SERVER also has FrameAnchoring off never receives a frame stamp off the world origin, so
        // its client has nothing to rebase for and should not be forced into a rebasable-only physics world just to
        // keep physics-backed prediction.
        (LoopbackTransport st, _) = LoopbackTransport.CreatePair();
        using var client = new WorldClient(st, Flat, Unit,
            new WorldClientConfig { FrameAnchoring = false }, physics: new UnrebasableWorld());
        Assert.Equal(WorldFrame.Origin, client.IslandFrame);
    }

    [Fact]
    public void WorldClient_FrameAnchoringOff_NeverRebasesEvenAgainstAFramedServer()
    {
        // Belt-and-braces: a consumer that misconfigures the two ends (server framed, client's own FrameAnchoring
        // off) must not crash calling Rebase on a world that cannot - it degrades (the client's physics prediction
        // queries the wrong space, exactly as WorldClientConfig.FrameAnchoring's doc warns), it does not throw.
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var serverConfig = new WorldServerConfig
        {
            TickSeconds = 1f / 30f,
            InterestRadius = 500f,
            MaxPlayers = 8,
            FrameAnchoring = true,
            SpawnPosition = _ => Far,
        };
        var server = new WorldServer(st, serverConfig, Flat, Unit);
        using var client = new WorldClient(ct, Flat, Unit,
            new WorldClientConfig { TickSeconds = serverConfig.TickSeconds, FrameAnchoring = false },
            physics: new UnrebasableWorld());

        for (int i = 0; i < 10 && !client.Joined; i++) { server.Poll(); server.Tick(serverConfig.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);
        Assert.NotEqual(WorldFrame.Origin, server.IslandFrame);   // the far spawn really did re-anchor the server

        // Drive a few more ticks so the client ingests the non-origin frame stamp. No exception is the assertion:
        // an unguarded Rebase call on UnrebasableWorld throws NotSupportedException from IPhysicsWorld's default.
        for (int i = 0; i < 5; i++) { server.Poll(); server.Tick(serverConfig.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);
    }

    static Entity PlayerEntity(WorldServer server)
    {
        Assert.True(server.TryGetPlayerNetId(0, out long netId));
        return EntityOf(server, netId);
    }

    static Entity EntityOf(WorldServer server, long netId)
    {
        Entity found = default;
        bool any = false;
        server.World.ForEach<NetId>((Entity e, ref NetId id) =>
        {
            if (id.Value == netId) { found = e; any = true; }
        });
        Assert.True(any, $"no entity with net id {netId}");
        return found;
    }

    // A backend from before the rebase API: it reports CanRebase false through the seam's default.
    sealed class UnrebasableWorld : IPhysicsWorld
    {
        public StaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null) => default;
        public void RemoveStatic(StaticHandle handle) { }
        public DynamicBodyHandle AddDynamic(PhysicsShape shape, Pose pose, DynamicBodyDescription body, PhysicsMaterial? material = null) => default;
        public void RemoveDynamic(DynamicBodyHandle handle) { }
        public Pose GetDynamicPose(DynamicBodyHandle handle) => Pose.Identity;
        public void GetDynamicVelocity(DynamicBodyHandle handle, out Vector3 linear, out Vector3 angular) { linear = default; angular = default; }
        public void SetDynamicVelocity(DynamicBodyHandle handle, Vector3 linear, Vector3 angular) { }
        public bool IsAwake(DynamicBodyHandle handle) => false;
        public ConstraintHandle AddConstraint(in ConstraintDescription description) => default;
        public void RemoveConstraint(ConstraintHandle handle) { }
        public void SetConstraintTarget(ConstraintHandle handle, float target) { }
        public void Step(float dt) { }
        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit, QueryFilter filter = default) { hit = default; return false; }
        public bool SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction, float maxDistance, out SweepHit hit, QueryFilter filter = default) { hit = default; return false; }
        public bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv) { mtv = default; return false; }
        public void Dispose() { }
    }
}
