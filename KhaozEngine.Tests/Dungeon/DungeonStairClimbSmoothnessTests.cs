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
    // The stair climb must feel like walking a ramp, not lurching up boxes: no fore-aft lurch head-on, no
    // sideways push, and - the case that used to WEDGE - an angled approach must still reach the top by sliding
    // along the shaft wall while climbing square to the treads. Drives CharacterMovement.Step against a real
    // Bepu-collided generated stair, exactly like DungeonStairWalkTests, and measures the per-tick motion
    // decomposed into the climb axis (forward) and its perpendicular (lateral).
    public class DungeonStairClimbSmoothnessTests
    {
        const float Dt = 1f / 60f;

        static DungeonConfig FloorsConfig() => new()
        {
            MaxFloors = 3,
            RoomCountTarget = 16,
            LockCount = 0,
            BossRoom = false,
            LoopEdgeBudget = 0,
        };

        static DungeonLayout StairLayout()
        {
            for (ulong seed = 11; seed <= 60; seed++)
            {
                DungeonLayout layout = DungeonGenerator.Generate(FloorsConfig(), seed);
                if (layout.Edges.Any(e => e.Kind == DungeonEdgeKind.Stair)) return layout;
            }
            throw new Xunit.Sdk.XunitException("No stair edge across seeds 11..60.");
        }

        // Builds the stair world and the climb frame (start pose, climb + lateral axes, base camera yaw aligned
        // with the ascent). Mirrors DungeonStairWalkTests' setup so the two files exercise the same real geometry.
        static (IPhysicsWorld world, Vector3 start, Vector2 climb, Vector2 perp, float axisYaw, float lowerFloorY, float upperFloorY, float halfH)
            BuildClimb()
        {
            DungeonLayout layout = StairLayout();
            DungeonKitMap kit = DungeonKitMap.Greybox();
            var plot = new DungeonPlotTransform(0f, 0f, 0f, 0f);
            DungeonStampResult stamp = DungeonStamp.Build(layout, kit, plot);

            IPhysicsWorld world = new BepuPhysicsWorld();
            foreach ((PhysicsShape shape, Pose pose) in stamp.Statics) world.AddStatic(shape, pose);
            world.Step(Dt);

            float cell = layout.CellSizeMeters;
            float floorHeight = layout.FloorHeightMeters;
            DungeonEdge stair = layout.Edges.First(e => e.Kind == DungeonEdgeKind.Stair);
            DungeonTile doorA = stair.Doors[0];
            DungeonTile lower = stair.Path[0];
            (float dax, _, float daz) = plot.TileCenter(doorA, cell, floorHeight);
            (float lx, _, float lz) = plot.TileCenter(lower, cell, floorHeight);

            var climb = Vector2.Normalize(new Vector2(lx - dax, lz - daz));
            var perp = new Vector2(-climb.Y, climb.X);
            float axisYaw = MathF.Atan2(-climb.X, -climb.Y);

            float halfH = MoveTuning.Default.CapsuleHalfHeight;
            float lowerFloorY = plot.BaseY + lower.Floor * floorHeight;
            float upperFloorY = plot.BaseY + (lower.Floor + 1) * floorHeight;
            var start = new Vector3(dax, lowerFloorY + halfH, daz);
            return (world, start, climb, perp, axisYaw, lowerFloorY, upperFloorY, halfH);
        }

        // Head-on: the ascent must be SMOOTH. Against the pre-fix instant step-up the capsule teleported a full
        // probe length (~one radius) forward on each mount tick and stalled between - a fore-aft lurch. This pins
        // that no single tick advances the capsule along the climb axis by more than a normal walk step, and that
        // it never drifts sideways off the straight climb line. Run at BOTH the 0.4 stair-test radius and the 0.3
        // demo radius: the geometry-robust mount must keep the cap (no lurch) on the stair run at either footprint.
        [Theory]
        [InlineData(0.4f)]
        [InlineData(0.3f)]
        public void HeadOnClimb_IsSmooth_NoLurch_NoLateralDrift(float capsuleRadius)
        {
            var (world, start, climb, perp, axisYaw, _, upperFloorY, halfH) = BuildClimb();
            using (world as IDisposable)
            {
                MoveTuning tuning = MoveTuning.Default with { CapsuleRadius = capsuleRadius };
                var state = new MoveState { Position = start, Grounded = true };
                var cmd = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: axisYaw, jump: false);
                float GroundHeight(float x, float z) => start.Y - halfH;

                float walkStep = tuning.WalkSpeed * Dt;          // the flat-ground per-tick forward budget
                float maxFwdStep = 0f, maxAbsLateral = 0f, prevFwd = 0f;
                for (int i = 0; i < 600; i++)
                {
                    state = CharacterMovement.Step(state, cmd, Dt, GroundHeight, tuning, groundNormal: null, world: world);
                    Vector2 off = new(state.Position.X - start.X, state.Position.Z - start.Z);
                    float fwd = Vector2.Dot(off, climb), lat = Vector2.Dot(off, perp);
                    maxFwdStep = MathF.Max(maxFwdStep, fwd - prevFwd);
                    maxAbsLateral = MathF.Max(maxAbsLateral, MathF.Abs(lat));
                    prevFwd = fwd;
                }

                float upperStandingY = upperFloorY + halfH;
                Assert.True(state.Position.Y > upperStandingY - 0.25f,
                    $"head-on climb did not reach the top: final Y {state.Position.Y:F3}, expected ~{upperStandingY:F3}");
                // No lurch: a step-up mount never jumps forward more than a normal walk step (the pre-fix snap was ~0.4 m).
                Assert.True(maxFwdStep <= walkStep + 0.01f,
                    $"a tick advanced {maxFwdStep:F4} m along the climb, exceeding the {walkStep:F4} m walk step: the step-up lurches.");
                // Straight line: a head-on climb never pushes the capsule sideways.
                Assert.True(maxAbsLateral < 0.05f,
                    $"head-on climb drifted {maxAbsLateral:F4} m sideways: the step-up pushes laterally.");
            }
        }

        // Angled: a player rarely walks exactly along the stair axis. A sustained off-axis approach USED TO wedge -
        // the along-move step-up probe drove into the shaft side wall, failed, and the riser fell through to a
        // wall-slide that killed the forward motion, so the climb stalled part-way. It must instead climb square to
        // the treads and slide along the wall, reaching the top. And the per-tick sideways motion WHILE on the ramp
        // must stay at the honest walk-angle rate (never the amplified full-speed leak the wall-slide produced).
        [Theory]
        [InlineData(8f)]
        [InlineData(15f)]
        public void AngledClimb_ReachesTop_WithoutWedge_OrLateralAmplification(float degrees)
        {
            var (world, start, climb, perp, axisYaw, lowerFloorY, upperFloorY, halfH) = BuildClimb();
            using (world as IDisposable)
            {
                MoveTuning tuning = MoveTuning.Default;
                var state = new MoveState { Position = start, Grounded = true };
                float yaw = axisYaw + degrees * MathF.PI / 180f;
                var cmd = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: yaw, jump: false);
                float GroundHeight(float x, float z) => lowerFloorY;

                // The honest lateral a walk at this angle covers in one tick; the wall-slide leak was several times this.
                float honestLatStep = MathF.Sin(degrees * MathF.PI / 180f) * tuning.WalkSpeed * Dt;
                float maxAbsDLatOnRamp = 0f, prevLat = 0f;
                for (int i = 0; i < 600; i++)
                {
                    state = CharacterMovement.Step(state, cmd, Dt, GroundHeight, tuning, groundNormal: null, world: world);
                    Vector2 off = new(state.Position.X - start.X, state.Position.Z - start.Z);
                    float lat = Vector2.Dot(off, perp);
                    // Only measure while genuinely on the ramp (between the floors), not the flat run-up/-out where
                    // walking at an angle legitimately covers ground sideways.
                    bool onRamp = state.Position.Y > lowerFloorY + halfH + 0.2f && state.Position.Y < upperFloorY + halfH - 0.1f;
                    if (onRamp) maxAbsDLatOnRamp = MathF.Max(maxAbsDLatOnRamp, MathF.Abs(lat - prevLat));
                    prevLat = lat;
                }

                // (a) No wedge: the angled climb still emerges on the upper floor.
                float upperStandingY = upperFloorY + halfH;
                Assert.True(state.Position.Y > upperStandingY - 0.25f,
                    $"angled ({degrees:0} deg) climb wedged: final Y {state.Position.Y:F3}, expected ~{upperStandingY:F3}");
                // (b) No amplification: on the ramp, sideways motion never exceeds the honest walk-angle rate.
                Assert.True(maxAbsDLatOnRamp <= honestLatStep + 0.004f,
                    $"on-ramp lateral {maxAbsDLatOnRamp:F4} m/tick exceeds the honest {honestLatStep:F4} m/tick for {degrees:0} deg: " +
                    "the wall-slide is amplifying the sideways push.");
            }
        }

        // Walking INTO the first riser from the flat lower floor must START the ascent, not vibrate on the spot.
        // Regression pin for the playtest bug: at the DEMO capsule radius (0.3, not the 0.4 the other stair tests use)
        // an off-axis approach into the first step used to buzz in place - the paced step-up's scaled-back forward
        // advance never cleared the riser against the shaft-corner depenetration pushback, so the capsule rose a
        // little, lost the tread, fell back, and repeated, never ascending. Starts a full cell BEFORE the bottom
        // tread on the flat floor and walks in at a range of angles; every one must reach the upper floor.
        [Theory]
        [InlineData(0f)]
        [InlineData(8f)]
        [InlineData(15f)]
        [InlineData(-12f)]
        public void WalkingIntoFirstStair_FromFlatFloor_StartsAscending_NoVibrate(float degrees)
        {
            var (world, doorStart, climb, _, axisYaw, lowerFloorY, upperFloorY, halfH) = BuildClimb();
            using (world as IDisposable)
            {
                // DEMO tuning: capsule radius 0.3 (Room3D/RoomDungeon), and start a whole cell back on the flat floor.
                MoveTuning tuning = MoveTuning.Default with { CapsuleRadius = 0.3f };
                var start = new Vector3(doorStart.X - climb.X, doorStart.Y, doorStart.Z - climb.Y);   // ~1 m before the door
                var state = new MoveState { Position = start, Grounded = true };
                float yaw = axisYaw + degrees * MathF.PI / 180f;
                var cmd = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: yaw, jump: false);
                float GroundHeight(float x, float z) => lowerFloorY;
                Func<float, float, Vector3> flatNormal = (x, z) => Vector3.UnitY;   // the demos pass a flat normal

                for (int i = 0; i < 600; i++)
                    state = CharacterMovement.Step(state, cmd, Dt, GroundHeight, tuning, flatNormal, world);

                float upperStandingY = upperFloorY + halfH;
                Assert.True(state.Position.Y > upperStandingY - 0.25f,
                    $"approaching the first stair at {degrees:0} deg from the flat floor never ascended (vibrated in place): " +
                    $"final Y {state.Position.Y:F3}, expected ~{upperStandingY:F3}");
            }
        }
    }
}
