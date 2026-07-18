using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Pure frustum-plane extraction + AABB/sphere containment, headless. The convention is verified against the
    /// engine's ACTUAL camera matrices (orthographic <see cref="IsoCamera3D"/> and perspective
    /// <see cref="FollowCamera3D"/>), not a hand-authored matrix, so a wrong row/column assumption that would pass a
    /// naive test fails here (the regression the brief calls out).
    /// </summary>
    public class FrustumPlanesTests
    {
        static bool BoxInside(FrustumPlanes f, Vector3 center, Vector3 halfExtent) =>
            f.IntersectsAabb(center - halfExtent, center + halfExtent);

        // A tiny box at a point is a proxy for "is this point inside".
        static bool PointInside(FrustumPlanes f, Vector3 p) => BoxInside(f, p, new Vector3(1e-3f));

        [Fact]
        public void Ortho_box_at_target_inside_box_far_behind_outside()
        {
            var cam = new IsoCamera3D { Target = Vector3.Zero, OrthoSize = 10f, AspectRatio = 1f };
            FrustumPlanes f = FrustumPlanes.Extract(cam.ViewProjection);

            Assert.True(PointInside(f, cam.Target));                       // looking straight at it
            // A point behind the camera (past the eye, away from the target) is outside the near plane.
            Vector3 behind = cam.Eye + (cam.Eye - cam.Target);
            Assert.False(PointInside(f, behind));
        }

        [Fact]
        public void Perspective_box_ahead_inside_box_behind_outside()
        {
            var cam = new FollowCamera3D { Target = Vector3.Zero, AspectRatio = 16f / 9f };
            cam.Pitch = 0.3f; cam.Distance = 8f;
            FrustumPlanes f = FrustumPlanes.Extract(cam.ViewProjection);

            Assert.True(PointInside(f, cam.Target));
            // Behind the eye along the view direction: outside.
            Vector3 behindEye = cam.Eye - cam.Forward * 2f;
            Assert.False(PointInside(f, behindEye));
        }

        [Fact]
        public void Ortho_points_outside_each_lateral_face_are_rejected()
        {
            // Axis-aligned ortho looking down -Z so the frustum faces map to world X/Y (easy to reason about).
            var cam = new IsoCamera3D
            {
                Target = Vector3.Zero, Azimuth = 0f, Elevation = 0f, OrthoSize = 10f, AspectRatio = 1f,
                Distance = 50f, NearPlane = 0.1f, FarPlane = 200f,
            };
            FrustumPlanes f = FrustumPlanes.Extract(cam.ViewProjection);

            // Ortho half-extent: OrthoSize/2 vertically, *aspect horizontally => 5 each here.
            Assert.True(PointInside(f, new Vector3(4.9f, 0f, 0f)));    // just inside +X face
            Assert.False(PointInside(f, new Vector3(6.0f, 0f, 0f)));   // outside +X (right)
            Assert.False(PointInside(f, new Vector3(-6.0f, 0f, 0f)));  // outside -X (left)
            Assert.False(PointInside(f, new Vector3(0f, 6.0f, 0f)));   // outside +Y (top)
            Assert.False(PointInside(f, new Vector3(0f, -6.0f, 0f)));  // outside -Y (bottom)
        }

        [Fact]
        public void Straddling_box_is_conservatively_kept()
        {
            var cam = new IsoCamera3D
            {
                Target = Vector3.Zero, Azimuth = 0f, Elevation = 0f, OrthoSize = 10f, AspectRatio = 1f,
            };
            FrustumPlanes f = FrustumPlanes.Extract(cam.ViewProjection);

            // A box centred just outside the right face but wide enough to straddle it: must be kept (not culled).
            Assert.True(BoxInside(f, new Vector3(5.5f, 0f, 0f), new Vector3(1.0f, 1.0f, 1.0f)));
        }

        [Fact]
        public void Sphere_test_matches_lateral_faces()
        {
            var cam = new IsoCamera3D
            {
                Target = Vector3.Zero, Azimuth = 0f, Elevation = 0f, OrthoSize = 10f, AspectRatio = 1f,
            };
            FrustumPlanes f = FrustumPlanes.Extract(cam.ViewProjection);

            // Centre 6 units right (outside the +X=5 face) with radius 0.5 -> fully outside (culled).
            Assert.False(f.IntersectsSphere(new Vector3(6f, 0f, 0f), 0.5f));
            // Same centre with radius 1.5 -> reaches back across the face -> kept.
            Assert.True(f.IntersectsSphere(new Vector3(6f, 0f, 0f), 1.5f));
            // Centre at the target -> always inside.
            Assert.True(f.IntersectsSphere(Vector3.Zero, 0.1f));
        }

        [Fact]
        public void Perspective_sphere_far_behind_camera_is_culled()
        {
            var cam = new FollowCamera3D { Target = Vector3.Zero, AspectRatio = 1f };
            cam.Pitch = 0.2f; cam.Distance = 6f;
            FrustumPlanes f = FrustumPlanes.Extract(cam.ViewProjection);

            Vector3 wayBehind = cam.Eye - cam.Forward * 20f;
            Assert.False(f.IntersectsSphere(wayBehind, 1f));
            Assert.True(f.IntersectsSphere(cam.Target, 1f));
        }

        [Fact]
        public void Normalized_planes_give_true_signed_distance()
        {
            var cam = new IsoCamera3D
            {
                Target = Vector3.Zero, Azimuth = 0f, Elevation = 0f, OrthoSize = 10f, AspectRatio = 1f,
            };
            FrustumPlanes n = FrustumPlanes.Extract(cam.ViewProjection).Normalized();
            // Right face is at X=5; a point at X=3 is 2 units inside it (plane index 1 = right).
            Vector4 right = n[1];
            var p = new Vector3(3f, 0f, 0f);
            float d = right.X * p.X + right.Y * p.Y + right.Z * p.Z + right.W;
            Assert.InRange(d, 1.9f, 2.1f);
        }
    }
}
