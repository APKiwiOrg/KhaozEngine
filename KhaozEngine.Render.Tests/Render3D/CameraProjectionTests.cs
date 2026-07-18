using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class CameraProjectionTests
    {
        private const int W = 1920, H = 1080;

        // Distance from a point to the (infinite) line a WorldToScreen pixel unprojects to.
        private static float DistanceToRay(Ray ray, Vector3 p)
        {
            Vector3 dir = Vector3.Normalize(ray.Direction);
            Vector3 toP = p - ray.Origin;
            Vector3 closest = ray.Origin + dir * Vector3.Dot(toP, dir);
            return Vector3.Distance(p, closest);
        }

        [Fact]
        public void FollowCamera_WorldToScreen_round_trips_with_ScreenToRay()
        {
            var cam = new FollowCamera3D { Target = Vector3.Zero, AspectRatio = (float)W / H };
            cam.Pitch = 0.4f; cam.Distance = 8f;
            var world = new Vector3(1.5f, 0.6f, -0.4f);

            Assert.True(cam.WorldToScreen(world, W, H, out Vector2 pixel));
            Assert.InRange(pixel.X, 0f, W);
            Assert.InRange(pixel.Y, 0f, H);

            Ray ray = cam.ScreenToRay(pixel, W, H);
            Assert.True(DistanceToRay(ray, world) < 1e-2f, $"point {world} not on the unprojected ray (pixel {pixel})");
        }

        [Fact]
        public void IsoCamera_WorldToScreen_round_trips_with_ScreenToRay()
        {
            var cam = new IsoCamera3D { Target = Vector3.Zero, AspectRatio = (float)W / H, OrthoSize = 12f };
            var world = new Vector3(2f, 1f, -1.5f);

            Assert.True(cam.WorldToScreen(world, W, H, out Vector2 pixel));
            Ray ray = cam.ScreenToRay(pixel, W, H);
            Assert.True(DistanceToRay(ray, world) < 1e-2f, $"point {world} not on the unprojected ray (pixel {pixel})");
        }

        [Fact]
        public void Target_projects_to_screen_centre()
        {
            var cam = new FollowCamera3D { Target = new Vector3(3, 1, -2), AspectRatio = (float)W / H };
            cam.Pitch = 0.5f; cam.Distance = 7f;
            Assert.True(cam.WorldToScreen(cam.Target, W, H, out Vector2 pixel));
            Assert.True(System.MathF.Abs(pixel.X - W / 2f) < 1f, $"x {pixel.X}");
            Assert.True(System.MathF.Abs(pixel.Y - H / 2f) < 1f, $"y {pixel.Y}");
        }

        [Fact]
        public void FollowCamera_point_behind_camera_returns_false()
        {
            var cam = new FollowCamera3D { Target = Vector3.Zero, AspectRatio = (float)W / H };
            cam.Pitch = 0.4f; cam.Distance = 8f;
            Vector3 behind = cam.Eye - cam.Forward * 50f;   // 50 units behind the eye, opposite the view direction
            Assert.False(cam.WorldToScreen(behind, W, H, out Vector2 pixel));
            Assert.Equal(default, pixel);
        }

        [Fact]
        public void IsoCamera_point_behind_camera_returns_false()
        {
            var cam = new IsoCamera3D { Target = Vector3.Zero, AspectRatio = (float)W / H, OrthoSize = 12f };
            Vector3 behind = cam.Eye - cam.Forward * 50f;   // behind the near plane for the ortho camera
            Assert.False(cam.WorldToScreen(behind, W, H, out _));
        }
    }
}
