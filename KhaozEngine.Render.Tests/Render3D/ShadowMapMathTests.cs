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

        // ---- Cascade split (FillCascadeSplits) -------------------------------------------------------------------

        [Fact]
        public void CascadeRadii_Endpoints_AreExactlyFocusAndMaxDistance()
        {
            // The near cascade is ALWAYS exactly the focus radius (so cascade 0 == the pre-cascade single map and the
            // near-shadow contact quality is preserved), and the outermost cascade is ALWAYS exactly the max distance.
            Span<float> r = stackalloc float[4];
            ShadowMapMath.FillCascadeSplits(r, count: 3, nearDistance: 16f, maxDistance: 130f);
            Assert.Equal(16f, r[0], 3);
            Assert.Equal(130f, r[2], 3);
        }

        [Fact]
        public void CascadeRadii_AreStrictlyGrowing()
        {
            // Concentric cascades must grow outward so the receiver's tightest-containing selection is well defined.
            Span<float> r = stackalloc float[4];
            ShadowMapMath.FillCascadeSplits(r, count: 4, nearDistance: 12f, maxDistance: 120f);
            Assert.True(r[0] < r[1] && r[1] < r[2] && r[2] < r[3], $"radii not growing: {r[0]},{r[1]},{r[2]},{r[3]}");
        }

        [Fact]
        public void CascadeRadii_SingleCascade_IsJustFocusRadius()
        {
            // count == 1 reproduces the pre-cascade single map: one entry at the focus radius (max distance unused).
            Span<float> r = stackalloc float[4];
            ShadowMapMath.FillCascadeSplits(r, count: 1, nearDistance: 16f, maxDistance: 130f);
            Assert.Equal(16f, r[0], 3);
        }

        [Fact]
        public void CascadeRadii_MaxDistanceBelowFocus_ClampsToFocus()
        {
            // A nonsensical max distance below the focus radius collapses (the outer cascade never fits tighter than
            // the near one), so every cascade is at least the focus radius.
            Span<float> r = stackalloc float[4];
            ShadowMapMath.FillCascadeSplits(r, count: 3, nearDistance: 20f, maxDistance: 5f);
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
            ShadowMapMath.FillCascadeSplits(r, 3, nearDistance: 8f, maxDistance: 80f);
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

        // ---- Frustum-slice fit (the frustum-fit cascade rework) --------------------------------------------------

        static Matrix4x4 PerspectiveViewProj(Vector3 eye, Vector3 target, float fovY, float aspect, float near, float far)
            => Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY)
               * Matrix4x4.CreatePerspectiveFieldOfView(fovY, aspect, near, far);

        [Fact]
        public void FrustumCorners_PerspectiveCamera_MatchAnalyticExtents()
        {
            var eye = new Vector3(2f, 10f, -3f);
            var target = eye + new Vector3(0f, -0.5f, 1f);
            const float fovY = MathF.PI / 3f, aspect = 16f / 9f, near = 0.5f, far = 200f;
            Matrix4x4 vp = PerspectiveViewProj(eye, target, fovY, aspect, near, far);

            Span<Vector3> c = stackalloc Vector3[8];
            Assert.True(ShadowMapMath.FrustumCornersWorld(vp, c));

            // Near-quad centroid sits `near` in front of the eye along forward, far-quad centroid `far`.
            Vector3 fwd = Vector3.Normalize(target - eye);
            Vector3 nearC = (c[0] + c[1] + c[2] + c[3]) * 0.25f;
            Vector3 farC = (c[4] + c[5] + c[6] + c[7]) * 0.25f;
            Assert.Equal(near, Vector3.Dot(nearC - eye, fwd), 3);
            Assert.Equal(far, Vector3.Dot(farC - eye, fwd), 0);

            // Far-plane half-height matches tan(fov/2) * far (analytic frustum extent).
            float halfH = MathF.Tan(fovY / 2f) * far;
            float measuredHalfH = (c[6] - c[4]).Length() / 2f;   // (-,+) minus (-,-) on the far quad = full height
            Assert.Equal(halfH, measuredHalfH, 0);
        }

        [Fact]
        public void FrustumCorners_NonInvertible_ReturnsFalse()
        {
            Span<Vector3> c = stackalloc Vector3[8];
            Assert.False(ShadowMapMath.FrustumCornersWorld(default(Matrix4x4), c));
        }

        [Fact]
        public void SliceSphere_ContainsAllSliceCorners()
        {
            Matrix4x4 vp = PerspectiveViewProj(new Vector3(0f, 5f, 0f), new Vector3(4f, 0f, 10f),
                MathF.PI / 3f, 16f / 9f, 0.5f, 200f);
            Span<Vector3> c = stackalloc Vector3[8];
            Assert.True(ShadowMapMath.FrustumCornersWorld(vp, c));

            ShadowMapMath.SliceBoundingSphere(c, 0.1f, 0.35f, out Vector3 center, out float radius);
            for (int i = 0; i < 4; i++)
            {
                Vector3 edge = c[i + 4] - c[i];
                Assert.True((c[i] + edge * 0.10f - center).Length() <= radius + 1e-3f);
                Assert.True((c[i] + edge * 0.35f - center).Length() <= radius + 1e-3f);
            }
        }

        [Fact]
        public void SliceSphere_RadiusIsRotationInvariant()
        {
            // Same fov/aspect/near/far, three different camera orientations: the slice sphere radius must be
            // identical, so the ortho extent (and thus the texel world size the snap quantizes by) never
            // breathes as the camera rotates. This is the stability property the texel snap depends on.
            const float fovY = MathF.PI / 3f, aspect = 16f / 9f, near = 0.5f, far = 200f;
            var eye = new Vector3(1f, 8f, 2f);
            Span<Vector3> c = stackalloc Vector3[8];
            Span<float> radii = stackalloc float[3];
            var targets = new[] { eye + Vector3.UnitZ, eye + new Vector3(1f, -0.7f, -0.2f), eye - Vector3.UnitX };
            for (int k = 0; k < targets.Length; k++)
            {
                Assert.True(ShadowMapMath.FrustumCornersWorld(
                    PerspectiveViewProj(eye, targets[k], fovY, aspect, near, far), c));
                ShadowMapMath.SliceBoundingSphere(c, 0.0f, 0.2f, out _, out radii[k]);
            }
            Assert.Equal(radii[0], radii[1], 2);
            Assert.Equal(radii[0], radii[2], 2);
        }

        [Fact]
        public void SliceFit_VisibleNearPointLandsInCascade0_RegardlessOfGazeDirection()
        {
            // The regression the rework fixes: a caster visible NEAR the camera (bottom of screen) must get
            // near-cascade texel density even when the camera's central gaze is far away. Build the cascades
            // exactly the way Scene3D does (corners, splits, slice spheres, BuildLightViewProj) for a camera
            // pitched gently down whose gaze-ground intersection is ~70 units out, with the point of interest
            // 6 units ahead near the lower frustum edge. The old gaze-focus fit put such a point in the
            // OUTER cascade (or off coverage). The slice fit must select cascade 0.
            var eye = new Vector3(0f, 4f, 0f);
            var target = eye + new Vector3(0f, -0.055f, 1f);   // gaze hits y=0 ground ~72 units out
            const float near = 0.5f, far = 300f;
            Matrix4x4 vp = PerspectiveViewProj(eye, target, MathF.PI / 3f, 16f / 9f, near, far);
            Span<Vector3> corners = stackalloc Vector3[8];
            Assert.True(ShadowMapMath.FrustumCornersWorld(vp, corners));

            Span<float> splits = stackalloc float[3];
            ShadowMapMath.FillCascadeSplits(splits, 3, nearDistance: 16f, maxDistance: 130f);
            Span<Matrix4x4> mats = stackalloc Matrix4x4[3];
            float range = far - near, prev = near;
            for (int i = 0; i < 3; i++)
            {
                float d = Math.Clamp(splits[i], near, far);
                ShadowMapMath.SliceBoundingSphere(corners, (prev - near) / range, (d - near) / range,
                    out Vector3 center, out float radius);
                mats[i] = ShadowMapMath.BuildLightViewProj(LightDir, center, radius, 2048);
                prev = d;
            }

            // A ground point 6 units of view depth ahead, pushed toward the lower frustum edge (in view, the
            // ground below the camera): visible, near, and far from the gaze point.
            Vector3 fwd = Vector3.Normalize(target - eye);
            Vector3 pNear = eye + fwd * 6f + new Vector3(0f, -3f, 0f);
            Assert.Equal(0, ShadowMapMath.SelectCascade(mats, 3, pNear));

            // And a point out at ~200 units of view depth selects the OUTER cascade, not cascade 0. (At this
            // wide 60-degree FOV a slice's bounding SPHERE is a loose fit - cascade 1's sphere still reaches
            // past its own nominal 130-unit outer split, so the probe needs enough depth margin to clear it.)
            Vector3 pFar = eye + fwd * 200f;
            Assert.Equal(2, ShadowMapMath.SelectCascade(mats, 3, pFar));
        }

        // ---- Light-direction quantization (the moving-sun shimmer fix) --------------------------------------------

        // A unit direction from azimuth (around Y) + elevation degrees, matching QuantizeDirection's Atan2(X,Z) basis.
        static Vector3 DirFromAzEl(float azDeg, float elDeg)
        {
            float az = azDeg * MathF.PI / 180f, el = elDeg * MathF.PI / 180f;
            float cosEl = MathF.Cos(el);
            return new Vector3(MathF.Sin(az) * cosEl, MathF.Sin(el), MathF.Cos(az) * cosEl);
        }

        [Fact]
        public void Quantize_StepZeroOrNegative_IsExactPassthrough()
        {
            // Default 0 (and any non-positive step) returns the input untouched, so the fit is byte-identical to before.
            var dir = Vector3.Normalize(new Vector3(0.3f, -0.7f, 0.2f));
            Assert.Equal(dir, ShadowMapMath.QuantizeDirection(dir, 0f));
            Assert.Equal(dir, ShadowMapMath.QuantizeDirection(dir, -5f));
        }

        [Fact]
        public void Quantize_WithinOneCell_YieldsIdenticalVectorAndFitMatrices()
        {
            // Two directions inside the SAME lattice cell (az node 10, el node -40 at a 5-degree step) must snap to the
            // bit-identical vector, so BuildLightViewProj yields bit-identical matrices for a sub-step light rotation.
            const float step = 5f;
            Vector3 qa = ShadowMapMath.QuantizeDirection(DirFromAzEl(10.4f, -39.3f), step);
            Vector3 qb = ShadowMapMath.QuantizeDirection(DirFromAzEl(11.2f, -41.1f), step);
            Assert.Equal(qa, qb);

            var focus = new Vector3(2f, 1f, -3f);
            Matrix4x4 ma = ShadowMapMath.BuildLightViewProj(qa, focus, radius: 7f, resolution: 2048);
            Matrix4x4 mb = ShadowMapMath.BuildLightViewProj(qb, focus, radius: 7f, resolution: 2048);
            Assert.Equal(ma, mb);
        }

        [Fact]
        public void Quantize_CrossingStepBoundary_ChangesOutputExactlyOnce()
        {
            // Sweeping azimuth across a single lattice boundary (12.5 deg, between the 10 and 15 nodes at a 5-degree
            // step) flips the snapped output exactly once - discrete steps, not a continuous slide.
            const float step = 5f;
            Vector3? prev = null;
            int transitions = 0;
            for (float azDeg = 11f; azDeg <= 14f + 1e-4f; azDeg += 0.1f)
            {
                Vector3 q = ShadowMapMath.QuantizeDirection(DirFromAzEl(azDeg, -40f), step);
                if (prev is { } p && q != p) transitions++;
                prev = q;
            }
            Assert.Equal(1, transitions);
        }

        [Fact]
        public void Quantize_DegenerateInputs_DoNotThrowOrNaN()
        {
            // Zero, near-zero, and the two poles (azimuth ill-defined) must return a finite unit-ish vector, never NaN.
            foreach ((Vector3 dir, float step) in new (Vector3, float)[]
            {
                (Vector3.Zero, 5f),
                (new Vector3(1e-9f, 1e-9f, 1e-9f), 5f),
                (new Vector3(0f, 1f, 0f), 5f),
                (new Vector3(0f, -1f, 0f), 5f),
                (new Vector3(0.01f, 0.9999f, 0f), 3f),
            })
            {
                Vector3 q = ShadowMapMath.QuantizeDirection(dir, step);
                Assert.True(float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z), $"NaN for {dir}: {q}");
                Assert.True(q.Length() > 0.5f, $"degenerate quantize should stay a unit-ish vector, got {q}");
            }
        }
    }
}
