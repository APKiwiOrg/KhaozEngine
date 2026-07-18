using System;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

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

        [Fact]
        public void CenterOn_sets_position()
        {
            var cam = new Camera2D { Position = new Vector2(5, 5) };
            cam.CenterOn(new Vector2(42, -7));
            Assert.Equal(new Vector2(42, -7), cam.Position);
        }

        [Fact]
        public void PanByScreenDelta_moves_world_opposite_by_delta_over_zoom()
        {
            var cam = new Camera2D { Position = new Vector2(100, 100), Zoom = 2f };
            cam.PanByScreenDelta(new Vector2(20, -10));
            // Position -= delta / zoom = (100,100) - (10,-5) = (90,105)
            Assert.Equal(new Vector2(90, 105), cam.Position);
        }

        [Fact]
        public void PanByScreenDelta_is_noop_for_zero_delta_or_zero_zoom()
        {
            var cam = new Camera2D { Position = new Vector2(1, 2), Zoom = 1f };
            cam.PanByScreenDelta(Vector2.Zero);
            Assert.Equal(new Vector2(1, 2), cam.Position);

            cam.Zoom = 0f;
            cam.PanByScreenDelta(new Vector2(5, 5));
            Assert.Equal(new Vector2(1, 2), cam.Position);
        }

        [Fact]
        public void Focus_fits_zoom_to_contain_and_centers_on_rect()
        {
            var cam = new Camera2D();
            // 400x600 rect into an 800x600 viewport: fit = min(800/400, 600/600) = min(2,1) = 1
            var rect = new Rect(100, 0, 400, 600);
            cam.Focus(rect, W, H);
            Assert.Equal(1f, cam.Zoom, 3);
            Assert.Equal(new Vector2(300, 300), cam.Position);
        }

        [Fact]
        public void Focus_padding_shrinks_the_fit_zoom()
        {
            var cam = new Camera2D();
            var rect = new Rect(0, 0, 400, 300); // fit without padding = min(2,2)=2
            cam.Focus(rect, W, H, paddingFraction: 0.25f);
            // scale = 1 + 2*0.25 = 1.5 -> w=600,h=450 -> fit = min(800/600,600/450)=1.333..
            Assert.Equal(800f / 600f, cam.Zoom, 3);
        }

        [Fact]
        public void Focus_clamps_to_min_and_max_zoom()
        {
            var cam = new Camera2D();
            cam.Focus(new Rect(0, 0, 10, 10), W, H, 0f, minZoom: 0.1f, maxZoom: 5f);
            Assert.Equal(5f, cam.Zoom, 3); // tiny rect would over-zoom; clamped to max

            cam.Focus(new Rect(0, 0, 100000, 100000), W, H, 0f, minZoom: 0.2f, maxZoom: 5f);
            Assert.Equal(0.2f, cam.Zoom, 3); // huge rect; clamped to min
        }

        [Fact]
        public void ClampPosition_clamps_each_axis_when_bounds_exceed_view()
        {
            var cam = new Camera2D { Zoom = 1f }; // halfW=400, halfH=300
            var bounds = new Rect(0, 0, 2000, 2000);
            // way past top-left
            var c = cam.ClampPosition(new Vector2(-500, -500), bounds, W, H);
            Assert.Equal(new Vector2(400, 300), c);
            // way past bottom-right
            c = cam.ClampPosition(new Vector2(5000, 5000), bounds, W, H);
            Assert.Equal(new Vector2(1600, 1700), c);
        }

        [Fact]
        public void ClampPosition_centers_each_axis_when_bounds_smaller_than_view()
        {
            var cam = new Camera2D { Zoom = 1f }; // view 800x600
            var bounds = new Rect(0, 0, 200, 100); // smaller than view on both axes
            var c = cam.ClampPosition(new Vector2(9999, -9999), bounds, W, H);
            Assert.Equal(new Vector2(100, 50), c); // centred regardless of desired
        }
    }
}
