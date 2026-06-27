using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
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
            // Normal nearly horizontal => slope ~90 deg, exceeds MaxSlope => horizontal move rejected.
            Func<float, float, Vector3> steep = (x, z) => Vector3.Normalize(new Vector3(1f, 0.05f, 0f));
            var c = new CharacterController3D { CapsuleHalfHeight = 0f };
            c.Update(Keys(Key.W), dt: 1f, cameraYaw: 0f, FlatGround, steep);
            Assert.True(MathF.Abs(c.Position.X) < 1e-6f && MathF.Abs(c.Position.Z) < 1e-6f, c.Position.ToString());
        }
    }
}
