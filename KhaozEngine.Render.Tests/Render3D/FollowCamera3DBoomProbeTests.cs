using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The follow camera's boom probe seam (<see cref="FollowCamera3D.BoomProbe"/>): an
    /// <see cref="ICameraBoomProbe"/> reports how far the boom can extend from the pivot, and the eye pulls in along
    /// the boom to that reach less the skin. The physics <see cref="FollowCamera3D.Occlusion"/> sweep runs through
    /// the same path, so when both are set the shorter reach wins.
    /// </summary>
    public class FollowCamera3DBoomProbeTests
    {
        /// <summary>The framing the occlusion and eye-cache rows use: the boom runs straight down +Z from the origin
        /// for 10 m.</summary>
        static FollowCamera3D Camera(ICameraBoomProbe? probe)
        {
            var cam = new FollowCamera3D
            {
                Target = Vector3.Zero,
                Yaw = 0f,
                HeightOffset = 0f,
                MinPitch = 0f,
                BoomProbe = probe,
            };
            cam.Pitch = 0f;
            cam.Distance = 10f;
            return cam;
        }

        static void Near(Vector3 expected, Vector3 actual, float tolerance)
            => Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

        [Fact]
        public void A_probe_reach_short_of_the_boom_pulls_the_eye_in_by_the_skin()
        {
            FollowCamera3D cam = Camera(new FixedReachProbe { ReachAt = 6f });

            Near(new Vector3(0f, 0f, 6f - cam.OcclusionSkin), cam.Eye, 1e-4f);
        }

        [Fact]
        public void The_probe_is_asked_from_the_pivot_along_the_boom_with_the_occlusion_radius()
        {
            var probe = new FixedReachProbe();
            using var world = new CountingPhysicsWorld();
            FollowCamera3D cam = Camera(probe);
            cam.PivotHeight = 1.5f;
            cam.Occlusion = world;

            _ = cam.Eye;

            Assert.Equal(new Vector3(0f, 1.5f, 0f), probe.LastOrigin);
            Near(Vector3.UnitZ, probe.LastDirection, 1e-5f);
            Assert.Equal(10f, probe.LastLength, 4);
            Assert.Equal(cam.OcclusionRadius, probe.LastRadius);
            // The physics sweep shares the path, so it starts at the pivot too (the world's origin is zero).
            Assert.Equal(cam.Pivot, world.LastSweepStart);
        }

        [Fact]
        public void A_full_length_reach_leaves_the_eye_alone()
        {
            var probe = new FixedReachProbe { ReachAt = null };
            FollowCamera3D cam = Camera(probe);

            Assert.Equal(new Vector3(0f, 0f, 10f), cam.Eye);
            Assert.Equal(1, probe.Calls);   // consulted, and a clear reach changed nothing
        }

        [Fact]
        public void The_pull_in_is_floored_at_the_minimum_occlusion_distance()
        {
            FollowCamera3D cam = Camera(new FixedReachProbe { ReachAt = 0f });

            Assert.Equal(cam.MinOcclusionDistance, Vector3.Distance(cam.Eye, cam.Pivot), 4);
        }

        [Fact]
        public void A_blocked_boom_below_the_horizon_slides_in_and_keeps_looking_up()
        {
            // The eye under the pivot meets the ground. It shortens along its own line rather than lifting, so the
            // view keeps its upward tilt.
            var cam = new FollowCamera3D
            {
                Target = Vector3.Zero,
                Yaw = 0f,
                HeightOffset = 0f,
                MinPitch = -1.4f,
                PivotHeight = 1.5f,
                BoomProbe = new FixedReachProbe { ReachAt = 1.2f },
            };
            cam.Pitch = -1.0f;
            cam.Distance = 10f;
            Vector3 dir = Vector3.Normalize(new Vector3(0f, MathF.Sin(-1.0f), MathF.Cos(-1.0f)));

            Near(cam.Pivot + dir * (1.2f - cam.OcclusionSkin), cam.Eye, 1e-4f);
            Near(-dir, cam.Forward, 1e-4f);
            Assert.True(cam.Forward.Y > 0f, cam.Forward.ToString());
        }

        [Fact]
        public void Probe_and_physics_together_take_the_shorter_reach()
        {
            using var world = new CountingPhysicsWorld { WallDistance = 6.25f };
            var probe = new FixedReachProbe { ReachAt = 4f };
            FollowCamera3D cam = Camera(probe);
            cam.Occlusion = world;

            Near(new Vector3(0f, 0f, 4f - cam.OcclusionSkin), cam.Eye, 1e-4f);

            probe.ReachAt = 8f;   // the probe clears past the wall, which no camera field can see
            cam.BeginFrame();

            Near(new Vector3(0f, 0f, 6.25f - cam.OcclusionRadius - cam.OcclusionSkin), cam.Eye, 1e-4f);
        }
    }
}
