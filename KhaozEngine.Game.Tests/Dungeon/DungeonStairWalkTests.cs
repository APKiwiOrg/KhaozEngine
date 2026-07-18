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

        // The climb must be SMOOTH: a stair run rises at a steady walking pace, never popping a whole ~0.33 m riser
        // in one tick. Drives the same real dungeon stair as above and asserts BOTH that the character reaches the
        // upper floor (the ascent still completes) AND that no single tick raises it by more than
        // MaxStepClimbSpeed * dt (+ a tiny float epsilon). This FAILS against the pre-fix instant step-up snap
        // (which pops a full riser, ~0.33 m, in one tick) and PASSES once the step-up is rate-limited.
        [Fact]
        public void CharacterWalksUpStair_AscendsAtBoundedRate()
        {
            DungeonLayout layout = StairLayout();
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(0f, 0f, 0f, 0f);

            DungeonStampResult stamp = DungeonStamp.Build(layout, kit, plot);

            using IPhysicsWorld world = new BepuPhysicsWorld();
            foreach ((PhysicsShape shape, Pose pose) in stamp.Statics)
            {
                world.AddStatic(shape, pose);
            }

            world.Step(1f / 60f);

            float cell = layout.CellSizeMeters;
            float floorHeight = layout.FloorHeightMeters;

            DungeonEdge stair = layout.Edges.First(e => e.Kind == DungeonEdgeKind.Stair);
            DungeonTile doorA = stair.Doors[0];
            DungeonTile lower = stair.Path[0];
            (float dax, _, float daz) = plot.TileCenter(doorA, cell, floorHeight);
            (float lx, _, float lz) = plot.TileCenter(lower, cell, floorHeight);

            var climb = new Vector2(lx - dax, lz - daz);
            Assert.True(climb.LengthSquared() > 1e-6f, "door and bottom tread must be distinct cells");
            climb = Vector2.Normalize(climb);
            float yaw = MathF.Atan2(-climb.X, -climb.Y);

            MoveTuning tuning = MoveTuning.Default;
            float halfH = tuning.CapsuleHalfHeight;
            float lowerFloorY = plot.BaseY + lower.Floor * floorHeight;
            float upperFloorY = plot.BaseY + (lower.Floor + 1) * floorHeight;

            const float dt = 1f / 60f;
            float maxRisePerTick = tuning.MaxStepClimbSpeed * dt;
            Assert.True(maxRisePerTick > 0f, "the rate limit must be active for this test");

            var state = new MoveState
            {
                Position = new Vector3(dax, lowerFloorY + halfH, daz),
                Grounded = true,
            };
            var forward = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: yaw, jump: false);

            float GroundHeight(float x, float z) => lowerFloorY;

            float maxUpwardStep = 0f;
            for (int i = 0; i < 600; i++)
            {
                float prevY = state.Position.Y;
                state = CharacterMovement.Step(state, forward, dt, GroundHeight, tuning,
                    groundNormal: null, world: world);
                float upward = state.Position.Y - prevY;   // only upward motion is rate-limited; falling is not
                if (upward > maxUpwardStep)
                {
                    maxUpwardStep = upward;
                }
            }

            // Reached the upper floor: the rate limit slows the climb but never stalls it.
            float upperStandingY = upperFloorY + halfH;
            Assert.True(state.Position.Y > upperStandingY - 0.25f,
                $"character did not emerge on the upper floor: final Y {state.Position.Y:F3}, expected ~{upperStandingY:F3}");

            // No single tick popped more than one tick's climb budget: the ascent is smooth.
            const float epsilon = 0.01f;   // far below a ~0.33 m riser pop, above float noise
            Assert.True(maxUpwardStep <= maxRisePerTick + epsilon,
                $"a tick rose {maxUpwardStep:F4} m, exceeding the {maxRisePerTick:F4} m/tick climb budget " +
                $"(MaxStepClimbSpeed {tuning.MaxStepClimbSpeed} m/s): the step-up is not rate-limited.");
        }

        // The climb must stay GROUNDED the whole way up: the character never reads airborne mid-ascent. This is the
        // assertion CharacterWalksUpStair_AscendsAtBoundedRate lacked. The paced step-up (MoveTuning.MaxStepClimbSpeed)
        // commits the capsule BELOW the tread it is mounting for smoothness; without the grounded-through-the-climb
        // hold in CharacterMovement, step 4's support probe misses that tread on the ticks between mounts, so
        // `Grounded` flips false with a one-tick gravity dip - and RoomDungeon feeds Grounded/VerticalVelocity to the
        // AnimatedCharacter, so those flicker ticks spam the FALL animation on the stairs. This asserts Grounded is
        // TRUE on EVERY tick of the ascent (it never jumps and never leaves a surface, so there is no legitimate
        // airborne tick), AND that the ascent is still smooth (no ~0.33 m riser pop) AND reaches the top. It FAILS
        // against the pre-fix rate-limit-only behaviour (Grounded flickers false for a tick or two between steps) and
        // PASSES with the hold in place.
        [Fact]
        public void CharacterWalksUpStair_StaysGroundedEveryTick()
        {
            DungeonLayout layout = StairLayout();
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(0f, 0f, 0f, 0f);

            DungeonStampResult stamp = DungeonStamp.Build(layout, kit, plot);

            using IPhysicsWorld world = new BepuPhysicsWorld();
            foreach ((PhysicsShape shape, Pose pose) in stamp.Statics)
            {
                world.AddStatic(shape, pose);
            }

            world.Step(1f / 60f);

            float cell = layout.CellSizeMeters;
            float floorHeight = layout.FloorHeightMeters;

            DungeonEdge stair = layout.Edges.First(e => e.Kind == DungeonEdgeKind.Stair);
            DungeonTile doorA = stair.Doors[0];
            DungeonTile lower = stair.Path[0];
            (float dax, _, float daz) = plot.TileCenter(doorA, cell, floorHeight);
            (float lx, _, float lz) = plot.TileCenter(lower, cell, floorHeight);

            var climb = new Vector2(lx - dax, lz - daz);
            Assert.True(climb.LengthSquared() > 1e-6f, "door and bottom tread must be distinct cells");
            climb = Vector2.Normalize(climb);
            float yaw = MathF.Atan2(-climb.X, -climb.Y);

            MoveTuning tuning = MoveTuning.Default;
            float halfH = tuning.CapsuleHalfHeight;
            float lowerFloorY = plot.BaseY + lower.Floor * floorHeight;
            float upperFloorY = plot.BaseY + (lower.Floor + 1) * floorHeight;

            const float dt = 1f / 60f;
            float maxRisePerTick = tuning.MaxStepClimbSpeed * dt;

            var state = new MoveState
            {
                Position = new Vector3(dax, lowerFloorY + halfH, daz),
                Grounded = true,
            };
            var forward = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: yaw, jump: false);

            float GroundHeight(float x, float z) => lowerFloorY;

            // The character starts grounded on the lower floor, walks the whole run, climbs, and steps onto the
            // upper landing - it never jumps and never walks off a ledge, so it should be grounded on literally
            // every tick. Any false tick is the climb-flicker jank.
            int airborneTicks = 0;
            int firstAirborneTick = -1;
            float maxUpwardStep = 0f;
            for (int i = 0; i < 600; i++)
            {
                float prevY = state.Position.Y;
                state = CharacterMovement.Step(state, forward, dt, GroundHeight, tuning,
                    groundNormal: null, world: world);

                if (!state.Grounded)
                {
                    airborneTicks++;
                    if (firstAirborneTick < 0) firstAirborneTick = i;
                }

                float upward = state.Position.Y - prevY;
                if (upward > maxUpwardStep) maxUpwardStep = upward;
            }

            // (a) reached the upper floor (the grounded hold never stalls the ascent).
            float upperStandingY = upperFloorY + halfH;
            Assert.True(state.Position.Y > upperStandingY - 0.25f,
                $"character did not emerge on the upper floor: final Y {state.Position.Y:F3}, expected ~{upperStandingY:F3}");

            // (b) THE assertion the previous test missed: grounded on every single tick, no airborne flicker.
            Assert.True(airborneTicks == 0,
                $"character read airborne on {airborneTicks} tick(s) during the stair climb (first at tick " +
                $"{firstAirborneTick}): the paced step-up flickers Grounded false, spamming the fall animation.");

            // (c) still smooth: no single tick popped a whole riser.
            const float epsilon = 0.01f;
            Assert.True(maxUpwardStep <= maxRisePerTick + epsilon,
                $"a tick rose {maxUpwardStep:F4} m, exceeding the {maxRisePerTick:F4} m/tick climb budget: " +
                $"the ascent is not smooth.");
        }
    }
}
