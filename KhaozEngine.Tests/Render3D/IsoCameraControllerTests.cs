using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class IsoCameraControllerTests
    {
        const int VW = 1280, VH = 720;

        static IsoCamera3D Cam()
        {
            var c = new IsoCamera3D { AspectRatio = (float)VW / VH, OrthoSize = 10f, Zoom = 1f };
            c.Target = new Vector3(3f, 0f, 2f);
            return c;
        }

        [Fact]
        public void Zoom_keeps_the_ground_point_under_the_cursor_fixed()
        {
            var cam = Cam();
            var ctrl = new IsoCameraController(cam);
            var cursor = new Vector2(900, 250);   // off-centre so the anchor shift is non-trivial

            Vector3 before = cam.ScreenToGround(cursor, VW, VH);
            ctrl.Zoom(2f, cursor, VW, VH);         // zoom in
            Vector3 after = cam.ScreenToGround(cursor, VW, VH);

            Assert.True(cam.Zoom > 1f);                       // zoomed in
            Assert.True(Vector3.Distance(before, after) < 1e-3f); // same world point under the cursor
        }

        [Fact]
        public void Zoom_clamps_to_min_and_max()
        {
            var cam = Cam();
            var ctrl = new IsoCameraController(cam) { MinZoom = 0.5f, MaxZoom = 3f };
            var cursor = new Vector2(VW / 2f, VH / 2f);

            for (int i = 0; i < 50; i++) ctrl.Zoom(1f, cursor, VW, VH);   // spam zoom-in
            Assert.Equal(3f, cam.Zoom, 3);

            for (int i = 0; i < 100; i++) ctrl.Zoom(-1f, cursor, VW, VH); // spam zoom-out
            Assert.Equal(0.5f, cam.Zoom, 3);
        }

        [Fact]
        public void Grab_pan_keeps_the_grabbed_point_under_the_moving_cursor()
        {
            var cam = Cam();
            var ctrl = new IsoCameraController(cam);
            var start = new Vector2(640, 360);

            Vector3 grabbed = cam.ScreenToGround(start, VW, VH);
            ctrl.BeginPan(start, VW, VH);
            Assert.True(ctrl.IsPanning);

            var moved = new Vector2(820, 300);    // drag elsewhere
            ctrl.UpdatePan(moved, VW, VH);

            // The grabbed world point now sits under the moved cursor.
            Vector3 underMoved = cam.ScreenToGround(moved, VW, VH);
            Assert.True(Vector3.Distance(grabbed, underMoved) < 1e-3f);

            ctrl.EndPan();
            Assert.False(ctrl.IsPanning);
        }

        [Fact]
        public void UpdatePan_without_BeginPan_is_a_no_op()
        {
            var cam = Cam();
            var ctrl = new IsoCameraController(cam);
            Vector3 t0 = cam.Target;
            ctrl.UpdatePan(new Vector2(100, 100), VW, VH);
            Assert.Equal(t0, cam.Target);
        }

        [Fact]
        public void Pan_clamps_target_to_bounds()
        {
            var cam = Cam();
            var ctrl = new IsoCameraController(cam)
            {
                PanMin = new Vector3(-1f, 0f, -1f),
                PanMax = new Vector3(1f, 0f, 1f),
            };

            // Drag hard in one direction; Target must stay inside the box on X/Z.
            ctrl.BeginPan(new Vector2(640, 360), VW, VH);
            ctrl.UpdatePan(new Vector2(0, 0), VW, VH);
            ctrl.UpdatePan(new Vector2(VW, VH), VW, VH);

            Assert.InRange(cam.Target.X, -1f, 1f);
            Assert.InRange(cam.Target.Z, -1f, 1f);
        }
    }
}
