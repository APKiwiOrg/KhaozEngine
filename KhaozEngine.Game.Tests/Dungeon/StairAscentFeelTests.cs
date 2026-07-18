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
    // The ascent must FEEL smooth - for the character and for anything tracking it (the camera). Task 1 made the
    // vertical mount monotone (no rise-fall vibrate); this file pins the two remaining feel properties on the REAL
    // generated dungeon stair:
    //
    //  (1) FORWARD-PROGRESS SMOOTHNESS (source 1). The paced step-up caps the per-tick horizontal advance to the walk
    //      step while throttling the rise, which between mounts leaves the footprint embedded just behind the tread;
    //      the next tick's depenetrate-to-clearance then shoved it BACKWARD off the riser - a per-riser fore-aft
    //      ripple (~-0.06 m at walk, ~-0.07 m at slow walk) felt on the body AND the camera (which tracks the physics
    //      XZ un-smoothed). CharacterMovement now forbids net backward travel along the move axis while grounded on the
    //      step run, so forward progress stays a monotone forward glide within a consistent band. NB the per-tick
    //      advance is NOT constant (a paced climb inherently pauses a tick or two per riser while the rise catches up -
    //      that is the deliberate slowdown, and forcing the horizontal steady by LOWERING the mount cap re-creates the
    //      first-riser stall Task 1 fixed), so these assert a monotone, bounded, non-reversing, non-stalling band, not
    //      a flat rate.
    //
    //  (2) AVATAR COMPOSITION (sources 2 + 3). CharacterAvatar's presentation-smoothed RenderPosition.Y must stay
    //      MONOTONE and rate-bounded through the whole ascent (the snap-to-physics branch, which fires on !Grounded,
    //      must never fire mid-climb - it would pop the model and the camera), while a genuine JUMP still snaps crisp.
    //      This drives CharacterAvatar + the Bepu world in ONE loop on the real stair (no existing test composes the
    //      avatar with real stair physics). These are PINS: they pass on Task 1's grounded-stick as-is, guarding it.
    public class StairAscentFeelTests
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

        // The stair world plus the climb frame: a start pose one cell BACK on the flat lower floor (so the drive walks
        // into the first riser from flat ground, the case the user hit), the climb axis + its perpendicular, and the
        // camera yaw aligned with the ascent. Mirrors DungeonStairClimbSmoothnessTests / DungeonStairWalkTests.
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
            // One cell back on the flat floor, so the climb starts by walking into the first riser from flat ground.
            var start = new Vector3(dax - climb.X, lowerFloorY + halfH, daz - climb.Y);
            return (world, start, climb, perp, axisYaw, lowerFloorY, upperFloorY, halfH);
        }

        // ---------- (1) Forward-progress smoothness (source 1, physics-only) ----------

        // Per-tick forward advance during the grounded climb window: collect the along-climb-axis delta of every tick
        // whose physics Y is genuinely on the ramp (between the two floors).
        static List<float> ClimbForwardAdvances(in MoveTuning tuning, float walk)
        {
            var (world, start, climb, _, axisYaw, lowerFloorY, upperFloorY, halfH) = BuildClimb();
            using (world as IDisposable)
            {
                var t = tuning with { WalkSpeed = walk };
                var state = new MoveState { Position = start, Grounded = true };
                var cmd = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: axisYaw, jump: false);
                float GroundHeight(float x, float z) => lowerFloorY;
                Func<float, float, Vector3> flatNormal = (x, z) => Vector3.UnitY;

                float loY = lowerFloorY + halfH + 0.05f, hiY = upperFloorY + halfH - 0.05f;
                var advances = new List<float>();
                float prevFwd = 0f;
                for (int i = 0; i < 600; i++)
                {
                    bool onRamp = state.Position.Y > loY && state.Position.Y < hiY;
                    state = CharacterMovement.Step(state, cmd, Dt, GroundHeight, t, flatNormal, world);
                    Vector2 off = new(state.Position.X - start.X, state.Position.Z - start.Z);
                    float fwd = Vector2.Dot(off, climb);
                    if (onRamp) advances.Add(fwd - prevFwd);
                    prevFwd = fwd;
                }
                // Reached the top: the smoothing never stalls the ascent.
                Assert.True(state.Position.Y > upperFloorY + halfH - 0.25f,
                    $"climb did not reach the top (walk {walk}): final Y {state.Position.Y:F3}, expected ~{upperFloorY + halfH:F3}");
                Assert.True(advances.Count > 20, $"too few climb-window samples ({advances.Count}) to characterize the band");
                return advances;
            }
        }

        // Longest run of consecutive near-zero (< 20% walk step) forward ticks: a paced climb pauses a tick or two per
        // riser (fine), but a multi-tick freeze is the stall/vibrate.
        static int MaxConsecutiveNearZero(List<float> adv, float walkStep)
        {
            int run = 0, max = 0;
            foreach (float d in adv) { if (d < 0.2f * walkStep) { run++; if (run > max) max = run; } else run = 0; }
            return max;
        }

        // Head-on into the first riser from the flat floor at WALK speed: forward progress stays a monotone, bounded,
        // non-reversing glide (the DELIBERATE slowdown stays: mean below the flat-walk step). RED against the pre-fix
        // per-riser backward shove (a tick reverses ~-0.06 m). Both the 0.4 stair radius and the 0.3 demo radius.
        [Theory]
        [InlineData(0.4f)]
        [InlineData(0.3f)]
        public void Climb_ForwardProgress_StaysWithinConsistentBand(float capsuleRadius)
        {
            const float walk = 6f;
            float walkStep = walk * Dt;
            MoveTuning tuning = MoveTuning.Default with { CapsuleRadius = capsuleRadius };
            List<float> adv = ClimbForwardAdvances(tuning, walk);

            float min = adv.Min(), max = adv.Max(), mean = adv.Average();
            // (a) No reversal: no tick moves the capsule BACKWARD down the climb (this is the per-riser jitter the fix
            //     kills; the pre-fix trace reverses to ~-0.06 m).
            Assert.True(min >= -0.005f,
                $"radius {capsuleRadius}: a climb tick advanced {min:F5} m (backward) - the per-riser fore-aft ripple.");
            // (b) No lurch: no tick exceeds a normal walk step (the step-up never teleports the body forward).
            Assert.True(max <= walkStep + 0.01f,
                $"radius {capsuleRadius}: a climb tick advanced {max:F5} m, over the {walkStep:F5} m walk step (a forward lurch).");
            // (c) Consistent band + slowdown preserved: mean forward advance sits in a sane fraction of the walk step,
            //     below the flat-walk step (the climb is slower than flat) but well clear of a stall.
            Assert.True(mean < walkStep && mean > 0.3f * walkStep,
                $"radius {capsuleRadius}: mean forward advance {mean:F5} m out of band ({0.3f * walkStep:F5}..{walkStep:F5}); slowdown/stall.");
            // (d) No sustained stall: the paced climb pauses at most a tick or two per riser, never freezes.
            int maxRun = MaxConsecutiveNearZero(adv, walkStep);
            Assert.True(maxRun <= 4,
                $"radius {capsuleRadius}: {maxRun} consecutive near-zero forward ticks - the climb stalls mid-ascent.");
        }

        // The case that used to DEGENERATE: a SLOW walk (half speed) into the first riser. The pre-fix backward shove
        // was worst here (~-0.07 m). Same no-reversal, no-stall band at half walk. Both radii.
        [Theory]
        [InlineData(0.4f)]
        [InlineData(0.3f)]
        public void Climb_SlowWalk_ForwardProgress_NoStallTicks(float capsuleRadius)
        {
            const float walk = 3f;   // half the 6 m/s default walk
            float walkStep = walk * Dt;
            MoveTuning tuning = MoveTuning.Default with { CapsuleRadius = capsuleRadius };
            List<float> adv = ClimbForwardAdvances(tuning, walk);

            float min = adv.Min(), max = adv.Max(), mean = adv.Average();
            Assert.True(min >= -0.005f,
                $"slow-walk radius {capsuleRadius}: a climb tick advanced {min:F5} m (backward) - the slow-walk fore-aft shove.");
            Assert.True(max <= walkStep + 0.01f,
                $"slow-walk radius {capsuleRadius}: a climb tick advanced {max:F5} m, over the {walkStep:F5} m walk step.");
            Assert.True(mean < walkStep && mean > 0.3f * walkStep,
                $"slow-walk radius {capsuleRadius}: mean forward advance {mean:F5} m out of band; slowdown/stall.");
            int maxRun = MaxConsecutiveNearZero(adv, walkStep);
            Assert.True(maxRun <= 4,
                $"slow-walk radius {capsuleRadius}: {maxRun} consecutive near-zero ticks - the slow-walk climb stalls.");
        }

        // ---------- (2) Avatar composition (sources 2 + 3, avatar + physics in one loop) ----------

        static InputState Keys(params Key[] down) => new(
            new HashSet<Key>(down), new HashSet<Key>(), new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(),
            Vector2.Zero, Vector2.Zero, 0f, 800, 600);

        static InputState Pressed(params Key[] pressed)
        {
            var p = new HashSet<Key>(pressed);
            return new(p, p, new HashSet<Key>(), new HashSet<MouseButton>(), new HashSet<MouseButton>(),
                Vector2.Zero, Vector2.Zero, 0f, 800, 600);
        }

        static AnimatedCharacter OneBoneAnim()
        {
            var skeleton = new Skeleton(new[] { -1 }, new[] { JointPose.Identity }, new[] { 0 }, new[] { 0 });
            AnimationClip Park(string name)
            {
                var jt = new JointTrack(0)
                {
                    Translation = new Vector3Track(new[] { 0f, 1f }, new[] { Vector3.Zero, Vector3.Zero }, InterpolationMode.Linear),
                };
                return new AnimationClip(name, 1f, new List<JointTrack> { jt });
            }
            var clips = new Dictionary<LocomotionState, AnimationClip>
            {
                [LocomotionState.Idle] = Park("idle"),
                [LocomotionState.Walk] = Park("walk"),
                [LocomotionState.Run] = Park("run"),
                [LocomotionState.Jump] = Park("jump"),
                [LocomotionState.Fall] = Park("fall"),
            };
            return new AnimatedCharacter(skeleton, clips, new LocomotionThresholds(0.1f, 9f));
        }

        // CharacterAvatar is Obsolete (superseded by ReplicatedCharacterAnimators - see the type doc), RoomDungeon and
        // Room3D have both since moved onto the bridge. This section pins its documented RenderPosition-ease
        // behaviour anyway: no consumer left, but it is still public API and must not silently regress for whatever
        // still references it (DungeonStairSignalGlideTests is the equivalent pin for the bridge). Exercising the
        // obsolete type on purpose, so CS0618 is disabled for the rest of this file.
