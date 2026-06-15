using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class IsoCamera3DPickingTests
    {
        // Project a known world point to a screen pixel the way rendering does, then pick it back.
        static Vector2 WorldToScreen(IsoCamera3D cam, Vector3 world, int vw, int vh)
        {
            var clip = Vector4.Transform(new Vector4(world, 1f), cam.ViewProjection);
            var ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
            return new Vector2((ndc.X * 0.5f + 0.5f) * vw, (0.5f - ndc.Y * 0.5f) * vh);
        }

        [Fact]
        public void ScreenToGround_RoundTripsAGroundPoint()
        {
            var cam = new IsoCamera3D { Target = new Vector3(3, 0, -2), OrthoSize = 12f, AspectRatio = 16f / 9f };
            var world = new Vector3(5f, 0f, 1f);                 // on the ground plane y=0
            Vector2 screen = WorldToScreen(cam, world, 1600, 900);

            Vector3 hit = cam.ScreenToGround(screen, 1600, 900);

            Assert.Equal(world.X, hit.X, 2);
            Assert.Equal(0f, hit.Y, 4);
            Assert.Equal(world.Z, hit.Z, 2);
        }

        [Fact]
        public void ScreenCentre_MapsToTheCameraTargetGroundPoint()
        {
            var cam = new IsoCamera3D { Target = new Vector3(7, 0, 4), OrthoSize = 10f, AspectRatio = 1f };
            Vector3 hit = cam.ScreenToGround(new Vector2(400, 400), 800, 800);
            Assert.Equal(7f, hit.X, 2);
            Assert.Equal(4f, hit.Z, 2);
        }

        [Fact]
        public void ScreenToGround_RespectsACustomGroundHeight()
        {
            var cam = new IsoCamera3D();
            Vector3 hit = cam.ScreenToGround(new Vector2(500, 220), 1000, 600, groundY: 2.5f);
            Assert.Equal(2.5f, hit.Y, 4);
        }

        [Fact]
        public void ScreenToRay_DirectionMatchesCameraForward()
        {
            var cam = new IsoCamera3D { Target = Vector3.Zero };
            Ray r = cam.ScreenToRay(new Vector2(123, 456), 1000, 700);
            Vector3 d = Vector3.Normalize(r.Direction);
            Vector3 f = cam.Forward;
            Assert.Equal(f.X, d.X, 3);
            Assert.Equal(f.Y, d.Y, 3);
            Assert.Equal(f.Z, d.Z, 3);   // orthographic: every ray is parallel to Forward
        }
    }
}
