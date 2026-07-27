using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Netcode;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The sharded head's island frames: one per <see cref="CellSim"/>, fixed at the cell's centre, each with its own
/// physics world. This is the shape a 100 km world with players spread across it needs, and the shape a single
/// shared physics world cannot provide - two entities a grid step apart would query the same colliders from spaces
/// 128 m from one another.
/// </summary>
public class ShardedFrameAnchoringTests
{
    private const float Cell = 60f;
    private const float Dt = 1f / 30f;
    private static readonly Func<float, float, float> Flat = (x, z) => 0f;
    private static readonly MoveTuning Unit = MoveTuning.Default;

    // 100 km out on both planar axes: the offset the whole design is sized against.
    private static readonly Vector3 Far = new(100_000f, 0f, 100_000f);

    private static WorldFrame FrameOf(CellCoord coord) =>
        WorldFrame.Nearest((coord.X + 0.5f) * Cell, (coord.Y + 0.5f) * Cell);

    private static ShardedWorldServerConfig Config(bool frameAnchoring, Func<int, Vector3>? spawn = null,
        Func<CellCoord, IPhysicsWorld>? physics = null) => new()
        {
            TickSeconds = Dt,
            CellSize = Cell,
            OverlapMargin = 24f,
            InterestRadius = 24f,
            MaxPlayers = 8,
            FrameAnchoring = frameAnchoring,
            SpawnPosition = spawn,
            PhysicsWorldFactory = physics,
        };

    [Fact]
    public void A_cells_frame_is_the_frame_nearest_its_centre_and_the_engine_agrees_with_the_factory_contract()
    {
        (LoopbackTransport st, _) = LoopbackTransport.CreatePair();
        var server = new ShardedWorldServer(st, Config(frameAnchoring: true), Flat, Unit);

        // The value a PhysicsWorldFactory has to express its poses against, and the value the engine computes.
        foreach (CellCoord coord in new[] { new CellCoord(0, 0), new CellCoord(1, 0), new CellCoord(1666, 1666) })
            Assert.Equal(FrameOf(coord), server.FrameFor(coord));

        // Cell (0,0)'s centre is (30, 30), which rounds to the world origin, so a game near the origin is unframed
        // in practice and byte-identical to the pre-frame engine.
        Assert.Equal(WorldFrame.Origin, server.FrameFor(new CellCoord(0, 0)));
        Assert.NotEqual(WorldFrame.Origin, server.FrameFor(new CellCoord(1666, 1666)));
    }

    [Fact]
    public void Frame_anchoring_off_leaves_every_cell_at_the_world_origin()
    {
        (LoopbackTransport st, _) = LoopbackTransport.CreatePair();
        var server = new ShardedWorldServer(st, Config(frameAnchoring: false), Flat, Unit);
        Assert.Equal(WorldFrame.Origin, server.FrameFor(new CellCoord(1666, 1666)));
    }

