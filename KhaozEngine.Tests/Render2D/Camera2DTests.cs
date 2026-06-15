using System;
using System.Numerics;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Render2D
{
    public class Camera2DTests
    {
        const int W = 800, H = 600;

        [Fact]
        public void Default_camera_maps_world_origin_to_screen_center()
        {
            var cam = new Camera2D();
            var s = cam.WorldToScreen(Vector2.Zero, W, H);
            Assert.True(Vector2.Distance(s, new Vector2(W / 2f, H / 2f)) < 1e-3f, s.ToString());
        }

        [Fact]
        public void Camera_position_maps_to_screen_center()
        {
            var cam = new Camera2D { Position = new Vector2(123, -45), Zoom = 1.7f, Rotation = 0.6f };
            var s = cam.WorldToScreen(cam.Position, W, H);
            Assert.True(Vector2.Distance(s, new Vector2(W / 2f, H / 2f)) < 1e-3f, s.ToString());
        }

        [Fact]
        public void Zoom_scales_screen_offset()
        {
            var cam = new Camera2D { Zoom = 2f };
            var s = cam.WorldToScreen(new Vector2(10, 0), W, H);
            // offset = (world - pos) * zoom = (10,0)*2 = (20,0) from centre
            Assert.True(Vector2.Distance(s, new Vector2(W / 2f + 20, H / 2f)) < 1e-3f, s.ToString());
        }

        [Fact]
        public void ScreenToWorld_roundtrips()
        {
            var cam = new Camera2D { Position = new Vector2(50, 30), Zoom = 1.3f, Rotation = -0.4f };
            var world = new Vector2(212, 97);
            var back = cam.ScreenToWorld(cam.WorldToScreen(world, W, H), W, H);
            Assert.True(Vector2.Distance(world, back) < 1e-2f, back.ToString());
        }

        [Fact]
        public void ViewProjection_maps_position_to_clip_origin()
        {
            var cam = new Camera2D { Position = new Vector2(77, 11), Zoom = 0.9f };
            Vector4 clip = Vector4.Transform(new Vector4(cam.Position, 0, 1), cam.GetViewProjection(W, H));
            Assert.True(MathF.Abs(clip.X / clip.W) < 1e-4f && MathF.Abs(clip.Y / clip.W) < 1e-4f, clip.ToString());
        }
    }
}
