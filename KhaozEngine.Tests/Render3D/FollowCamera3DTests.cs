using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class FollowCamera3DTests
    {
        [Fact]
        public void Eye_is_behind_target_along_yaw_pitch_distance()
        {
            // Yaw 0, Pitch 0, no height offset: eye sits +Z of the target by Distance, looking -Z.
            // MinPitch lowered so the pure pitch-0 geometry is exercised (the camera clamps pitch > 0 by default).
            var cam = new FollowCamera3D { Target = Vector3.Zero, Yaw = 0f, HeightOffset = 0f, MinPitch = 0f };
            cam.Pitch = 0f;
            cam.Distance = 10f;
            Assert.True(Vector3.Distance(cam.Eye, new Vector3(0, 0, 10)) < 1e-4f, cam.Eye.ToString());
            Assert.True(Vector3.Distance(cam.Forward, new Vector3(0, 0, -1)) < 1e-4f, cam.Forward.ToString());
        }

        [Fact]
        public void Height_offset_raises_the_eye()
        {
            var cam = new FollowCamera3D { Target = Vector3.Zero, Yaw = 0f, HeightOffset = 2f, MinPitch = 0f };
            cam.Pitch = 0f;
            cam.Distance = 10f;
            Assert.True(Vector3.Distance(cam.Eye, new Vector3(0, 2, 10)) < 1e-4f, cam.Eye.ToString());
        }

        [Fact]
        public void Camera_always_looks_at_the_target()
        {
            foreach (var (yaw, pitch, dist) in new[] { (0f, 0.3f, 6f), (1.2f, 0.8f, 12f), (-2f, 0.1f, 4f) })
            {
                var cam = new FollowCamera3D { Target = new Vector3(3, 1, -2), Yaw = yaw, HeightOffset = 1f };
                cam.Pitch = pitch; cam.Distance = dist;
                Vector3 inView = Vector3.Transform(cam.Target, cam.View);   // target in view space
                Assert.True(MathF.Abs(inView.X) < 1e-3f && MathF.Abs(inView.Y) < 1e-3f, inView.ToString());
                Assert.True(inView.Z < 0f, $"target should be in front (-Z): {inView.Z}");
            }
        }

        [Fact]
        public void Pitch_clamps_to_its_range()
        {
            var cam = new FollowCamera3D();
            cam.Pitch = 100f;                       // absurdly high
            Assert.Equal(cam.MaxPitch, cam.Pitch, 5);
            cam.Pitch = -100f;                      // absurdly low
            Assert.Equal(cam.MinPitch, cam.Pitch, 5);
        }

        [Fact]
        public void Distance_clamps_to_min_max()
        {
            var cam = new FollowCamera3D();
            cam.Distance = 1e6f;
            Assert.Equal(cam.MaxDistance, cam.Distance, 5);
            cam.Distance = -50f;
            Assert.Equal(cam.MinDistance, cam.Distance, 5);
        }

        [Fact]
        public void Target_projects_to_screen_center()
        {
            var cam = new FollowCamera3D { Target = new Vector3(2, 0.5f, 1), AspectRatio = 1.6f };
            cam.Pitch = 0.4f; cam.Distance = 8f;
            Vector4 clip = Vector4.Transform(new Vector4(cam.Target, 1f), cam.ViewProjection);
            Vector2 ndc = new(clip.X / clip.W, clip.Y / clip.W);
            Assert.True(MathF.Abs(ndc.X) < 1e-3f && MathF.Abs(ndc.Y) < 1e-3f, ndc.ToString());
        }

        [Fact]
        public void Eye_is_lifted_above_high_ground_at_its_xz()
        {
            // Ground higher than the geometric eye (a dip: terrain rises behind the character) lifts the eye
            // so it never sinks below the surface.
            var cam = new FollowCamera3D { Target = Vector3.Zero, GroundClearance = 0.5f };
            cam.Pitch = 0.3f; cam.Distance = 9f;
            float geomEyeY = cam.Eye.Y;       // before a ground delegate is attached
            cam.GroundHeight = (x, z) => 50f; // ground far above the geometric eye
            Assert.True(cam.Eye.Y >= 50f + 0.5f - 1e-4f, $"eye Y {cam.Eye.Y} not lifted above ground+clearance");
            Assert.True(cam.Eye.Y > geomEyeY, "eye should have been lifted");
        }

        [Fact]
        public void Eye_is_unchanged_when_ground_is_below()
        {
            var cam = new FollowCamera3D { Target = Vector3.Zero };
            cam.Pitch = 0.3f; cam.Distance = 9f;
            float geomEyeY = cam.Eye.Y;
            cam.GroundHeight = (x, z) => -1000f;   // ground far below the eye: no clamp
            Assert.Equal(geomEyeY, cam.Eye.Y, 4);
        }

        [Fact]
        public void Eye_is_geometric_when_no_ground_delegate()
        {
            var cam = new FollowCamera3D { Target = Vector3.Zero, HeightOffset = 0f, MinPitch = 0f };
            cam.Pitch = 0f; cam.Distance = 10f;
            Assert.Null(cam.GroundHeight);
            Assert.True(Vector3.Distance(cam.Eye, new Vector3(0, 0, 10)) < 1e-4f, cam.Eye.ToString());
        }
    }
}