    [Fact]
    public void Test21_Two_players_in_cells_with_different_frames_each_hit_their_OWN_cells_collider()
    {
        // The section-3 blocker as a test. Both players step in the same tick, each in its own cell's frame, each
        // querying its own cell's physics world. Under one shared physics world the far player's wall would sit
        // 99,968 m from where that player is standing and it would walk straight through.
        //
        // The wall is registered at the SAME frame-local offset in both cells, so if the frame and the physics
        // world ever came apart the far player would be the one that fails.
        // Both start at the same offset INSIDE their own cell, with 25 m of room behind them to the -Z border, so
        // neither hands off mid-walk and the two runs are geometrically identical bar the anchor.
        var nearCoord = new CellCoord(0, 0);
        var farCoord = new CellCoord((int)MathF.Floor(Far.X / Cell), (int)MathF.Floor(Far.Z / Cell));
        Vector3 near = StandingPointIn(nearCoord);
        Vector3 far = StandingPointIn(farCoord);
        var built = new Dictionary<CellCoord, IPhysicsWorld>();

        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var config = Config(frameAnchoring: true, spawn: slot => slot == 0 ? near : far, physics: coord =>
        {
            WorldFrame frame = FrameOf(coord);
            var world = new BepuPhysicsWorld();
            if (frame != WorldFrame.Origin) world.Rebase(frame.Anchor);   // the factory contract: poses are frame-local
            Vector3 wallCentre = frame.ToLocal(WallCentreFor(coord));
            world.AddStatic(new BoxShape(new Vector3(20f, 3f, 0.125f)), Pose.At(wallCentre));
            world.Step(Dt);
            built[coord] = world;
            return world;
        });

        var server = new ShardedWorldServer(st, config, Flat, Unit);
        var client = new WorldClient(ct, Flat, Unit, new WorldClientConfig { TickSeconds = Dt });
        using (client)
        {
            for (int i = 0; i < 10 && !client.Joined; i++) { server.Poll(); server.Tick(Dt); client.Poll(); }
            Assert.True(client.Joined);

            // The far half is an entity rather than a second session: what is under test is the per-cell step, not
            // the session layer. It walks under the SAME server.Tick as the joined player, so both cells are stepped
            // in the same tick by their own PlayerMovementSystem instances, which is the point.
            var forward = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f);
            long npc = server.SpawnEntity(far.X, far.Z);
            Assert.True(server.Host.TryGetOwner(npc, out CellSim owner, out Entity e));
            Assert.Equal(farCoord, owner.Coord);
            Assert.Equal(FrameOf(farCoord), owner.Frame);
            Assert.Same(built[farCoord], owner.Physics);
            Assert.Equal(owner.Frame.Anchor, owner.Physics!.Origin);
            Assert.NotEqual(WorldFrame.Origin, owner.Frame);   // or the far half of this test proves nothing
            owner.World.Set(e, new MovementState { Grounded = true });
            server.OnBeforeTick += _ => owner.World.Set(e, new PendingMove { Command = forward });

            for (int i = 0; i < 90; i++) { client.SendInput(forward); server.Poll(); server.Tick(Dt); client.Poll(); }

            Assert.True(server.TryGetPlayerState(0, out PlayerMoveState nearState));
            float nearTravel = near.Z - nearState.Position.Z;   // forward is -Z
            Assert.True(nearTravel > 1f, $"the near player barely moved ({nearTravel:F3} m), so nothing is proven");
            Assert.True(nearTravel < 4f, $"the near player passed its own wall (travelled {nearTravel:F3} m)");

            Assert.True(owner.World.TryGet(e, out ReplicatedPosition pos));
            Assert.Equal(owner.Frame, pos.Frame);
            float farTravel = far.Z - pos.Value.Z;
            Assert.True(farTravel > 1f, $"the far entity barely moved ({farTravel:F3} m), so nothing is proven");
            Assert.True(farTravel < 4f,
                $"the far entity walked {farTravel:F3} m and passed its own cell's wall: the cell's frame and its "
              + "physics world are not in the same space.");
        }

        // Where the walker stands inside a cell, in ABSOLUTE world coordinates.
        static Vector3 StandingPointIn(CellCoord coord) => new(coord.X * Cell + 30f, 0f, coord.Y * Cell + 30f);

