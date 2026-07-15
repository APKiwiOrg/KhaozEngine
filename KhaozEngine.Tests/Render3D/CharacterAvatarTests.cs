using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    // The turnkey CharacterAvatar wires movement + facing + animation with no per-game glue. These headless tests
    // pin the two behaviours that wiring is responsible for: (1) the animation state it feeds tracks the controller's
    // grounded / vertical-velocity / real horizontal speed (idle -> walk -> run -> jump -> fall), and (2) facing
    // tracks the player's INTENDED direction, staying put under a lateral collision velocity (the capsule slides
    // along a wall while facing holds on the intent - the "no wall-spin" property). Drawing needs a GPU and is not
    // exercised here.
    // CharacterAvatar is Obsolete (superseded by ReplicatedCharacterAnimators - see the type doc), but these pins
    // stay: no consumer left, but it is still public API and its documented behaviour (the RenderHeightSmoothRate
    // ease, the facing/animation wiring) must not silently regress for whatever still references it. Exercising it
    // on purpose, so CS0618 is disabled for the whole file (mirrors PopupPanelLocalizedTests' obsolete-shim pin).
#pragma warning disable CS0618
    public class CharacterAvatarTests
    {
        const float Dt = 1f / 60f;

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

        static readonly Func<float, float, float> FlatGround = (x, z) => 0f;

        // A one-bone rig with a distinct clip per locomotion state - enough for the state machine to select against.
        static AnimatedCharacter OneBoneAnim()
        {
            var skeleton = new Skeleton(new[] { -1 }, new[] { JointPose.Identity }, new[] { 0 }, new[] { 0 });
            AnimationClip Park(string name)
            {
                var jt = new JointTrack(0)
                {
                    Translation = new Vector3Track(new[] { 0f, 1f },
                        new[] { Vector3.Zero, Vector3.Zero }, InterpolationMode.Linear),
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

        static CharacterAvatar FlatAvatar(out CharacterController3D controller)
        {
            controller = new CharacterController3D { CapsuleHalfHeight = 0.9f };
            controller.SetXZ(0f, 0f);
            return new CharacterAvatar(controller, OneBoneAnim(), mesh: default);
        }

        [Fact]
        public void AnimationState_TracksSpeed_IdleWalkRun()
        {
            CharacterAvatar avatar = FlatAvatar(out _);

            for (int i = 0; i < 30; i++) avatar.Update(Keys(), Dt, 0f, FlatGround);
            Assert.Equal(LocomotionState.Idle, avatar.Animation.State);

            for (int i = 0; i < 30; i++) avatar.Update(Keys(Key.W), Dt, 0f, FlatGround);
            Assert.Equal(LocomotionState.Walk, avatar.Animation.State);

            for (int i = 0; i < 30; i++) avatar.Update(Keys(Key.W, Key.LeftShift), Dt, 0f, FlatGround);
            Assert.Equal(LocomotionState.Run, avatar.Animation.State);
        }

        [Fact]
        public void AnimationState_JumpThenFall_ThenLandsIdle()
        {
            CharacterAvatar avatar = FlatAvatar(out _);
            for (int i = 0; i < 15; i++) avatar.Update(Keys(), Dt, 0f, FlatGround);   // settle grounded
            Assert.Equal(LocomotionState.Idle, avatar.Animation.State);

            // A jump reads immediately (air states are not debounced): rising -> Jump.
            avatar.Update(Pressed(Key.Space), Dt, 0f, FlatGround);
            Assert.True(avatar.VerticalVelocity > 0f, "jump should impart upward velocity");
            Assert.Equal(LocomotionState.Jump, avatar.Animation.State);

            // Ride the arc: while airborne and descending it must read Fall; then it lands.
            bool sawFall = false;
            for (int i = 0; i < 180 && !avatar.Grounded; i++)
            {
                avatar.Update(Keys(), Dt, 0f, FlatGround);
                if (!avatar.Grounded && avatar.VerticalVelocity < 0f && avatar.Animation.State == LocomotionState.Fall)
                    sawFall = true;
            }
            Assert.True(sawFall, "descending airborne should read Fall");
            Assert.True(avatar.Grounded, "should have landed");

            for (int i = 0; i < 30; i++) avatar.Update(Keys(), Dt, 0f, FlatGround);
            Assert.Equal(LocomotionState.Idle, avatar.Animation.State);
        }

        [Fact]
        public void Facing_TracksIntendedDirection_NotSlidVelocity()
        {
            // A wall in front (forward = -Z at yaw 0). Walk W+D (forward-right diagonal) into it: the capsule cannot go
            // -Z but slides +X along the wall, so its VELOCITY becomes ~+X. Facing must hold on the diagonal INTENT,
            // not swing to the slid velocity - the collision-robust "no wall-spin" property.
            using IPhysicsWorld world = new BepuPhysicsWorld();
            world.AddStatic(new BoxShape(new Vector3(10f, 3f, 0.25f)), Pose.At(new Vector3(0f, 1.5f, -2f)));
            world.Step(Dt);

            var controller = new CharacterController3D { CapsuleHalfHeight = 0.9f, CapsuleRadius = 0.4f };
            controller.SetXZ(0f, 0f);
            var avatar = new CharacterAvatar(controller, OneBoneAnim(), mesh: default);

            float intendedYaw = CharacterFacing.YawOf(CharacterFacing.IntendedMoveDirection(Keys(Key.W, Key.D), 0f));

            for (int i = 0; i < 150; i++) avatar.Update(Keys(Key.W, Key.D), Dt, 0f, FlatGround, physics: world);

            // Facing settled on the intended diagonal.
            Assert.Equal(intendedYaw, avatar.FacingYaw, 2);

            // And the wall genuinely deflected the motion: the actual displacement heads a different way than the
            // intent (so a velocity-steered facing would have swung away). Guards against a vacuous test where the
            // capsule never reached the wall.
            Vector3 before = avatar.Position;
            for (int i = 0; i < 10; i++) avatar.Update(Keys(Key.W, Key.D), Dt, 0f, FlatGround, physics: world);
            Vector3 disp = avatar.Position - before; disp.Y = 0f;
            float velYaw = CharacterFacing.YawOf(disp);
            Assert.True(MathF.Abs(CharacterFacing.WrapAngle(velYaw - intendedYaw)) > 0.3f,
                $"velocity yaw {velYaw:F3} too close to intent {intendedYaw:F3}: the wall did not deflect the capsule, test is vacuous");
        }

        [Fact]
        public void RenderHeight_EasesAGroundedStepSnap_ThenConverges()
        {
            // A terrain step that jumps 1 m up at x = 3 (no physics, so the controller clamps straight onto it): the
            // physics height snaps a full metre in one tick when crossed, but the DRAW height (RenderPosition.Y) must
            // ease at the capped rate - this is the stair-bump smoothing, exercised without stair geometry.
            var controller = new CharacterController3D { CapsuleHalfHeight = 0.9f };
            controller.SetXZ(0f, 0f);
            var avatar = new CharacterAvatar(controller, OneBoneAnim(), mesh: default) { RenderHeightSmoothRate = 6f };
            Func<float, float, float> steppedGround = (x, z) => x > 3f ? 1f : 0f;

            for (int i = 0; i < 10; i++) avatar.Update(Keys(), Dt, 0f, steppedGround);   // settle on the low floor
            Assert.Equal(avatar.Position.Y, avatar.RenderPosition.Y, 3);                  // drawn exactly at physics when flat

            float maxRenderStep = 0f, maxRenderVsPhysicsGap = 0f;
            bool physicsJumped = false;
            for (int i = 0; i < 120; i++)   // walk +X (D at yaw 0) across the step
            {
                float prevRenderY = avatar.RenderPosition.Y;
                avatar.Update(Keys(Key.D), Dt, 0f, steppedGround);
                maxRenderStep = MathF.Max(maxRenderStep, MathF.Abs(avatar.RenderPosition.Y - prevRenderY));
                maxRenderVsPhysicsGap = MathF.Max(maxRenderVsPhysicsGap, MathF.Abs(avatar.Position.Y - avatar.RenderPosition.Y));
                if (avatar.Position.Y > 1.5f) physicsJumped = true;                       // physics snapped up onto the 1.9 m step
            }

            Assert.True(physicsJumped, "the character should have crossed the 1 m terrain step (physics height snapped up)");
            Assert.True(maxRenderVsPhysicsGap > 0.2f, "the draw height should have visibly lagged the physics snap (proof it eased)");
            Assert.True(maxRenderStep <= 6f * Dt + 1e-3f,
                $"draw height jumped {maxRenderStep:F4} m in one frame, over the {6f * Dt:F4} m/frame cap: not smoothed");
            Assert.Equal(avatar.Position.Y, avatar.RenderPosition.Y, 2);                  // and it converges back onto physics
        }

        [Fact]
        public void RenderHeight_SnapsWhileAirborne_SoJumpsStayCrisp()
        {
            var avatar = FlatAvatar(out _);
            for (int i = 0; i < 10; i++) avatar.Update(Keys(), Dt, 0f, FlatGround);
            avatar.Update(Pressed(Key.Space), Dt, 0f, FlatGround);   // jump
            for (int i = 0; i < 40 && !avatar.Grounded; i++)
            {
                avatar.Update(Keys(), Dt, 0f, FlatGround);
                if (!avatar.Grounded)
                    Assert.Equal(avatar.Position.Y, avatar.RenderPosition.Y, 4);   // airborne: draw height tracks physics exactly
            }
        }

        [Fact]
        public void NullPiece_Throws()
        {
            var controller = new CharacterController3D();
            Assert.Throws<ArgumentNullException>(() => new CharacterAvatar(null!, OneBoneAnim(), default));
            Assert.Throws<ArgumentNullException>(() => new CharacterAvatar(controller, null!, default));
        }
    }
#pragma warning restore CS0618
}
