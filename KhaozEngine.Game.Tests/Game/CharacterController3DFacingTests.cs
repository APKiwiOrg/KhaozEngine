using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Game
{
    /// <summary>
    /// The local controller's half of the facing feature
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/436">#436</see>).
    /// <c>CharacterController3D.FacingTurnSpeed</c> shipped wired into the tuning it builds and with nothing a
    /// consumer could see: no accessor for the heading the step maintains, and no way to ask for the face-camera
    /// target the knob mostly exists to rate-limit. So setting it changed nothing observable on this path while
    /// the networked path had both. These drive the controller frame by frame and read the heading back.
    /// </summary>
    public sealed class CharacterController3DFacingTests
    {
        const float Dt = 1f / 30f;

        static readonly Func<float, float, float> Flat = (x, z) => 0f;

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

        /// <summary>A controller settled on flat ground, walking forward under camera yaw 0, so its heading is 0.</summary>
        static CharacterController3D Settled(float turnSpeed)
        {
            var controller = new CharacterController3D { FacingTurnSpeed = turnSpeed };
            for (int i = 0; i < 10; i++) controller.Update(Keys(Key.W), Dt, 0f, Flat);

            return controller;
        }

        /// <summary>
        /// THE KNOB IS OBSERVABLE. A finite rate turns the heading toward the new direction of travel over several
        /// frames, by no more than its own budget per tick, and lands exactly on the target.
        /// </summary>
        [Fact]
        public void A_finite_turn_speed_turns_the_heading_over_frames()
        {
            const float Rate = 2f;
            CharacterController3D controller = Settled(Rate);
            Assert.Equal(0f, controller.FacingYaw, 4);

            float target = MathF.PI / 2f;
            controller.Update(Keys(Key.W), Dt, target, Flat);
            float afterOneTick = controller.FacingYaw;

            Assert.True(afterOneTick > 0f, "the heading did not turn at all");
            Assert.True(afterOneTick <= (Rate * Dt) + 1e-5f,
                $"the heading turned {afterOneTick} rad in one tick, past the {Rate * Dt} rad the knob allows");
            Assert.True(afterOneTick < target - 0.1f, "the heading snapped instead of easing");

            for (int i = 0; i < 60; i++) controller.Update(Keys(Key.W), Dt, target, Flat);

            Assert.Equal(target, controller.FacingYaw, 4);
        }

        /// <summary>
        /// THE DEFAULT IS UNCHANGED. An infinite rate snaps in one tick, which is the feel every consumer had
        /// before the knob existed, so reading the heading cannot have altered anyone's presentation.
        /// </summary>
        [Fact]
        public void The_default_turn_speed_snaps_the_heading_in_one_tick()
        {
            var controller = new CharacterController3D();
            float target = MathF.PI / 2f;

            controller.Update(Keys(Key.W), Dt, target, Flat);

            Assert.Equal(target, controller.FacingYaw, 4);
        }

        /// <summary>
        /// FACE-CAMERA IS REACHABLE FROM THIS TYPE NOW. A stationary character turns to the camera while it is
        /// held, which is the target the rate limit mostly exists for, and holds its heading while it is not.
        /// </summary>
        [Fact]
        public void Face_camera_turns_a_stationary_character_toward_the_camera()
        {
            const float Yaw = 1f;
            var pinned = new CharacterController3D { FacingTurnSpeed = 2f, FaceCamera = true };
            var free = new CharacterController3D { FacingTurnSpeed = 2f };

            for (int i = 0; i < 60; i++)
            {
                pinned.Update(Keys(), Dt, Yaw, Flat);
                free.Update(Keys(), Dt, Yaw, Flat);
            }

            Assert.Equal(Yaw, pinned.FacingYaw, 4);
            Assert.Equal(0f, free.FacingYaw, 4);
        }

        /// <summary>
        /// THE LANDING LATCH IS READABLE TOO, which is what a single-player fall-damage curve needs and what the
        /// networked path already had. It is a one-tick event, so the tick after the landing reads zero again.
        /// </summary>
        [Fact]
        public void A_landing_reports_its_impact_speed_for_one_tick()
        {
            var controller = new CharacterController3D();
            for (int i = 0; i < 10; i++) controller.Update(Keys(), Dt, 0f, Flat);
            Assert.Equal(0f, controller.LandingImpactSpeed, 4);

            controller.Update(Pressed(Key.Space), Dt, 0f, Flat);
            Assert.False(controller.Grounded);

            float impact = 0f;
            for (int i = 0; i < 120 && impact == 0f; i++)
            {
                controller.Update(Keys(), Dt, 0f, Flat);
                impact = controller.LandingImpactSpeed;
            }

            Assert.True(impact > 5f, $"the landing reported an impact of {impact} m/s after a full jump arc");

            controller.Update(Keys(), Dt, 0f, Flat);
            Assert.Equal(0f, controller.LandingImpactSpeed, 4);
        }
    }
}
