using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Dungeon;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Dungeon
{
    // Behavioral proof that a stair run is actually WALKABLE and EXITABLE: a capsule driven forward from the
    // base of a generated stair, resolving against the DungeonStamp collision statics in a real Bepu physics
    // world (exactly how ControllerOnPhysicsTests drives CharacterMovement), must climb the ramp and emerge
    // onto the UPPER floor - it must not slide back down (razor's-edge 45-degree ramp) and must not wedge its
    // head under a landing slab part-way up. Uses MoveTuning.Default (45-degree MaxSlope), so it proves the
    // stair is walkable at the ENGINE default with no per-game slope override.
    public class DungeonStairWalkTests
    {
        static DungeonConfig FloorsConfig() => new()
        {
            MaxFloors = 3,
            RoomCountTarget = 16,
            LockCount = 0,
            BossRoom = false,
            LoopEdgeBudget = 0,
        };

        // The first seed whose growth carves a stair edge, so the walk always exercises a real cross-floor run.
        static DungeonLayout StairLayout()
        {
            for (ulong seed = 11; seed <= 60; seed++)
            {
                DungeonLayout layout = DungeonGenerator.Generate(FloorsConfig(), seed);
                if (layout.Edges.Any(e => e.Kind == DungeonEdgeKind.Stair))
                {
                    return layout;
                }
            }

            throw new Xunit.Sdk.XunitException("No stair edge was produced across seeds 11..60.");
        }

        [Fact]
        public void CharacterWalksUpStair_EmergesOnUpperFloor()
        {
            DungeonLayout layout = StairLayout();
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(0f, 0f, 0f, 0f); // identity transform: plot origin at world origin

            DungeonStampResult stamp = DungeonStamp.Build(layout, kit, plot);

            using IPhysicsWorld world = new BepuPhysicsWorld();
            foreach ((PhysicsShape shape, Pose pose) in stamp.Statics)
            {
                world.AddStatic(shape, pose);
            }

            world.Step(1f / 60f); // prime the broad phase (statics don't move, so one step is enough)

            float cell = layout.CellSizeMeters;
            float floorHeight = layout.FloorHeightMeters;

            DungeonEdge stair = layout.Edges.First(e => e.Kind == DungeonEdgeKind.Stair);

            // Start at the source-room door (Doors[0]) on the lower floor and drive toward the bottom tread
            // (Path[0]) and up the run. The door -> lower-tread step gives the climb direction robustly for any
            // run length. Both are colinear with the whole ascent.
            DungeonTile doorA = stair.Doors[0];
            DungeonTile lower = stair.Path[0];
            (float dax, _, float daz) = plot.TileCenter(doorA, cell, floorHeight);
            (float lx, _, float lz) = plot.TileCenter(lower, cell, floorHeight);

            var climb = new Vector2(lx - dax, lz - daz);
            Assert.True(climb.LengthSquared() > 1e-6f, "door and bottom tread must be distinct cells");
            climb = Vector2.Normalize(climb);

            // Camera basis: forward = (-sin yaw, 0, -cos yaw). Pick a yaw whose forward equals the climb dir so a
            // pure +Y (forward) move command walks straight up the run.
            float yaw = MathF.Atan2(-climb.X, -climb.Y);

            MoveTuning tuning = MoveTuning.Default; // 45-degree MaxSlope, 1.8 m capsule, walk 6 m/s
            float halfH = tuning.CapsuleHalfHeight;
            float lowerFloorY = plot.BaseY + lower.Floor * floorHeight;
            float upperFloorY = plot.BaseY + (lower.Floor + 1) * floorHeight;

            var state = new MoveState
            {
                Position = new Vector3(dax, lowerFloorY + halfH, daz),
                Grounded = true,
            };
            var forward = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: yaw, jump: false);

            // Flat analytic ground at the lower floor Y (matching RoomDungeon: one fallback floor; Step adds the
            // half-height itself). The physics floor slabs / ramp provide the real per-position support above it.
            float GroundHeight(float x, float z) => lowerFloorY;

            float bestY = state.Position.Y;
            for (int i = 0; i < 600; i++)
            {
                state = CharacterMovement.Step(state, forward, 1f / 60f, GroundHeight, tuning,
                    groundNormal: null, world: world);
                bestY = MathF.Max(bestY, state.Position.Y);
            }

            // Emerged onto the upper floor: the capsule centre is at (upper floor + half-height) when standing on
            // the upper-floor landing/room slab. A slide-back leaves it near the lower floor; a head-wedge leaves
            // it stuck ~2 m below the top. Either fails this by a wide margin.
            float upperStandingY = upperFloorY + halfH;
            Assert.True(state.Position.Y > upperStandingY - 0.25f,
                $"character did not emerge on the upper floor: final Y {state.Position.Y:F3}, expected ~{upperStandingY:F3} " +
                $"(lower floor {lowerFloorY:F3}, upper floor {upperFloorY:F3}); peak Y reached {bestY:F3}");

            // And it genuinely climbed (not merely nudged): well above the lower floor.
            Assert.True(state.Position.Y > lowerFloorY + halfH + floorHeight * 0.5f,
                $"character stayed near the lower floor: final Y {state.Position.Y:F3}");
        }
    }
}
