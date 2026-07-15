using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Dungeon;
using KhaozEngine.Game;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Dungeon
{
    // RoomDungeon (KhaozEngine.Showcase) used to draw its avatar through CharacterAvatar's own presentation-smoothed
    // RenderPosition (a plain ease-toward-physics-height with no knowledge of WHY the height jumped). It has since been
    // ported onto the engine's canonical signal-driven stair glide: CharacterController3D now exposes the sim's own
    // ClimbRate/StepDeltaY facts (KhaozEngine.Game.Render3D/CharacterController3D.cs), and the room feeds them each
    // frame to ReplicatedCharacterAnimators ("the character bridge" - the same bridge RoomNet drives for networked
    // players) as an exact-movement CharacterSample, exactly like this test does.
    //
    // This is an end-to-end pin of that wiring on the REAL generated dungeon geometry (DungeonStamp.Build's stamped
    // stair-step statics, not a synthetic staircase): (1) the new CharacterController3D.ClimbRate seam actually fires
    // during a real ascent (proves the property reads the sim's live state, not a stale default), and (2) the bridge's
    // RenderPosition.Y - what the drawn model and a follow camera target - rises to the top monotonically and rate-
    // bounded from that signal, mirroring StairAscentFeelTests' avatar-composition pins but for the bridge instead of
    // CharacterAvatar. GPU-free: a one-bone parked animator, no Scene3D/mesh involved (RoomDungeon's own Scene3D/GPU
    // wiring is not headlessly testable and is intentionally out of scope here).
    public class DungeonStairSignalGlideTests
    {
        const float Dt = 1f / 60f;

        // Mirrors RoomDungeon's own demo constants (KhaozEngine.Showcase/RoomDungeon.cs).
        const float CapsuleRadius = 0.3f;
        const float CapsuleHalfHeight = 0.9f;

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

        // The stair world plus the climb frame: a start pose one cell BACK on the flat lower floor (walking into the
        // first riser from flat ground), the climb axis, and the camera yaw aligned with the ascent. Mirrors
        // StairAscentFeelTests.BuildClimb exactly (duplicated rather than shared, so this file has no dependency on
        // that one and cannot be destabilized by changes to it).
        static (IPhysicsWorld world, Vector3 start, float axisYaw, float lowerFloorY, float upperFloorY)
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
            float axisYaw = MathF.Atan2(-climb.X, -climb.Y);
            float lowerFloorY = plot.BaseY + lower.Floor * floorHeight;
            float upperFloorY = plot.BaseY + (lower.Floor + 1) * floorHeight;
            var start = new Vector3(dax - climb.X, lowerFloorY + CapsuleHalfHeight, daz - climb.Y);
            return (world, start, axisYaw, lowerFloorY, upperFloorY);
        }

        static InputState Keys(params Key[] down) => new(
            new HashSet<Key>(down), new HashSet<Key>(), new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(),
            Vector2.Zero, Vector2.Zero, 0f, 800, 600);

        static ReplicatedCharacterAnimators NewBridge()
        {
            var skeleton = new Skeleton(new[] { -1 }, new[] { JointPose.Identity }, new[] { 0 }, new[] { 0 });
            AnimationClip Park(string name) => new(name, 1f, new List<JointTrack>
            {
                new JointTrack(0) { Translation = new Vector3Track(new[] { 0f, 1f }, new[] { Vector3.Zero, Vector3.Zero }, InterpolationMode.Linear) },
            });
            var clips = new Dictionary<LocomotionState, AnimationClip>
            {
                [LocomotionState.Idle] = Park("idle"),
                [LocomotionState.Walk] = Park("walk"),
                [LocomotionState.Run] = Park("run"),
                [LocomotionState.Jump] = Park("jump"),
                [LocomotionState.Fall] = Park("fall"),
            };
            // CharacterAnimatorTuning.Default: the reference adopter's tuning (RoomNet, and now RoomDungeon), unmodified.
            return new ReplicatedCharacterAnimators(skeleton, clips, CharacterAnimatorTuning.Default);
        }

        // Drive CharacterController3D + the bridge up the real stair in ONE loop, exactly mirroring RoomDungeon's
        // OnUpdate wiring: exact grounded/vertical/swim signals, CharacterController3D.ClimbRate fed straight through
        // as CharacterSample.ClimbRate, and a locally-accumulated StepDeltaY running sum as StepCumulativeY.
        static (List<float> climbRates, List<float> renderY, List<bool> grounded, float finalPhysY, float upperFloorY)
            DriveClimb(int ticks = 600)
        {
            var (world, start, axisYaw, lowerFloorY, upperFloorY) = BuildClimb();
            using (world as IDisposable)
            {
                var controller = new CharacterController3D { CapsuleHalfHeight = CapsuleHalfHeight, CapsuleRadius = CapsuleRadius };
                controller.SetXZ(start.X, start.Z);
                controller.Update(InputState.Empty, 0f, axisYaw, (x, z) => lowerFloorY, (x, z) => Vector3.UnitY, world);

                ReplicatedCharacterAnimators bridge = NewBridge();
                var samples = new List<CharacterSample>(1);
                float stepCumulativeY = 0f;

                var climbRates = new List<float>();
                var renderY = new List<float>();
                var grounded = new List<bool>();

                for (int i = 0; i < ticks; i++)
                {
                    controller.Update(Keys(Key.W), Dt, axisYaw, (x, z) => lowerFloorY, (x, z) => Vector3.UnitY, world);
                    stepCumulativeY += controller.StepDeltaY;

                    Vector3 feet = new(controller.Position.X, controller.Position.Y - CapsuleHalfHeight, controller.Position.Z);
                    var sample = new CharacterSample(0L, feet, isLocal: true, grounded: controller.Grounded,
                        verticalVelocity: controller.VerticalVelocity, planarSpeed: 6f, swimming: controller.Swimming,
                        climbRate: controller.ClimbRate, stepCumulativeY: stepCumulativeY);
                    samples.Clear();
                    samples.Add(sample);
                    bridge.Update(samples, Dt);

                    climbRates.Add(controller.ClimbRate);
                    renderY.Add(bridge.Live[0].RenderPosition.Y);
                    grounded.Add(controller.Grounded);
                }

                return (climbRates, renderY, grounded, controller.Position.Y, upperFloorY);
            }
        }

        // (1) The seam: CharacterController3D.ClimbRate must actually fire (non-zero) on a meaningful number of ticks
        // during a real ascent - proves the new property reads the sim's live per-tick state, not a stale/default
        // zero, and that the dungeon's stamped stair statics (0.333 m risers) are detected as a continuous run.
        [Fact]
        public void Climb_ExportsNonZeroClimbRate_DuringRealAscent()
        {
            var (climbRates, _, _, finalPhysY, upperFloorY) = DriveClimb();

            Assert.True(finalPhysY > upperFloorY + CapsuleHalfHeight - 0.25f,
                $"climb did not reach the top: final Y {finalPhysY:F3}, expected ~{upperFloorY + CapsuleHalfHeight:F3}");

            int climbingTicks = climbRates.Count(r => r != 0f);
            Assert.True(climbingTicks > 20,
                $"CharacterController3D.ClimbRate read non-zero on only {climbingTicks} of {climbRates.Count} ticks - " +
                "the seam is not observing the sim's live climb signal.");
            Assert.True(climbRates.Any(r => r > 0f), "no tick reported a positive (ascending) ClimbRate.");
            Assert.All(climbRates, r => Assert.True(r <= CharacterController3D_MaxStepClimbSpeedDefault + 1e-3f,
                $"ClimbRate {r:F3} exceeds MaxStepClimbSpeed - the paced ceiling was not honoured."));
        }

        const float CharacterController3D_MaxStepClimbSpeedDefault = 3.5f;

        // (2) The bridge: fed that exact signal, RenderPosition.Y (what the drawn model and a follow camera target)
        // reaches the top, staying rate-bounded (never a jump beyond the paced climb speed) through the ascent -
        // mirrors StairAscentFeelTests.Avatar_RenderY_IsMonotoneAndRateBounded_OnRealStairs, but for the bridge.
        [Fact]
        public void Climb_BridgeRenderY_ReachesTop_RateBounded()
        {
            var (_, renderY, grounded, finalPhysY, upperFloorY) = DriveClimb();

            Assert.True(finalPhysY > upperFloorY + CapsuleHalfHeight - 0.25f,
                $"climb did not reach the top: final Y {finalPhysY:F3}");

            // Feed-forward is exactly bounded by MaxStepClimbSpeed*dt (SmoothedY += ClimbRate*dt). The critical-damp
            // term adds a small correction on top to absorb quantization drift. A generous fixed headroom (well under
            // a raw single-riser pop of ~0.333 m, but comfortably above the feed-forward bound alone) catches a real
            // regression - a raw per-riser pop slipping through the glide - without being brittle to the damp term's
            // exact magnitude.
            float upBound = CharacterController3D_MaxStepClimbSpeedDefault * Dt + 0.02f;
            float worstRise = 0f, worstDrop = 0f;
            for (int i = 1; i < renderY.Count; i++)
            {
                if (!grounded[i]) continue;   // airborne ticks (settle/landing) are excluded, matching the avatar pin
                float d = renderY[i] - renderY[i - 1];
                worstRise = MathF.Max(worstRise, d);
                worstDrop = MathF.Min(worstDrop, d);
            }

            // RenderPosition.Y is the bridge's FEET height (the sample was fed the feet position, not the capsule
            // centre - see CharacterPose.World's translation), so it should approach upperFloorY, not the capsule-
            // centre height finalPhysY converges to above.
            Assert.True(renderY[^1] > upperFloorY - 0.25f,
                $"bridge RenderPosition.Y did not reach the top: final {renderY[^1]:F3}, expected ~{upperFloorY:F3}");
            Assert.True(worstRise <= upBound,
                $"RenderPosition.Y rose {worstRise:F5} m in a single grounded tick, over the {upBound:F5} m/tick bound - " +
                "a raw per-riser pop slipped through the glide.");
            Assert.True(worstDrop >= -0.05f,
                $"RenderPosition.Y dropped {worstDrop:F5} m in a single grounded tick during the ascent - not a smooth glide.");
        }
    }
}
