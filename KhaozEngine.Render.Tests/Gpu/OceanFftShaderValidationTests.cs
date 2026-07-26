using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Device-free validation of the FFT ocean's two compute kernels at every resolution the producer will build
    /// them at, through <see cref="ShaderValidation.ValidateCompute"/> (SPIR-V, then HLSL / MSL / GLSL / ESSL).
    /// Plain <c>[Fact]</c>s, so a source that fails to cross-compile to one backend fails the fast lane on every
    /// push instead of surfacing only on that backend's GPU leg.
    /// <para>
    /// Both resolutions are covered because the resolution is SUBSTITUTED INTO THE SOURCE - compute specialization
    /// constants are broken below the seam (#312) - so 128 and 256 are genuinely different shaders, and a
    /// substitution that only happens to be valid at one of them is exactly the failure this catches.
    /// </para>
    /// </summary>
    public sealed class OceanFftShaderValidationTests
    {
        [Theory]
        [InlineData(32)]
        [InlineData(128)]
        [InlineData(256)]
        public void RowPass(int n)
            => ShaderValidation.ValidateCompute(OceanComputeShaders.RowPass(n), $"OceanRowPass{n}");

        [Theory]
        [InlineData(32)]
        [InlineData(128)]
        [InlineData(256)]
        public void ColumnPass(int n)
            => ShaderValidation.ValidateCompute(OceanComputeShaders.ColumnPass(n), $"OceanColumnPass{n}");

        [Fact]
        public void TheWorkgroupSizeIsHalfTheTransformLength()
        {
            Assert.Equal(64u, OceanComputeShaders.GroupSize(128));
            Assert.Equal(128u, OceanComputeShaders.GroupSize(256));
        }

        [Fact]
        public void StageCountIsLog2OfTheResolution()
        {
            Assert.Equal(7, OceanComputeShaders.Stages(128));
            Assert.Equal(8, OceanComputeShaders.Stages(256));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(96)]        // not a power of two
        [InlineData(512)]       // past the supported ceiling
        public void AnUnsupportedResolutionIsRejectedRatherThanMiscompiled(int n)
            => Assert.Throws<ArgumentOutOfRangeException>(() => OceanComputeShaders.RowPass(n));

        /// <summary>
        /// The regression case, reconstructed from the source that actually shipped it by moving ONE line. The row
        /// pass reads <c>Timing</c> (binding 0) at the top of its spectrum evaluation; drop that read below the
        /// <c>H0</c> read (binding 1) and the two swap Metal slots, because Metal indices follow first-reference
        /// order while the resource layout is counted in binding order.
        /// <para>
        /// What that cost, before the guard existed: the kernel divided by a tile size it had read out of the
        /// spectrum buffer, got 0, and produced a NaN surface - on Metal only, with Vulkan and Direct3D11 correct,
        /// and with nothing in the GLSL that looks wrong. Keeping the real source here rather than a synthetic
        /// stand-in is deliberate; this exact shape is the one that cost the afternoon.
        /// </para>
        /// </summary>
        [Fact]
        public void TheRowPassIsRejectedIfItsUniformReadDropsBelowItsSpectrumRead()
        {
            const string uniformFirst = "    float depth = Timing.w;\n    vec4 h = H0[";
            string good = OceanComputeShaders.RowPass(32);
            Assert.Contains(uniformFirst, good);

            string broken = good
                .Replace(uniformFirst, "    vec4 h = H0[")
                .Replace("    float dk = KE_TWO_PI / tile;", "    float depth = Timing.w;\n    float dk = KE_TWO_PI / tile;");
            Assert.NotEqual(good, broken);

            var ex = Assert.Throws<ShaderValidationException>(
                () => ShaderValidation.ValidateCompute(broken, "RowPassWithTheUniformReadMovedDown"));
            Assert.Contains("RowPassWithTheUniformReadMovedDown", ex.Message);
            Assert.Contains("binding order", ex.Message);
        }
    }
}