        // 3 m in front of that (forward is -Z), also absolute: the factory converts into the cell's frame.
        static Vector3 WallCentreFor(CellCoord coord)
        {
            Vector3 stand = StandingPointIn(coord);
            return new Vector3(stand.X, 1.5f, stand.Z - 3f);
        }
    }

    [Fact]
    public void Test24_A_ghost_carries_the_MIRRORING_cells_frame_not_the_source_cells()
    {
        // The fifth door, and the only one the step loop's self-heal cannot cover: PlayerMovementSystem skips ghosts
        // by design, so a wrong stamp here would persist for the ghost's whole life. Every engine path keys on Value
        // and survives it; a consumer reading Local for cross-border collision - which is what Ghost's own doc
        // invites - would be a frame-width out.
        (LoopbackTransport st, _) = LoopbackTransport.CreatePair();
        var server = new ShardedWorldServer(st, Config(frameAnchoring: true), Flat, Unit);

        // Cell (1,0)'s frame anchor is (128, 0) and cell (2,0)'s is also 128... pick a pair whose frames differ.
        var sourceCoord = new CellCoord(0, 0);
        var mirrorCoord = new CellCoord(1, 0);
        Assert.NotEqual(FrameOf(sourceCoord), FrameOf(mirrorCoord));

        // An entity 1 m inside cell (0,0)'s eastern border, so it mirrors into cell (1,0).
        var at = new Vector3(59f, 0f, 30f);
        long netId = server.SpawnEntity(at.X, at.Z);
        server.Host.EnsureCell(mirrorCoord);
        server.Host.SyncGhosts();

        Assert.True(server.Host.TryGetCell(sourceCoord, out CellSim source));
        Assert.True(server.Host.TryGetCell(mirrorCoord, out CellSim mirror));
        Assert.True(source.TryGetOwned(netId, out Entity owned));
        Assert.True(mirror.TryGetGhost(netId, out Entity ghost));

        Assert.True(source.World.TryGet(owned, out ReplicatedPosition sourcePos));
        Assert.True(mirror.World.TryGet(ghost, out ReplicatedPosition ghostPos));

        Assert.Equal(mirror.Frame, ghostPos.Frame);                      // the DESTINATION cell's frame
        Assert.NotEqual(sourcePos.Frame, ghostPos.Frame);                // and it really did have to convert
        Assert.Equal(sourcePos.Value, ghostPos.Value);                   // bit-identical absolute position

        // Idempotent: it always converts the value the sync pass just applied, never a previously converted one.
        server.Host.SyncGhosts();
        Assert.True(mirror.TryGetGhost(netId, out Entity ghost2));
        Assert.True(mirror.World.TryGet(ghost2, out ReplicatedPosition again));
        Assert.Equal(mirror.Frame, again.Frame);
        Assert.Equal(sourcePos.Value, again.Value);
    }

    [Fact]
    public void A_handoff_lands_the_entity_in_the_destination_cells_frame_within_half_a_ulp()
    {
        // The conversion happens where the component LANDS. Exact to half a ULP of the destination magnitude rather
        // than bit-exact, because a crossing can grow the local's magnitude across a binade boundary.
        (LoopbackTransport st, _) = LoopbackTransport.CreatePair();
        var server = new ShardedWorldServer(st, Config(frameAnchoring: true), Flat, Unit);

        long netId = server.SpawnEntity(59f, 30f);                       // cell (0,0)
        Assert.True(server.Host.TryGetOwner(netId, out CellSim before, out Entity e));
        Assert.Equal(new CellCoord(0, 0), before.Coord);

        var moved = new Vector3(60.1f, 0f, 30f);                         // one step over into cell (1,0)
        before.World.Set(e, ReplicatedPosition.FromWorld(moved, before.Frame));
        server.Host.ProcessHandoffs();

        Assert.True(server.Host.TryGetOwner(netId, out CellSim after, out Entity moved2));
        Assert.Equal(new CellCoord(1, 0), after.Coord);
        Assert.True(after.World.TryGet(moved2, out ReplicatedPosition pos));
        Assert.Equal(after.Frame, pos.Frame);
        Assert.True(Vector3.Distance(pos.Value, moved) <= MathF.Pow(2f, -18f),
            $"the handoff moved the absolute position by {Vector3.Distance(pos.Value, moved)} m");
    }

    [Fact]
    public void A_cell_size_past_the_float32_divergence_ceiling_is_refused_with_the_derivation_in_the_message()
    {
        // CellSize 600 with the default OverlapMargin clears a 512 m PER-AXIS ceiling comfortably (388 m) while its
        // PLANAR magnitude of 549 m does not, which is exactly why the guard carries the sqrt(2).
        (LoopbackTransport st, _) = LoopbackTransport.CreatePair();
        var big = new ShardedWorldServerConfig { CellSize = 600f, OverlapMargin = 24f, InterestRadius = 24f };
        ArgumentException ex = Assert.Throws<ArgumentException>(() => new ShardedWorldServer(st, big, Flat, Unit));
        Assert.Contains("planar magnitude", ex.Message, StringComparison.Ordinal);
        Assert.Contains("512", ex.Message, StringComparison.Ordinal);

        // Unframed, the ceiling does not apply: there is no frame-local coordinate for it to bound.
        var unframed = new ShardedWorldServerConfig
        {
            CellSize = 600f, OverlapMargin = 24f, InterestRadius = 24f, FrameAnchoring = false,
        };
        _ = new ShardedWorldServer(st, unframed, Flat, Unit);

        // And the default 60 / 24 sits well inside it, under either operand.
        _ = new ShardedWorldServer(st, Config(frameAnchoring: true), Flat, Unit);
    }

    [Fact]
    public void A_framed_walk_at_100km_tracks_the_same_walk_at_the_origin_and_an_unframed_one_does_not()
    {
        // The sharded mirror of the flat head's discriminator. Same 300-tick command stream walked three ways, and
        // what is compared is the DISTANCE TRAVELLED, which is what the coordinate's quantum degrades. The origin
        // run is a reference rather than ground truth (it accumulates its own error), which is why the assertion is
        // a RATIO: what it pins is that framing collapses the far run's deviation to a small fraction of the
        // unframed one's, which is the claim the release actually makes.
        float atOrigin = TravelledZ(frameAnchoring: false, Vector3.Zero);
        float framed = TravelledZ(frameAnchoring: true, Far);
        float unframed = TravelledZ(frameAnchoring: false, Far);

        Assert.True(atOrigin > 10f, $"the players must actually have walked, but only moved {atOrigin:F3} m");
        float framedError = MathF.Abs(framed - atOrigin);
        float unframedError = MathF.Abs(unframed - atOrigin);

        Assert.True(unframedError > 0.05f,
            $"the unframed run at 100 km deviated by only {unframedError * 1000f:F1} mm, so this scenario no longer "
          + "distinguishes the two paths and the assertion below proves nothing: walk further, or longer.");
        Assert.True(framedError * 10f < unframedError,
            $"framed deviated {framedError * 1000f:F1} mm from the origin run against the unframed run's "
          + $"{unframedError * 1000f:F1} mm: the per-cell island frame is not buying the precision it exists for.");

        static float TravelledZ(bool frameAnchoring, Vector3 spawn)
        {
            var forward = new MoveCommand(new Vector2(0f, 1f), run: true, cameraYaw: 0f);
            (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
            var config = Config(frameAnchoring, _ => spawn);
            var server = new ShardedWorldServer(st, config, Flat, Unit);
            var client = new WorldClient(ct, Flat, Unit, new WorldClientConfig { TickSeconds = Dt });
            using (client)
            {
                for (int i = 0; i < 10 && !client.Joined; i++) { server.Poll(); server.Tick(Dt); client.Poll(); }
                Assert.True(client.Joined);
                for (int i = 0; i < 300; i++)
                {
                    client.SendInput(forward);
                    server.Poll();
                    server.Tick(Dt);
                    client.Poll();
                }
                Assert.True(server.TryGetPlayerState(0, out PlayerMoveState state));
                if (frameAnchoring)
                {
                    // And it really was stepping small: the local stayed inside a cell-sized neighbourhood of its
                    // anchor, which is the mechanism the precision above comes from.
                    Assert.True(server.TryGetPlayerNetId(0, out long netId));
                    Assert.True(server.Host.TryGetOwner(netId, out CellSim cell, out Entity e));
                    Assert.True(cell.World.TryGet(e, out ReplicatedPosition pos));
                    Assert.Equal(cell.Frame, pos.Frame);
                    Assert.True(MathF.Abs(pos.Local.Z) <= Cell / 2f + WorldFrame.Grid / 2f + 1f,
                        $"local Z was {pos.Local.Z}");
                }
                return MathF.Abs(state.Position.Z - spawn.Z);
            }
        }
    }
}
