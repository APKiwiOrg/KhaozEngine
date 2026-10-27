using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Device-free tripwires on the two halves of the shadow pipeline that only exist as GLSL text, so a headless run
    /// can still hold them to the CPU mirrors in <see cref="ShadowMapMath"/> (issue #394). Neither can be executed
    /// here, so each is asserted the way <c>ShaderSourceValidationTests</c> works on the same sources: on the shader
    /// text, paired with a behavioural assertion on the C# mirror it must agree with.
    /// <list type="bullet">
    /// <item>RECEIVER select: the GLSL <c>projectCascade</c> bounds test must reject BOTH ends of the depth range,
    /// matching <see cref="ShadowMapMath.SelectCascade"/>. Dropping the <c>z &lt; 0</c> half let a receiver in front
    /// of a cascade's near plane claim that cascade and read fully lit with a hard edge.</item>
    /// <item>DEPTH pass: every shadow depth vertex must pancake the rasterized depth at the near plane and every
    /// shadow depth fragment must clamp the stored depth, matching <see cref="ShadowMapMath.PancakeDepth"/>. Without
    /// it a caster up-light of the near plane is clipped away and the ground it shades reads the atlas clear value.</item>
    /// </list>
    /// </summary>
    public sealed class ShadowShaderContractTests
    {
        // The exact bounds test the receiver applies to a cascade-local depth. Both ends, in the CPU mirror's order.
        const string DepthBoundsGlsl = "depth < 0.0 || depth > 1.0";

        // The depth pass's near-plane pancake, in its two halves: the rasterized clip depth (vertex) and the stored
        // R32F value (fragment). Split deliberately - clamping the VARYING at the vertex instead would tilt the
        // interpolated depth across a triangle crossing the near plane and under-shadow.
        const string VertexPancakeGlsl = "lightClip.z = max(lightClip.z, 0.0);";
        const string FragmentPancakeGlsl = "max(vLightDepth, 0.0)";
        const string ComplementKeepGlsl =
            "bool keep = vDissolveComplement > 0.5 ? mask < threshold : mask >= threshold;";

        [Fact]
        public void ProjectCascade_RejectsBothDepthBounds_MatchingSelectCascade()
        {
            Assert.Contains(DepthBoundsGlsl, ShaderSources.LightingCommonGlsl, StringComparison.Ordinal);
        }

        [Fact]
        public void SelectCascade_RejectsAReceiverInFrontOfTheNearPlane_AndFallsOutward()
        {
            // Two concentric cascades. A point on the light axis just up-light of the near cascade's near plane has
            // valid UV in that cascade (it is dead centre of the map) but negative depth, so the tight cascade must
            // decline it and the wider one must take it. This is the case the GPU predicate above has to agree on.
            Vector3 light = Vector3.Normalize(new Vector3(0.3f, -0.5f, 0.81f));
            var focus = new Vector3(2f, 1f, -4f);
            const float r0 = 10f, r1 = 40f;
            var mats = new[]
            {
                ShadowMapMath.BuildLightViewProj(light, focus, r0, 2048),
                ShadowMapMath.BuildLightViewProj(light, focus, r1, 2048),
            };

            // 2*r0 up-light of the focus is exactly cascade 0's near plane, so a little further is in front of it.
            Vector3 inFront = focus - light * (2f * r0 + 3f);
            Assert.True(ClipZ(mats[0], inFront) < 0f, "probe is not actually in front of cascade 0's near plane");
            Assert.InRange(ClipZ(mats[1], inFront), 0f, 1f);
            Assert.Equal(1, ShadowMapMath.SelectCascade(mats, 2, inFront));

            // The same point pulled back INSIDE cascade 0's depth range does select cascade 0, so the rejection above
            // is the depth bound doing its job and not the UV margin.
            Vector3 inside = focus - light * (2f * r0 - 3f);
            Assert.InRange(ClipZ(mats[0], inside), 0f, 1f);
            Assert.Equal(0, ShadowMapMath.SelectCascade(mats, 2, inside));
        }

        [Theory]
        [InlineData(nameof(ShaderSources.ShadowDepthVert))]
        [InlineData(nameof(ShaderSources.ShadowDepthDissolveVert))]
        [InlineData(nameof(ShaderSources.ShadowDepthCutoutVert))]
        [InlineData(nameof(ShaderSources.SkinnedShadowDepthVert))]
        [InlineData(nameof(ShaderSources.SkinnedShadowDepthDissolveVert))]
        public void EveryShadowDepthVertex_PancakesTheRasterizedDepth(string which)
        {
            string src = which switch
            {
                nameof(ShaderSources.ShadowDepthVert) => ShaderSources.ShadowDepthVert,
                nameof(ShaderSources.ShadowDepthDissolveVert) => ShaderSources.ShadowDepthDissolveVert,
                nameof(ShaderSources.ShadowDepthCutoutVert) => ShaderSources.ShadowDepthCutoutVert,
                nameof(ShaderSources.SkinnedShadowDepthDissolveVert) => ShaderSources.SkinnedShadowDepthDissolveVert,
                _ => ShaderSources.SkinnedShadowDepthVert,
            };
            Assert.Contains(VertexPancakeGlsl, src, StringComparison.Ordinal);
            // The stored varying must come from the UNCLAMPED clip position, so the pancake never tilts the
            // interpolated depth of a triangle that crosses the near plane.
            Assert.Contains("vLightDepth = lightClip.z / lightClip.w;", src, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(nameof(ShaderSources.ShadowDepthFrag))]
        [InlineData(nameof(ShaderSources.ShadowDepthDissolveFrag))]
        [InlineData(nameof(ShaderSources.ShadowDepthDissolveInvertedFrag))]
        [InlineData(nameof(ShaderSources.ShadowDepthCutoutFrag))]
        [InlineData(nameof(ShaderSources.ShadowDepthCutoutInvertedFrag))]
        public void EveryShadowDepthFragment_ClampsTheStoredDepth(string which)
        {
            string src = which switch
            {
                nameof(ShaderSources.ShadowDepthFrag) => ShaderSources.ShadowDepthFrag,
                nameof(ShaderSources.ShadowDepthDissolveFrag) => ShaderSources.ShadowDepthDissolveFrag,
                nameof(ShaderSources.ShadowDepthCutoutFrag) => ShaderSources.ShadowDepthCutoutFrag,
                nameof(ShaderSources.ShadowDepthCutoutInvertedFrag) => ShaderSources.ShadowDepthCutoutInvertedFrag,
                _ => ShaderSources.ShadowDepthDissolveInvertedFrag,
            };
            Assert.Contains(FragmentPancakeGlsl, src, StringComparison.Ordinal);
        }

        [Fact]
        public void TheCutoutDepthFragments_AlphaTestLikeTheColourPass()
        {
            // Issue #15. The shadow must cut out exactly the texels the mesh does, so both cutout fragments apply
            // ModelFrag's own test (cutoff from ISpecParams.z, skipped at 0) to the albedo alpha, and the plain one
            // keeps the plain dissolve's complement-aware keep set so a dissolving MASK caster still erodes.
            Assert.Contains("if (vSpecParams.z > 0.0 && texRgba.a < vSpecParams.z) discard;", ShaderSources.ModelFrag,
                StringComparison.Ordinal);
            Assert.Contains("vCutoff = ISpecParams.z;", ShaderSources.ShadowDepthCutoutVert, StringComparison.Ordinal);
            Assert.Contains("vUv = TexCoord;", ShaderSources.ShadowDepthCutoutVert, StringComparison.Ordinal);
            Assert.Contains("if (vCutoff > 0.0 && alpha < vCutoff) discard;", ShaderSources.ShadowDepthCutoutFrag,
                StringComparison.Ordinal);
            Assert.Contains("if (vCutoff > 0.0 && alpha < vCutoff + vDissolveComplement * 1e-30) discard;",
                ShaderSources.ShadowDepthCutoutInvertedFrag, StringComparison.Ordinal);
            Assert.Contains(ComplementKeepGlsl, ShaderSources.ShadowDepthCutoutFrag, StringComparison.Ordinal);
            Assert.Contains("if (mask >= 1.0 - threshold) discard;", ShaderSources.ShadowDepthCutoutInvertedFrag,
                StringComparison.Ordinal);

            // The cheap pipelines stay cheap: neither the plain depth fragment nor the dissolve ones sample anything.
            Assert.DoesNotContain("texture(", ShaderSources.ShadowDepthFrag, StringComparison.Ordinal);
            Assert.DoesNotContain("texture(", ShaderSources.ShadowDepthDissolveFrag, StringComparison.Ordinal);
            Assert.DoesNotContain("discard", ShaderSources.ShadowDepthFrag, StringComparison.Ordinal);
        }

        [Fact]
        public void TheSkinnedDissolveDepthVertex_FeedsTheRigidDissolveFragmentTheQueuedKeepSet()
        {
            // Issue #387. The skinned dissolve vertex reuses ShadowDepthDissolveFrag unchanged, so it must emit
            // exactly that fragment's four interpolants, with the threshold and complement from its slot.
            string vert = ShaderSources.SkinnedShadowDepthDissolveVert;
            Assert.Contains("layout(location=0) out float vLightDepth;", vert, StringComparison.Ordinal);
            Assert.Contains("layout(location=1) out vec3 vNoisePos;", vert, StringComparison.Ordinal);
            Assert.Contains("layout(location=2) out vec2 vDissolve;", vert, StringComparison.Ordinal);
            Assert.Contains("layout(location=3) out float vDissolveComplement;", vert, StringComparison.Ordinal);
            Assert.Contains("vDissolve = vec2(DissolveParams.y, 0.0);", vert, StringComparison.Ordinal);
            Assert.Contains("vDissolveComplement = DissolveParams.z;", vert, StringComparison.Ordinal);
            Assert.Contains("vNoisePos = (world.xyz + RenderOrigin.xyz) * DissolveParams.x;", vert, StringComparison.Ordinal);
            Assert.Contains("bool keep = vDissolve.z > 0.5 ? mask < threshold : mask >= threshold;",
                ShaderSources.SkinnedModelDissolveFrag, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(-2.5f, 0f)]
        [InlineData(-0.001f, 0f)]
        [InlineData(0f, 0f)]
        [InlineData(0.42f, 0.42f)]
        [InlineData(1f, 1f)]
        public void PancakeDepth_ClampsBelowZeroOnly(float clipDepth, float expected)
        {
            Assert.Equal(expected, ShadowMapMath.PancakeDepth(clipDepth), 6);
        }

        [Fact]
        public void RigidColorAndShadowDepthCarryTheSameComplementPhase()
        {
            Assert.Contains("layout(location=14) in float IDissolveComplement;", ShaderSources.ModelVert);
            Assert.Contains("vDissolveComplement = IDissolveComplement;", ShaderSources.ModelVert);
            Assert.Contains("layout(location=10) in float vDissolveComplement;", ShaderSources.ModelFrag);
            Assert.Contains(ComplementKeepGlsl, ShaderSources.ModelFrag, StringComparison.Ordinal);

            Assert.Contains("layout(location=14) in float IDissolveComplement;", ShaderSources.ShadowDepthDissolveVert);
            Assert.Contains("vDissolveComplement = IDissolveComplement;", ShaderSources.ShadowDepthDissolveVert);
            Assert.Contains("layout(location=3) in float vDissolveComplement;", ShaderSources.ShadowDepthDissolveFrag);
            Assert.Contains(ComplementKeepGlsl, ShaderSources.ShadowDepthDissolveFrag, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(0.00f, 0.00f)]
        [InlineData(0.10f, 0.25f)]
        [InlineData(0.25f, 0.25f)]
        [InlineData(0.49f, 0.50f)]
        [InlineData(0.50f, 0.50f)]
        [InlineData(0.90f, 0.75f)]
        [InlineData(1.00f, 1.00f)]
        public void ComplementPhaseAssignsEveryNoiseSampleToExactlyOneRepresentation(float mask, float transition)
        {
            bool lod0Keeps = Keeps(mask, transition, complement: false);
            bool lod1Keeps = Keeps(mask, transition, complement: true);

            Assert.NotEqual(lod0Keeps, lod1Keeps);
        }

        [Fact]
        public void TheCascadeFilter_WeightsTheSharedFootprintAndDividesByNine()
        {
            string filter = ShaderSources.CascadeShadowFilterGlsl;
            Assert.Contains("vec4 wx = vec4(1.0 - f.x, 1.0, 1.0, f.x);", filter, StringComparison.Ordinal);
            Assert.Contains("vec4 wy = vec4(1.0 - f.y, 1.0, 1.0, f.y);", filter, StringComparison.Ordinal);
            Assert.Contains("lit += ((depth <= d) ? 1.0 : 0.0) * wx[x] * wy[y];", filter, StringComparison.Ordinal);
            Assert.Contains("return lit / 9.0;", filter, StringComparison.Ordinal);
            Assert.Contains("luv = clamp(luv, vec2(halfTexel), vec2(1.0 - halfTexel));", filter, StringComparison.Ordinal);
            Assert.Contains(filter, ShaderSources.LightingCommonGlsl, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(0.25f)]
        [InlineData(0.5f)]
        [InlineData(0.75f)]
        public void TheFootprintWeights_AreOneMinusF_One_One_F_AndSumToNine(float f)
        {
            float[] w = ShadowFilterOracle.Weights(f);
            Assert.Equal(new[] { 1f - f, 1f, 1f, f }, w);
            float total = 0f;
            foreach (float wy in w)
                foreach (float wx in w)
                    total += wx * wy;
            Assert.Equal(9f, total);
        }

        // On a texel centre the fraction is zero, the fourth texel carries no weight and the footprint is the old 3x3
        // point kernel exactly, at the column borders too. Off a centre the point kernel is a step and the oracle is
        // not, which is the staircase the bilinear filter removes.
        [Fact]
        public void ThePointFilter_MatchesTheOracleOnCentresAndMissesItOffThem()
        {
            float[] atlas = ShadowFilterOracle.Atlas();
            int centres = 0;
            var missedCascades = new bool[ShadowFilterOracle.Count];
            foreach (ShadowFilterCase c in ShadowFilterOracle.Cases())
            {
                if (c.Blend) continue;
                float point = ShadowFilterOracle.PointFilter(atlas, c.Cascade, c.Uv, c.Depth);
                float filter = ShadowFilterOracle.Filter(atlas, c.Cascade, c.Uv, c.Depth);
                float tx = c.Uv.X * ShadowFilterOracle.Resolution - 0.5f;
                float ty = c.Uv.Y * ShadowFilterOracle.Resolution - 0.5f;
                if (tx == MathF.Floor(tx) && ty == MathF.Floor(ty))
                {
                    centres++;
                    Assert.True(point == filter, $"{c.Name}: point {point} differs from the oracle {filter} on a centre.");
                }
                else if (point != filter)
                {
                    missedCascades[c.Cascade] = true;
                }
            }
            Assert.True(centres >= ShadowFilterOracle.Count * 3, $"Only {centres} centre cases.");
            Assert.All(missedCascades, Assert.True);
        }

        [Fact]
        public void TheEqualDepthCases_LandOnAnEqualStoredTexelThatCountsAsLit()
        {
            float[] atlas = ShadowFilterOracle.Atlas();
            foreach (ShadowFilterCase c in ShadowFilterOracle.Cases())
            {
                if (!c.Name.Contains("equal to stored", StringComparison.Ordinal)) continue;
                float equal = ShadowFilterOracle.Filter(atlas, c.Cascade, c.Uv, c.Depth);
                float above = ShadowFilterOracle.Filter(atlas, c.Cascade, c.Uv, MathF.BitIncrement(c.Depth));
                Assert.True(equal > above, $"{c.Name}: no weighted texel holds exactly {c.Depth}.");
            }
        }

        static float ClipZ(in Matrix4x4 mat, Vector3 p)
        {
            Vector4 lc = Vector4.Transform(new Vector4(p, 1f), mat);
            return lc.Z / lc.W;
        }

        static bool Keeps(float mask, float threshold, bool complement) =>
            complement ? mask < threshold : mask >= threshold;
    }
}
