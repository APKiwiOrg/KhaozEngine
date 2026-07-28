using System;
using System.Globalization;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Headless contract for the dissolve noise scale (issue #391). The noise cell is <c>1 / scale</c> world units,
    /// and the shadow depth pass rasterizes into a cascade whose texel world size grows with the cascade, so a fixed
    /// scale eventually puts the whole cell inside ONE shadow texel. At that point the dither is not a dither any
    /// more: the surviving fragments land as isolated texels with no coherent shape for the receiver's 3x3 kernel to
    /// resolve, which is what erased dithered casters in the outer cascades. These pins are GPU-free: they are pure
    /// arithmetic over the same fitted cascade radii the renderer computes.
    /// </summary>
    public sealed class ShadowDissolveNoiseTests
    {
        readonly ITestOutputHelper _out;
        public ShadowDissolveNoiseTests(ITestOutputHelper o) => _out = o;

        // A representative in-game framing: a wide third-person perspective camera over the ground, and the shadow
        // profile a shipping consumer runs (4 cascades, 2048 per cascade, 250 m reach - Ruinborne's pinned tier).
        const int CascadeCount = 4, Resolution = 2048;
        const float NearDistance = 16f, MaxDistance = 250f;

        static FlyCamera3D Camera() => new()
        {
            Position = new Vector3(0f, 12f, -30f),
            Yaw = 0f,
            Pitch = -0.35f,
            FieldOfView = MathF.PI / 3f,
            AspectRatio = 16f / 9f,
            NearPlane = 0.5f,
            FarPlane = 400f,
        };

        // Mirror Scene3D.ComputeShadowCascades: practical split, slice-sphere fit per cascade, and hand back each
        // cascade's fitted radius (the only input the noise scale needs beyond the resolution).
        static float[] CascadeRadii()
        {
            FlyCamera3D cam = Camera();
            Span<Vector3> corners = stackalloc Vector3[8];
            Assert.True(ShadowMapMath.FrustumCornersWorld(cam.ViewProjection, corners));
            Vector3 eye = cam.Eye, fwd = cam.Forward;
            Vector3 nearC = (corners[0] + corners[1] + corners[2] + corners[3]) * 0.25f;
            Vector3 farC = (corners[4] + corners[5] + corners[6] + corners[7]) * 0.25f;
            float camNear = Vector3.Dot(nearC - eye, fwd);
            float camFar = Vector3.Dot(farC - eye, fwd);
            float range = MathF.Max(camFar - camNear, 1e-3f);

            Span<float> splits = stackalloc float[ShadowSettings.MaxCascades];
            ShadowMapMath.FillCascadeSplits(splits, CascadeCount, NearDistance, MaxDistance);
            var radii = new float[CascadeCount];
            float prev = camNear;
            for (int i = 0; i < CascadeCount; i++)
            {
                float d = Math.Clamp(splits[i], camNear, camFar);
                ShadowMapMath.SliceBoundingSphere(corners, (prev - camNear) / range, (d - camNear) / range,
                    out _, out float r);
                radii[i] = r;
                prev = MathF.Max(d, prev);
            }
            return radii;
        }

        [Fact]
        public void Every_cascade_resolves_the_dissolve_dither()
        {
            float[] radii = CascadeRadii();
            for (int i = 0; i < radii.Length; i++)
            {
                float texel = ShadowMapMath.TexelWorldSize(radii[i], Resolution);
                float cellTexels = ShadowDissolveNoise.CellTexels(radii[i], Resolution);
                float baseCellTexels = 1f / ShadowDissolveNoise.BaseScale / texel;
                _out.WriteLine($"cascade {i}: radius {radii[i]:0.0} texel {texel:0.0000} m " +
                               $"cell {cellTexels:0.00} texels (base scale would give {baseCellTexels:0.00})");
                Assert.True(cellTexels >= ShadowDissolveNoise.MinCellTexels - 1e-3f,
                    $"cascade {i} (radius {radii[i]:0.0}, texel {texel:0.0000} m) resolves the dissolve dither at " +
                    $"only {cellTexels:0.00} shadow texels per noise cell, under the {ShadowDissolveNoise.MinCellTexels} " +
                    "the depth pass needs. The dither degenerates into isolated texels and the caster stops shadowing.");
            }
        }

        [Fact]
        public void Near_cascades_keep_the_base_scale_untouched()
        {
            // The rescale is a FLOOR on the cell, never a change for its own sake: wherever the base cell already
            // spans enough texels the shadow pass must evaluate exactly the colour pass's scale, so the near field
            // is bit-identical to before the per-cascade scale landed.
            float[] radii = CascadeRadii();
            float texel0 = ShadowMapMath.TexelWorldSize(radii[0], Resolution);
            Assert.True(1f / ShadowDissolveNoise.BaseScale / texel0 >= ShadowDissolveNoise.MinCellTexels,
                "cascade 0 no longer resolves the base-scale cell, so this pin is testing nothing");
            Assert.Equal(ShadowDissolveNoise.BaseScale, ShadowDissolveNoise.ScaleForCascade(radii[0], Resolution), 5);
        }

        [Fact]
        public void The_shader_literal_matches_the_promoted_constant()
        {
            // The GLSL sources splice BaseScaleGlsl in as text, so the two spellings of the one number can drift.
            Assert.Equal(ShadowDissolveNoise.BaseScale,
                float.Parse(ShadowDissolveNoise.BaseScaleGlsl, CultureInfo.InvariantCulture), 5);
        }

        [Fact]
        public void A_degenerate_cascade_never_divides_by_zero()
        {
            Assert.True(ShadowDissolveNoise.ScaleForCascade(0f, 0) > 0f);
            Assert.True(float.IsFinite(ShadowDissolveNoise.CellTexels(0f, 0)));
        }
    }
}
