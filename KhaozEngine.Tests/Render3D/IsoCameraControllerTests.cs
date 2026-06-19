using System;
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

        [Fact]
        public void Orbit_advances_azimuth_by_dx_times_yaw_speed()
        {
            var cam = Cam();
            var ctrl = new IsoCameraController(cam);
            float a0 = cam.Azimuth;

            ctrl.BeginOrbit(new Vector2(400, 300));
            Assert.True(ctrl.IsOrbiting);
            ctrl.UpdateOrbit(new Vector2(500, 300));   // dx = +100, dy = 0

            Assert.Equal(a0 + 100f * ctrl.OrbitYawSpeed, cam.Azimuth, 4);
        }

        [Fact]
        public void Orbit_clamps_elevation_to_min_and_max()
        {
            var ctrl = new IsoCameraController(Cam()) { MinElevation = MathF.PI / 12f, MaxElevation = MathF.PI * 0.49f };

            // Drag down hard (dy positive lowers elevation) -> sticks at MinElevation.
            ctrl.BeginOrbit(new Vector2(400, 0));
            ctrl.UpdateOrbit(new Vector2(400, 100000));
            Assert.Equal(ctrl.MinElevation, ctrl.Camera.Elevation, 4);
            ctrl.EndOrbit();

            // Drag up hard (dy negative raises elevation) -> sticks at MaxElevation.
            ctrl.BeginOrbit(new Vector2(400, 100000));
            ctrl.UpdateOrbit(new Vector2(400, 0));
            Assert.Equal(ctrl.MaxElevation, ctrl.Camera.Elevation, 4);
        }

        [Fact]
        public void Orbit_leaves_target_unchanged()
        {
            var cam = Cam();
            var ctrl = new IsoCameraController(cam);
            Vector3 t0 = cam.Target;

            ctrl.BeginOrbit(new Vector2(400, 300));
            ctrl.UpdateOrbit(new Vector2(700, 120));   // swing azimuth and elevation
            ctrl.EndOrbit();

            Assert.Equal(t0, cam.Target);
        }

        [Fact]
        public void Eye_stays_above_the_ground_plane_at_both_elevation_extremes()
        {
            var ctrl = new IsoCameraController(Cam());

            ctrl.Camera.Elevation = ctrl.MinElevation;
            Assert.True(ctrl.Camera.Eye.Y > ctrl.Camera.Target.Y);   // never flat/under the board

            ctrl.Camera.Elevation = ctrl.MaxElevation;
            Assert.True(ctrl.Camera.Eye.Y > ctrl.Camera.Target.Y);   // never degenerate at the top
        }

        [Fact]
        public void Orbit_is_a_no_op_when_not_orbiting()
        {
            var cam = Cam();
            var ctrl = new IsoCameraController(cam);
            float a0 = cam.Azimuth, e0 = cam.Elevation;

            ctrl.UpdateOrbit(new Vector2(900, 50));   // no BeginOrbit
            Assert.Equal(a0, cam.Azimuth, 6);
            Assert.Equal(e0, cam.Elevation, 6);

            ctrl.EndOrbit();                          // also a no-op
            Assert.False(ctrl.IsOrbiting);
        }
    }
}
