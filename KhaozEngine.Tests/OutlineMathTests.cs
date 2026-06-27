using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests
{
    public class OutlineMathTests
    {
        // NDC depth (System.Numerics perspective, D3D/[0,1] convention) for a given view-space eye distance.
        static float NdcFromView(float viewDist, float near, float far)
            => far * (viewDist - near) / (viewDist * (far - near));

        [Fact]
        public void Ortho_projection_is_flagged_non_perspective()
        {
            var ortho = Matrix4x4.CreateOrthographic(10f, 6f, 0.5f, 100f);
            CameraDepth cam = OutlineMath.ExtractCameraDepth(ortho);
            Assert.False(cam.IsPerspective);
        }

        [Fact]
        public void Perspective_projection_recovers_near_and_far()
        {
            var persp = Matrix4x4.CreatePerspectiveFieldOfView(1.0f, 16f / 9f, 0.1f, 500f);
            CameraDepth cam = OutlineMath.ExtractCameraDepth(persp);
            Assert.True(cam.IsPerspective);
            Assert.Equal(0.1f, cam.Near, 3);
            Assert.Equal(500f, cam.Far, 0);
        }

        [Fact]
        public void Linearize_maps_ndc_endpoints_to_near_and_far()
        {
            float n = 0.1f, f = 500f;
            Assert.Equal(n, OutlineMath.LinearizeDepth(NdcFromView(n, n, f), n, f), 3);
            Assert.Equal(f, OutlineMath.LinearizeDepth(NdcFromView(f, n, f), n, f), 0);
            // Round-trips an interior point.
            Assert.Equal(37.5f, OutlineMath.LinearizeDepth(NdcFromView(37.5f, n, f), n, f), 1);
        }

        [Fact]
        public void Relative_depth_metric_is_stable_across_zoom_while_raw_is_not()
        {
            // A receding plane: equal screen steps map to a ~constant MULTIPLICATIVE view-depth step (5%/px),
            // so the relative metric |dLin|/lin is constant with distance, but the raw NDC delta collapses far.
            const float n = 0.1f, f = 500f, step = 1.05f;
            float NearLo = 5f, NearHi = 5f * step;
            float FarLo = 300f, FarHi = 300f * step;

            float rawNear = MathF.Abs(NdcFromView(NearHi, n, f) - NdcFromView(NearLo, n, f));
            float rawFar = MathF.Abs(NdcFromView(FarHi, n, f) - NdcFromView(FarLo, n, f));

            float relNear = MathF.Abs(OutlineMath.LinearizeDepth(NdcFromView(NearHi, n, f), n, f)
                                    - OutlineMath.LinearizeDepth(NdcFromView(NearLo, n, f), n, f))
                            / OutlineMath.LinearizeDepth(NdcFromView(NearLo, n, f), n, f);
            float relFar = MathF.Abs(OutlineMath.LinearizeDepth(NdcFromView(FarHi, n, f), n, f)
                                   - OutlineMath.LinearizeDepth(NdcFromView(FarLo, n, f), n, f))
                           / OutlineMath.LinearizeDepth(NdcFromView(FarLo, n, f), n, f);

            // Raw NDC delta near is many times the far delta (non-linear compression) -> a fixed threshold flickers.
            Assert.True(rawNear > rawFar * 8f, $"raw not collapsing: near={rawNear}, far={rawFar}");
            // Relative linear metric is ~equal near and far (both ~0.05) -> a fixed threshold is stable.
            Assert.Equal(relNear, relFar, 2);
        }
    }
}
