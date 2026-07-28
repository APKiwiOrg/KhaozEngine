using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
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
        const string DepthBoundsGlsl = "z < 0.0 || z > 1.0";

        // The depth pass's near-plane pancake, in its two halves: the rasterized clip depth (vertex) and the stored
        // R32F value (fragment). Split deliberately - clamping the VARYING at the vertex instead would tilt the
        // interpolated depth across a triangle crossing the near plane and under-shadow.
        const string VertexPancakeGlsl = "lightClip.z = max(lightClip.z, 0.0);";
        const string FragmentPancakeGlsl = "max(vLightDepth, 0.0)";

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
        [InlineData(nameof(ShaderSources.SkinnedShadowDepthVert))]
        public void EveryShadowDepthVertex_PancakesTheRasterizedDepth(string which)
        {
            string src = which switch
            {
                nameof(ShaderSources.ShadowDepthVert) => ShaderSources.ShadowDepthVert,
                nameof(ShaderSources.ShadowDepthDissolveVert) => ShaderSources.ShadowDepthDissolveVert,
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
        public void EveryShadowDepthFragment_ClampsTheStoredDepth(string which)
        {
            string src = which switch
            {
                nameof(ShaderSources.ShadowDepthFrag) => ShaderSources.ShadowDepthFrag,
                nameof(ShaderSources.ShadowDepthDissolveFrag) => ShaderSources.ShadowDepthDissolveFrag,
                _ => ShaderSources.ShadowDepthDissolveInvertedFrag,
            };
            Assert.Contains(FragmentPancakeGlsl, src, StringComparison.Ordinal);
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

        static float ClipZ(in Matrix4x4 mat, Vector3 p)
        {
            Vector4 lc = Vector4.Transform(new Vector4(p, 1f), mat);
            return lc.Z / lc.W;
        }
    }
}
