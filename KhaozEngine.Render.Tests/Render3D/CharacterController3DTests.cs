using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Locomotion;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class CharacterController3DTests
    {
        static InputState Keys(params Key[] down)
        {
            var d = new HashSet<Key>(down);
            return new InputState(
                d, new HashSet<Key>(), new HashSet<Key>(),
                new HashSet<MouseButton>(), new HashSet<MouseButton>(),
                Vector2.Zero, Vector2.Zero, 0f, 800, 600);
        }

        // Edge-triggered keys (KeysPressed populated) so WasPressed(...) sees them this frame.
        static InputState Pressed(params Key[] pressed)
        {
            var p = new HashSet<Key>(pressed);
            return new InputState(
                p, p, new HashSet<Key>(),
                new HashSet<MouseButton>(), new HashSet<MouseButton>(),
                Vector2.Zero, Vector2.Zero, 0f, 800, 600);
        }

        static readonly Func<float, float, float> FlatGround = (x, z) => 0f;

        [Fact]
        public void W_at_yaw_zero_moves_toward_negative_z()
        {
            var c = new CharacterController3D { CapsuleHalfHeight = 0f };
            c.Update(Keys(Key.W), dt: 1f, cameraYaw: 0f, FlatGround);
            Assert.True(c.Position.Z < 0f, c.Position.ToString());
            Assert.True(MathF.Abs(c.Position.X) < 1e-4f, c.Position.ToString());
            Assert.Equal(c.WalkSpeed, MathF.Abs(c.Position.Z), 4);   // 1 second at walk speed
        }

        [Fact]
        public void D_at_yaw_zero_moves_toward_positive_x()
        {
            var c = new CharacterController3D { CapsuleHalfHeight = 0f };
            c.Update(Keys(Key.D), dt: 1f, cameraYaw: 0f, FlatGround);
            Assert.True(c.Position.X > 0f, c.Position.ToString());
            Assert.True(MathF.Abs(c.Position.Z) < 1e-4f, c.Position.ToString());
        }

        [Fact]
        public void Diagonal_is_normalized()
        {
            var c = new CharacterController3D { CapsuleHalfHeight = 0f };
            c.Update(Keys(Key.W, Key.D), dt: 1f, cameraYaw: 0f, FlatGround);
            float horiz = new Vector2(c.Position.X, c.Position.Z).Length();
            Assert.Equal(c.WalkSpeed, horiz, 3);   // not WalkSpeed*sqrt(2)
        }

        [Fact]
        public void Idle_does_not_move()
        {
            var c = new CharacterController3D { CapsuleHalfHeight = 0f };
            c.Update(Keys(), dt: 1f, cameraYaw: 0f, FlatGround);
            Assert.True(MathF.Abs(c.Position.X) < 1e-6f && MathF.Abs(c.Position.Z) < 1e-6f, c.Position.ToString());
        }

        [Fact]
        public void Displacement_scales_with_dt()
        {
            var a = new CharacterController3D { CapsuleHalfHeight = 0f };
            a.Update(Keys(Key.W), dt: 0.1f, cameraYaw: 0f, FlatGround);
            var b = new CharacterController3D { CapsuleHalfHeight = 0f };
            b.Update(Keys(Key.W), dt: 0.2f, cameraYaw: 0f, FlatGround);
            Assert.Equal(2f * MathF.Abs(a.Position.Z), MathF.Abs(b.Position.Z), 4);
        }

        [Fact]
        public void Run_is_faster_than_walk()
        {
            var walk = new CharacterController3D { CapsuleHalfHeight = 0f };
            walk.Update(Keys(Key.W), dt: 1f, cameraYaw: 0f, FlatGround);
            var run = new CharacterController3D { CapsuleHalfHeight = 0f };
            run.Update(Keys(Key.W, Key.LeftShift), dt: 1f, cameraYaw: 0f, FlatGround);
            Assert.True(MathF.Abs(run.Position.Z) > MathF.Abs(walk.Position.Z), $"run {run.Position.Z} walk {walk.Position.Z}");
            Assert.Equal(run.RunSpeed, MathF.Abs(run.Position.Z), 3);
        }

        [Fact]
        public void Y_clamps_to_ground_plus_half_height_each_frame()
        {
            Func<float, float, float> bumpy = (x, z) => 5f;
            var c = new CharacterController3D { CapsuleHalfHeight = 0.9f };
            c.Update(Keys(Key.W), dt: 0.5f, cameraYaw: 0f, bumpy);
            Assert.Equal(5f + 0.9f, c.Position.Y, 4);
        }

        [Fact]
        public void Camera_relative_yaw_rotates_movement()
        {
            // Yaw = +90 deg: forward (W) should now head toward -X (camera turned a quarter turn).
            var c = new CharacterController3D { CapsuleHalfHeight = 0f };
            c.Update(Keys(Key.W), dt: 1f, cameraYaw: MathF.PI / 2f, FlatGround);
            Assert.True(c.Position.X < 0f, c.Position.ToString());
            Assert.True(MathF.Abs(c.Position.Z) < 1e-3f, c.Position.ToString());
        }

        [Fact]
        public void Step_onto_too_steep_ground_is_rejected()
        {
            // Normal nearly horizontal => slope ~90 deg, exceeds MaxSlope => horizontal move rejected. The face rises
            // toward -Z, which is where W at yaw 0 travels, and the normal and the height describe ONE surface on
            // purpose: the gate is direction-aware, so a steep normal over ground that does not stand above the feet
            // is a DESCENT and would (correctly) not be refused.
            Func<float, float, Vector3> steep = (x, z) => Vector3.Normalize(new Vector3(0f, 0.05f, 1f));
            Func<float, float, float> wall = (x, z) => MathF.Max(0f, -z) * 20f;
            var c = new CharacterController3D { CapsuleHalfHeight = 0f };
            c.Update(Keys(Key.W), dt: 1f, cameraYaw: 0f, wall, steep);
            Assert.True(MathF.Abs(c.Position.X) < 1e-6f && MathF.Abs(c.Position.Z) < 1e-6f, c.Position.ToString());
        }

        [Fact]
        public void Space_launches_a_jump_when_grounded()
        {
            var c = new CharacterController3D { CapsuleHalfHeight = 0f };
            c.Update(Keys(), dt: 1f / 60f, cameraYaw: 0f, FlatGround);   // settle grounded on flat ground
            Assert.True(c.Grounded);

            c.Update(Pressed(Key.Space), dt: 1f / 60f, cameraYaw: 0f, FlatGround);
            Assert.True(c.VerticalVelocity > 0f, $"jump should launch, got {c.VerticalVelocity}");
            Assert.False(c.Grounded);
        }

        [Fact]
        public void Stays_grounded_with_no_jump_on_flat_ground()
        {
            var c = new CharacterController3D { CapsuleHalfHeight = 0.9f };
            for (int i = 0; i < 30; i++) c.Update(Keys(Key.W), dt: 1f / 60f, cameraYaw: 0f, FlatGround);
            Assert.True(c.Grounded);
            Assert.Equal(0.9f, c.Position.Y, 4);
            Assert.Equal(0f, c.VerticalVelocity, 4);
        }

        [Fact]
        public void Facing_turn_speed_mirrors_the_tuning_and_steers_no_position()
        {
            // The controller mirrors MoveTuning's knobs literal for literal (AIRBORNE-MOMENTUM-DESIGN-2026-07-26), so
            // the facing rate's default is the tuning's own rather than some plausible finite value picked here.
            Assert.Equal(MoveTuning.Default.FacingTurnSpeed, new CharacterController3D().FacingTurnSpeed);

            // And setting a finite rate steers nothing: facing is an OUTPUT, no position is derived from it, so the
            // two runs stay bit-identical rather than merely close. The heading itself is not asserted here because
            // the controller exposes no FacingYaw to read it from. That gap is #433.
            var snap = new CharacterController3D { CapsuleHalfHeight = 0f };
            var lean = new CharacterController3D { CapsuleHalfHeight = 0f, FacingTurnSpeed = 3f };
            for (int i = 0; i < 30; i++)
            {
                snap.Update(Keys(Key.W, Key.D), dt: 1f / 60f, cameraYaw: 0f, FlatGround);
                lean.Update(Keys(Key.W, Key.D), dt: 1f / 60f, cameraYaw: 0f, FlatGround);
            }
            Assert.Equal(snap.Position, lean.Position);
        }
    }
}
