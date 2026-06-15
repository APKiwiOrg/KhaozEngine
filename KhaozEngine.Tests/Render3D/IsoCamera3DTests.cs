using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class IsoCamera3DTests
    {
        static Vector2 ProjectNdc(IIsoCamera3D cam, Vector3 world)
        {
            Vector4 clip = Vector4.Transform(new Vector4(world, 1f), cam.ViewProjection);
            return new Vector2(clip.X / clip.W, clip.Y / clip.W);
        }

        [Fact]
        public void Defaults_are_iso_angles()
        {
            var cam = new IsoCamera3D();
            Assert.Equal(MathF.PI / 4f, cam.Azimuth, 5);
            Assert.Equal(MathF.Atan(0.5f), cam.Elevation, 5);
        }

        [Fact]
        public void Forward_matches_azimuth_elevation()
        {
            var cam = new IsoCamera3D { Azimuth = MathF.PI / 4f, Elevation = MathF.Atan(0.5f) };
            float cE = MathF.Cos(cam.Elevation), sE = MathF.Sin(cam.Elevation);
            float cA = MathF.Cos(cam.Azimuth), sA = MathF.Sin(cam.Azimuth);
            var expected = -Vector3.Normalize(new Vector3(cE * sA, sE, cE * cA));
            Assert.True(Vector3.Distance(expected, cam.Forward) < 1e-4f, $"{cam.Forward} != {expected}");
        }

        [Fact]
        public void Target_projects_to_center()
        {
            var cam = new IsoCamera3D { Target = new Vector3(3, 1, -2) };
            var ndc = ProjectNdc(cam, cam.Target);
            Assert.True(MathF.Abs(ndc.X) < 1e-4f && MathF.Abs(ndc.Y) < 1e-4f, ndc.ToString());
        }

        [Fact]
        public void Vertical_offset_of_half_orthosize_projects_to_top_edge()
        {
            var cam = new IsoCamera3D { OrthoSize = 10f, AspectRatio = 1f, Zoom = 1f };
            Matrix4x4.Invert(cam.View, out var invView);
            var camUp = Vector3.Normalize(new Vector3(invView.M21, invView.M22, invView.M23));
            var p = cam.Target + camUp * (cam.OrthoSize * 0.5f);
            var ndc = ProjectNdc(cam, p);
            Assert.True(MathF.Abs(ndc.Y - 1f) < 1e-3f, $"ndc.Y={ndc.Y}");
        }

        [Fact]
        public void Zoom_scales_projection()
        {
            var a = new IsoCamera3D { OrthoSize = 10f, AspectRatio = 1f, Zoom = 1f };
            var b = new IsoCamera3D { OrthoSize = 10f, AspectRatio = 1f, Zoom = 2f };
            Matrix4x4.Invert(a.View, out var inv);
            var camUp = Vector3.Normalize(new Vector3(inv.M21, inv.M22, inv.M23));
            var p = a.Target + camUp * 1f;
            float ya = ProjectNdc(a, p).Y, yb = ProjectNdc(b, p).Y;
            Assert.True(MathF.Abs(yb - 2f * ya) < 1e-3f, $"ya={ya} yb={yb}");
        }
    }
}
