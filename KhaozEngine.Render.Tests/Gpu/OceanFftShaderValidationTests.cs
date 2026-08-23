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
        public void TheShadersAgreeWithOceanSpectrumOnTheCascadeCeiling()
        {
            // Three literals mirror OceanSpectrum.MaxCascades: KE_MAX_CASCADES in both water stages, and the
            // Cascade[3] uniform array in both kernels. Raising the ceiling is a shader change, not a knob, and
            // this is what says so out loud when someone tries.
            Assert.Equal(3, OceanSpectrum.MaxCascades);
            Assert.Contains($"const int KE_MAX_CASCADES = {OceanSpectrum.MaxCascades};", ShaderSources.WaterVert);
            Assert.Contains($"const int KE_MAX_CASCADES = {OceanSpectrum.MaxCascades};", ShaderSources.WaterFrag);
            Assert.Contains($"vec4 Cascade[{OceanSpectrum.MaxCascades}];", OceanComputeShaders.RowPass(32));
            Assert.Contains($"vec4 Cascade[{OceanSpectrum.MaxCascades}];", OceanComputeShaders.ColumnPass(32));
        }

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
        /// pass reads <c>Timing</c> (binding 0) at the top of its spectrum evaluation. Dropping that read below
        /// the <c>H0</c> read (binding 1) used to swap their Metal slots, because Metal indices followed
        /// first-reference order while the resource layout was counted in binding order.
        /// <para>
        /// What that cost, before the guard existed: the kernel divided by a tile size it had read out of the
        /// spectrum buffer, got 0, and produced a NaN surface, on Metal only, with Vulkan and Direct3D11 correct,
        /// and with nothing in the GLSL that looks wrong. Keeping the real source here rather than a synthetic
        /// stand-in is deliberate, because this exact shape is the one that cost the afternoon.
        /// </para>
        /// <para>
        /// AND 18.0.0 ENDED IT AT THE SOURCE. Row 10 (#693) made the engine AUTHOR each resource's Metal index in
        /// ascending <c>(set, binding)</c>, so first-reference order decides nothing and this edit is now
        /// harmless. The row is kept, inverted, as the record of which class of afternoon stopped existing.
        /// <c>MslBindingOrderGuardTests</c> carries the same inversion for the graphics pair, and
        /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/604">#604</see> is the change that deletes
        /// the now-inert check itself.
        /// </para>
        /// </summary>
        [Fact]
        public void TheRowPassWithItsUniformReadMovedDown_IsAcceptedBecauseTheIndexIsAuthored()
        {
            const string uniformFirst = "    float depth = Timing.w;\n    vec4 h = H0[";

            // NORMALIZED to LF before any multi-line match, and then run BOTH ways. The kernel is a C# verbatim
            // string literal, so its line endings are whatever the checked-out .cs has, and `.gitattributes`
            // normalizes only the golden grids - so on a Windows checkout (autocrlf) the source carries CRLF and a
            // "\n" marker silently never matches. That is a test-harness assumption rather than a shader property
            // (glslang is line-ending agnostic), and it cost one red Direct3D11 leg. Running the CRLF form too
            // means the Windows case is now proved on every platform instead of only on Windows.
            string lf = OceanComputeShaders.RowPass(32).Replace("\r\n", "\n");
            Assert.Contains(uniformFirst, lf);

            foreach (string source in new[] { lf, lf.Replace("\n", "\r\n") })
            {
                string broken = source
                    .Replace(uniformFirst.Replace("\n", NewlineOf(source)), "    vec4 h = H0[")
                    .Replace("    float dk = KE_TWO_PI / tile;",
                        "    float depth = Timing.w;" + NewlineOf(source) + "    float dk = KE_TWO_PI / tile;");
                Assert.NotEqual(source, broken);

                ShaderValidation.ValidateCompute(broken, "RowPassWithTheUniformReadMovedDown");

                // The unmodified source must pass too, so a validation that had started accepting nothing would
                // still be caught somewhere in this file rather than reading as a clean inversion.
                ShaderValidation.ValidateCompute(source, "RowPassUnmodified");
            }
        }

        static string NewlineOf(string source) => source.Contains("\r\n") ? "\r\n" : "\n";
    }
}
