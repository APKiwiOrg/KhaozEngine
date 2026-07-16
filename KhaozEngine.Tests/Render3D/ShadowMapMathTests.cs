using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Pure (device-free) tests for the directional shadow-map light-space fitting + texel snapping
    /// (<see cref="ShadowMapMath"/>). These pin the two properties the shimmer-free shadow map depends on:
    /// the ortho frustum CONTAINS the focus sphere (nothing the camera looks at falls outside the map), and
    /// the light-space origin is QUANTIZED to shadow-texel increments so a sub-texel camera pan does not move
    /// the sampled shadow edge (the classic swimming-shadow fix). Run under plain <c>dotnet test</c>.
    /// </summary>
    public sealed class ShadowMapMathTests
    {
        static readonly Vector3 LightDir = Vector3.Normalize(new Vector3(-0.5f, -0.85f, -0.35f));

        [Fact]
        public void Fit_ContainsFocusSphere_AllCardinalPointsInsideNdc()
        {
            var focus = new Vector3(3f, 1f, -2f);
            const float radius = 5f;
            Matrix4x4 lightVp = ShadowMapMath.BuildLightViewProj(LightDir, focus, radius, resolution: 2048);

            // Every point on the focus sphere must project inside the light clip box [-1,1] x [-1,1] x [0,1].
            foreach (var dir in new[]
            {
                Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ,
                Vector3.Normalize(Vector3.One), Vector3.Normalize(new Vector3(1, -1, 1)),
            })
            {
                Vector3 p = focus + dir * radius;
                Vector4 clip = Vector4.Transform(new Vector4(p, 1f), lightVp);
                // Ortho: w == 1, so clip == NDC.
                Assert.InRange(clip.X, -1.0001f, 1.0001f);
                Assert.InRange(clip.Y, -1.0001f, 1.0001f);
                Assert.InRange(clip.Z, -0.0001f, 1.0001f);
            }
        }

        [Fact]
        public void Fit_FocusCenter_ProjectsNearClipCentre()
        {
            var focus = new Vector3(-2f, 0.5f, 4f);
            Matrix4x4 lightVp = ShadowMapMath.BuildLightViewProj(LightDir, focus, radius: 6f, resolution: 2048);
            Vector4 clip = Vector4.Transform(new Vector4(focus, 1f), lightVp);
            // The focus centre lands within one texel of the clip-space centre (texel snapping offsets it slightly).
            float texel = 2f / 2048f;
            Assert.InRange(clip.X, -2f * texel, 2f * texel);
            Assert.InRange(clip.Y, -2f * texel, 2f * texel);
        }

        [Fact]
        public void Snap_SubTexelCameraPan_DoesNotMoveProjectedWorldPoint()
        {
            // Two focus points separated by LESS than one shadow texel in light space must project a FIXED world
            // point to the SAME shadow-map texel (the snap quantizes the origin, killing the sub-texel swim).
            const int resolution = 1024;
            const float radius = 8f;
            float worldPerTexel = (2f * radius) / resolution;

            var focusA = new Vector3(0f, 0f, 0f);
            var focusB = focusA + new Vector3(worldPerTexel * 0.3f, 0f, 0f); // < 1 texel pan

            Matrix4x4 vpA = ShadowMapMath.BuildLightViewProj(LightDir, focusA, radius, resolution);
            Matrix4x4 vpB = ShadowMapMath.BuildLightViewProj(LightDir, focusB, radius, resolution);

            // A fixed world probe point (well inside both frusta).
            var probe = new Vector3(1.2f, 0.4f, -0.7f);
            Vector4 a = Vector4.Transform(new Vector4(probe, 1f), vpA);
            Vector4 b = Vector4.Transform(new Vector4(probe, 1f), vpB);

            // Convert to texel coordinates; the snap must keep them within a fraction of a texel of each other.
            float ax = (a.X * 0.5f + 0.5f) * resolution, ay = (a.Y * 0.5f + 0.5f) * resolution;
            float bx = (b.X * 0.5f + 0.5f) * resolution, by = (b.Y * 0.5f + 0.5f) * resolution;
            Assert.True(System.MathF.Abs(ax - bx) < 0.05f, $"x moved {System.MathF.Abs(ax - bx)} texels under a sub-texel pan");
            Assert.True(System.MathF.Abs(ay - by) < 0.05f, $"y moved {System.MathF.Abs(ay - by)} texels under a sub-texel pan");
        }

        [Fact]
        public void Snap_SuperTexelCameraPan_DoesMoveByWholeTexels()
        {
            // A pan LARGER than a texel must move the projection - but by an INTEGER number of texels (the origin
            // tracks the camera in texel-sized steps, not continuously and not frozen).
            const int resolution = 1024;
            const float radius = 8f;
            float worldPerTexel = (2f * radius) / resolution;

            var focusA = new Vector3(0f, 0f, 0f);
            var focusB = focusA + new Vector3(worldPerTexel * 10f, 0f, 0f); // 10-texel pan

            Matrix4x4 vpA = ShadowMapMath.BuildLightViewProj(LightDir, focusA, radius, resolution);
            Matrix4x4 vpB = ShadowMapMath.BuildLightViewProj(LightDir, focusB, radius, resolution);
            var probe = new Vector3(1.2f, 0.4f, -0.7f);
            Vector4 a = Vector4.Transform(new Vector4(probe, 1f), vpA);
            Vector4 b = Vector4.Transform(new Vector4(probe, 1f), vpB);
            float ax = (a.X * 0.5f + 0.5f) * resolution;
            float bx = (b.X * 0.5f + 0.5f) * resolution;
            float deltaTexels = ax - bx;
            // Non-trivially moved, and (post-snap) a near-integer number of texels.
            Assert.True(System.MathF.Abs(deltaTexels) > 1f, "a 10-texel pan should move the projection");
            float frac = System.MathF.Abs(deltaTexels - System.MathF.Round(deltaTexels));
            Assert.True(frac < 0.05f, $"post-snap movement should be whole texels; fractional part {frac}");
        }

        [Fact]
        public void TexelWorldSize_MatchesOrthoExtentOverResolution()
        {
            Assert.Equal(2f * 4f / 2048f, ShadowMapMath.TexelWorldSize(radius: 4f, resolution: 2048), 6);
        }

        [Fact]
        public void Fit_DegenerateLightDir_DoesNotThrowOrNaN()
        {
            // A near-zero light direction must fall back to a sane down-vector, not produce NaNs.
            Matrix4x4 vp = ShadowMapMath.BuildLightViewProj(Vector3.Zero, Vector3.Zero, radius: 3f, resolution: 512);
            Vector4 clip = Vector4.Transform(new Vector4(0f, 0f, 0f, 1f), vp);
            Assert.False(float.IsNaN(clip.X) || float.IsNaN(clip.Y) || float.IsNaN(clip.Z));
        }

        // ---- Cascade split (FillCascadeRadii) --------------------------------------------------------------------

        [Fact]
        public void CascadeRadii_Endpoints_AreExactlyFocusAndMaxDistance()
        {
            // The near cascade is ALWAYS exactly the focus radius (so cascade 0 == the pre-cascade single map and the
            // near-shadow contact quality is preserved), and the outermost cascade is ALWAYS exactly the max distance.
            Span<float> r = stackalloc float[4];
            ShadowMapMath.FillCascadeRadii(r, count: 3, focusRadius: 16f, maxDistance: 130f);
            Assert.Equal(16f, r[0], 3);
            Assert.Equal(130f, r[2], 3);
        }

        [Fact]
        public void CascadeRadii_AreStrictlyGrowing()
        {
            // Concentric cascades must grow outward so the receiver's tightest-containing selection is well defined.
            Span<float> r = stackalloc float[4];
            ShadowMapMath.FillCascadeRadii(r, count: 4, focusRadius: 12f, maxDistance: 120f);
            Assert.True(r[0] < r[1] && r[1] < r[2] && r[2] < r[3], $"radii not growing: {r[0]},{r[1]},{r[2]},{r[3]}");
        }

        [Fact]
        public void CascadeRadii_SingleCascade_IsJustFocusRadius()
        {
            // count == 1 reproduces the pre-cascade single map: one entry at the focus radius (max distance unused).
            Span<float> r = stackalloc float[4];
            ShadowMapMath.FillCascadeRadii(r, count: 1, focusRadius: 16f, maxDistance: 130f);
            Assert.Equal(16f, r[0], 3);
        }

        [Fact]
        public void CascadeRadii_MaxDistanceBelowFocus_ClampsToFocus()
        {
            // A nonsensical max distance below the focus radius collapses (the outer cascade never fits tighter than
            // the near one), so every cascade is at least the focus radius.
            Span<float> r = stackalloc float[4];
            ShadowMapMath.FillCascadeRadii(r, count: 3, focusRadius: 20f, maxDistance: 5f);
            Assert.True(r[0] >= 20f - 1e-3f && r[2] >= 20f - 1e-3f);
        }

        // ---- Atlas column transform ------------------------------------------------------------------------------

        [Fact]
        public void AtlasColumnTransform_SingleColumn_IsIdentity()
        {
            Assert.Equal(Matrix4x4.Identity, ShadowMapMath.AtlasColumnTransform(0, 1));
        }

        [Fact]
        public void AtlasColumnTransform_MapsCascadeNdcIntoItsColumn()
        {
            // For column i of n, the transform must map cascade clip.x [-1,1] onto the column's atlas-U sub-range
            // [i/n, (i+1)/n] (as NDC [-1 + 2i/n, -1 + 2(i+1)/n]), leaving Y and the stored depth Z untouched.
            const int n = 3;
            for (int i = 0; i < n; i++)
            {
                Matrix4x4 c = ShadowMapMath.AtlasColumnTransform(i, n);
                // clip' = clip * C (row vector). Feed the left/right/centre of the cascade clip x range.
                Vector4 left = Vector4.Transform(new Vector4(-1f, 0.3f, 0.7f, 1f), c);
                Vector4 right = Vector4.Transform(new Vector4(1f, 0.3f, 0.7f, 1f), c);
                float expLeft = -1f + 2f * i / n;
                float expRight = -1f + 2f * (i + 1) / n;
                Assert.Equal(expLeft, left.X, 4);
                Assert.Equal(expRight, right.X, 4);
                // Y and Z (the stored depth) are unchanged by the X-only column transform.
                Assert.Equal(0.3f, left.Y, 5);
                Assert.Equal(0.7f, left.Z, 5);
            }
        }

        // ---- Cascade selection (containment) ---------------------------------------------------------------------

        [Fact]
        public void SelectCascade_PicksTightestContainingCascade_AndFallsOutward()
        {
            // Concentric cascades of growing radius around one focus. A point near the focus falls in cascade 0, a
            // point past cascade 0's extent but inside cascade 1 falls in cascade 1, and a point beyond all is -1.
            var focus = new Vector3(0f, 0f, 0f);
            var mats = new Matrix4x4[3];
            Span<float> r = stackalloc float[4];
            ShadowMapMath.FillCascadeRadii(r, 3, focusRadius: 8f, maxDistance: 80f);
            for (int i = 0; i < 3; i++) mats[i] = ShadowMapMath.BuildLightViewProj(LightDir, focus, r[i], 2048);

            // On the focus plane, displace along a light-space axis by increasing world distance. Use the light's
            // right vector so the displacement lands in the map's XY plane.
            Vector3 near = focus + new Vector3(3f, 0f, 0f);     // ~3 units: inside cascade 0 (radius 8)
            Vector3 mid = focus + new Vector3(30f, 0f, 0f);     // ~30 units: past cascade 0, inside cascade 1/2
            Vector3 far = focus + new Vector3(200f, 0f, 0f);    // ~200 units: beyond every cascade

            int selNear = ShadowMapMath.SelectCascade(mats, 3, near);
            int selMid = ShadowMapMath.SelectCascade(mats, 3, mid);
            int selFar = ShadowMapMath.SelectCascade(mats, 3, far);
            Assert.Equal(0, selNear);
            Assert.True(selMid >= 1 && selMid <= 2, $"mid point should fall in a later cascade, got {selMid}");
            Assert.True(selMid >= selNear, "selection must fall outward for a farther point");
            Assert.Equal(-1, selFar);
        }
    }
}