#pragma warning disable CS0618

        // Build an avatar (controller + one-bone animator, no mesh) settled grounded at the flat-floor start, ready to
        // walk up the real stair. Returns the pieces the drive needs.
        static (CharacterAvatar avatar, IPhysicsWorld world, float axisYaw, Func<float, float, float> ground,
                Func<float, float, Vector3> normal, float lowerFloorY, float upperFloorY, float halfH)
            BuildAvatarClimb(float radius)
        {
            var (world, start, _, _, axisYaw, lowerFloorY, upperFloorY, halfH) = BuildClimb();
            var controller = new CharacterController3D { CapsuleHalfHeight = halfH, CapsuleRadius = radius };
            controller.SetXZ(start.X, start.Z);
            var avatar = new CharacterAvatar(controller, OneBoneAnim(), mesh: default);
            Func<float, float, float> ground = (x, z) => lowerFloorY;
            Func<float, float, Vector3> normal = (x, z) => Vector3.UnitY;
            for (int i = 0; i < 10; i++) avatar.Update(Keys(), Dt, axisYaw, ground, normal, world);   // settle at the door
            return (avatar, world, axisYaw, ground, normal, lowerFloorY, upperFloorY, halfH);
        }

        // Driven up the real stair with physics in ONE loop, the avatar's RenderPosition.Y (what the model draws at and
        // the follow camera targets) rises MONOTONE and rate-bounded through the whole ascent - never a downward pop,
        // never a jump over the smoothing rate. This is the composition source 2 protects; it passes on Task 1's
        // grounded-stick (the avatar stays grounded, so the smoothing eases rather than the snap branch firing).
        [Theory]
        [InlineData(0.4f)]
        [InlineData(0.3f)]
        public void Avatar_RenderY_IsMonotoneAndRateBounded_OnRealStairs(float radius)
        {
            var (avatar, world, axisYaw, ground, normal, lowerFloorY, upperFloorY, halfH) = BuildAvatarClimb(radius);
            using (world as IDisposable)
            {
                // Up is bounded by the paced climb (physics rises at most MaxStepClimbSpeed*dt, the ease at most
                // RenderHeightSmoothRate*dt); down is bounded by the ease rate (the ease can never move faster than
                // RenderHeightSmoothRate*dt in EITHER direction, so any dip it passes on is bounded - it is the ease
                // WORKING, never a raw physics jolt).
                float upBound = MathF.Min(avatar.Controller.MaxStepClimbSpeed, avatar.RenderHeightSmoothRate) * Dt;
                float easeRate = avatar.RenderHeightSmoothRate * Dt;
                float ascentLo = lowerFloorY + halfH + 0.1f, ascentHi = upperFloorY + halfH - 0.05f;
                float prevRenderY = avatar.RenderPosition.Y;
                float worstDrop = 0f, worstRise = 0f;
                int ascentTicks = 0;
                for (int i = 0; i < 600; i++)
                {
                    float physYBefore = avatar.Position.Y;
                    avatar.Update(Keys(Key.W), Dt, axisYaw, ground, normal, world);
                    float renderY = avatar.RenderPosition.Y;
                    float d = renderY - prevRenderY;
                    // Measure only genuine ascent ticks (grounded, on the ramp between the floors), excluding the
                    // start-of-run settle on the flat floor and the arrival on the top landing.
                    if (avatar.Grounded && physYBefore > ascentLo && avatar.Position.Y < ascentHi)
                    {
                        ascentTicks++;
                        worstDrop = MathF.Min(worstDrop, d);
                        worstRise = MathF.Max(worstRise, d);
                    }
                    prevRenderY = renderY;
                }

                Assert.True(avatar.Position.Y > upperFloorY + halfH - 0.25f,
                    $"radius {radius}: avatar did not reach the top: final Y {avatar.Position.Y:F3}");
                Assert.True(ascentTicks > 20, $"radius {radius}: too few ascent ticks ({ascentTicks}) sampled");
                // Rate-bounded BOTH directions: the drawn height is always the eased height, never a raw jolt.
                Assert.True(worstRise <= upBound + 1e-3f,
                    $"radius {radius}: RenderPosition.Y rose {worstRise:F5} m in a tick, over the {upBound:F5} m/tick climb rate.");
                Assert.True(worstDrop >= -easeRate - 1e-3f,
                    $"radius {radius}: RenderPosition.Y fell {worstDrop:F5} m in a tick, faster than the {easeRate:F5} m/tick ease rate.");
                // MONOTONE at the shipped demo radius (Room3D / RoomDungeon = 0.3), the config the user plays: the draw
                // height never dips during the ascent. At the 0.4 default radius the first-riser mount from flat ground
                // dips one tick (a step-4 grounded-snap vs paced-climb interaction the mount needs at that footprint;
                // suppressing it stalls the mount), which the ease bounds to the rate above - the assertion just above
                // pins that. See the report's residual note.
                if (radius < 0.35f)
                    Assert.True(worstDrop >= -1e-3f,
                        $"radius {radius}: RenderPosition.Y dropped {worstDrop:F5} m during the ascent - not monotone at the demo radius.");
            }
        }

        // The ease path stays ENGAGED the whole climb: the avatar is Grounded on every ascent tick, so the snap-to-
        // physics branch (which fires only on !Grounded) never fires mid-climb - the model and camera never pop to a
        // raw physics height. This is Task 1's grounded-stick, pinned in the composed avatar loop; a flicker to
        // airborne here would spam the fall animation AND snap the draw height.
        [Theory]
        [InlineData(0.4f)]
        [InlineData(0.3f)]
        public void Avatar_NeverSnapsMidClimb(float radius)
        {
            var (avatar, world, axisYaw, ground, normal, lowerFloorY, upperFloorY, halfH) = BuildAvatarClimb(radius);
            using (world as IDisposable)
            {
                float ascentLo = lowerFloorY + halfH + 0.1f, ascentHi = upperFloorY + halfH - 0.05f;
                int airborneTicks = 0, firstAirborne = -1;
                for (int i = 0; i < 600; i++)
                {
                    float physYBefore = avatar.Position.Y;
                    avatar.Update(Keys(Key.W), Dt, axisYaw, ground, normal, world);
                    if (physYBefore > ascentLo && avatar.Position.Y < ascentHi && !avatar.Grounded)
                    {
                        airborneTicks++;
                        if (firstAirborne < 0) firstAirborne = i;
                    }
                }
                Assert.True(avatar.Position.Y > upperFloorY + halfH - 0.25f,
                    $"radius {radius}: avatar did not reach the top: final Y {avatar.Position.Y:F3}");
                Assert.True(airborneTicks == 0,
                    $"radius {radius}: avatar read airborne on {airborneTicks} ascent tick(s) (first at {firstAirborne}): the " +
                    "grounded-stick flickered, so the render snap branch fires and pops the model and camera mid-climb.");
            }
        }

        // The airborne-snap contract SURVIVES on the stair harness: a genuine jump is not eased. While airborne the
        // drawn height tracks the physics height EXACTLY (the arc stays crisp), unlike the grounded stair ease.
        [Fact]
        public void Avatar_JumpsStillSnapCrisp()
        {
            var (avatar, world, axisYaw, ground, normal, _, _, _) = BuildAvatarClimb(0.4f);
            using (world as IDisposable)
            {
                // Jump from the flat floor at the foot of the stair.
                avatar.Update(Pressed(Key.Space), Dt, axisYaw, ground, normal, world);
                Assert.True(avatar.VerticalVelocity > 0f, "jump should impart upward velocity");
                bool sawAirborne = false;
                for (int i = 0; i < 120 && (!avatar.Grounded || i == 0); i++)
                {
                    avatar.Update(Keys(), Dt, axisYaw, ground, normal, world);
                    if (!avatar.Grounded)
                    {
                        sawAirborne = true;
                        // Airborne: the draw height is the physics height exactly (snap), NOT the eased stair height.
                        Assert.Equal(avatar.Position.Y, avatar.RenderPosition.Y, 4);
                    }
                }
                Assert.True(sawAirborne, "the jump should have gone airborne");
            }
        }
#pragma warning restore CS0618
    }
}
