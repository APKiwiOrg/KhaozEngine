using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>The GLSL carries the limiter, noise term and explicit-LOD taps of its mirror,
    /// TemporalSharpenMath.</summary>
    public sealed class TemporalSharpenShaderContractTests
    {
        [Fact]
        public void TheShaderCarriesTheMirrorsLimiterAndNoiseTerm()
        {
            string src = ShaderSources.TemporalSharpenFrag;
            Assert.Equal(0.1875f, TemporalSharpenMath.Limit);
            Assert.Contains("const float RCAS_LIMIT = 0.1875;", src);
            Assert.Contains("vec3 hitMin = min(mn4, e) / max(4.0 * mx4, vec3(1e-5));", src);
            Assert.Contains("vec3 hitMax = (vec3(1.0) - max(mx4, e)) / min(4.0 * mn4 - vec3(4.0), vec3(-1e-5));", src);
            Assert.Contains("nz = 1.0 - 0.5 * nz;", src);
            // A ring channel at 0 or 1 on all four taps sets no bound on that side, as in the mirror.
            Assert.Equal(0.25f, TemporalSharpenMath.NoBound);
            Assert.Contains("const float RCAS_NO_BOUND = 0.25;", src);
            Assert.Contains("hitMin = mix(hitMin, vec3(RCAS_NO_BOUND), equal(mx4, vec3(0.0)));", src);
            Assert.Contains("hitMax = mix(hitMax, vec3(-RCAS_NO_BOUND), equal(mn4, vec3(1.0)));", src);
            Assert.Contains("oColor = vec4(clamp(c, 0.0, 1.0), centre.a);", src);
            // Only the base level of a mid-chain post target is current (the FXAA note), so every tap is explicit.
            Assert.DoesNotContain("texture(sampler2D", src);
        }
    }
}
