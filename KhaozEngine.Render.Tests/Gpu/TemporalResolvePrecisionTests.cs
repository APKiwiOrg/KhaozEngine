using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE ACCUMULATION'S PRECISION, PINNED (<see cref="TemporalResolvePrecisionPolicy"/>,
    /// <see cref="ShaderSources.TemporalHalfPrecisionGlsl"/>). Half on Metal, full elsewhere. Each half program is its
    /// full twin with the other header, validates on every backend's cross-compile, and is what a Metal scene records
    /// for both entry points, the debug views and the count probe. The full programs' pinned hashes are the ones before
    /// the choice existed, which the HLSL, MSL and SPIR-V byte-equality facts hold. Device-free.
    /// </summary>
    public sealed class TemporalResolvePrecisionTests
    {
        [Theory]
        [InlineData(GpuBackendKind.MetalNative, "Half")]
        [InlineData(GpuBackendKind.VulkanNative, "Full")]
        [InlineData(GpuBackendKind.Direct3D11Native, "Full")]
        public void TheMeasuredPrecisionFollowsTheBackend(GpuBackendKind backend, string expected)
            => Assert.Equal(expected, TemporalResolvePrecisionPolicy.For(backend).ToString());

        public static TheoryData<string, string, string> Twins => new()
        {
            { "TemporalResolve", ShaderSources.TemporalResolveFrag, ShaderSources.TemporalResolveHalfFrag },
            { "TemporalAccumulate", ShaderSources.TemporalAccumulateFrag, ShaderSources.TemporalAccumulateHalfFrag },
            { "TemporalDebugView", ShaderSources.TemporalDebugFrag, ShaderSources.TemporalDebugHalfFrag },
            { "TemporalCountProbe", ShaderSources.TemporalProbeFrag, ShaderSources.TemporalProbeHalfFrag },
        };

        [Theory]
        [MemberData(nameof(Twins))]
        public void EachHalfProgramIsItsFullTwinWithTheOtherHeader(string name, string full, string half)
        {
            Assert.StartsWith("#version 450\n" + ShaderSources.TemporalFullPrecisionGlsl, full);
            Assert.Equal(full.Replace(ShaderSources.TemporalFullPrecisionGlsl, ShaderSources.TemporalHalfPrecisionGlsl),
                half);
            Assert.Contains("float16_t", ShaderSources.TemporalHalfPrecisionGlsl);
            Assert.DoesNotContain("float16", full);
            Assert.True(half.Length > full.Length, name);
        }

        [Theory]
        [MemberData(nameof(Twins))]
        public void EachHalfProgramValidates(string name, string full, string half)
        {
            Assert.NotEqual(full, half);
            ShaderValidation.ValidatePair(ShaderSources.FullscreenVert, half, name + "Half");
        }

        [Theory]
        [InlineData(GpuBackendKind.MetalNative, "Fused")]
        [InlineData(GpuBackendKind.MetalNative, "Split")]
        [InlineData(GpuBackendKind.VulkanNative, "Fused")]
        [InlineData(GpuBackendKind.VulkanNative, "Split")]
        public void AScenePipelinesEveryTemporalProgramAtItsBackendsPrecision(GpuBackendKind backend, string forced)
        {
            TemporalResolveEntry entry = Enum.Parse<TemporalResolveEntry>(forced);
            using var rig = new HeadlessSceneRig(backend);
            rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            rig.Scene.Post.Temporal.Upscale = TemporalUpscale.Performance;
            rig.Scene.DebugView = SceneDebugView.History;
            rig.Scene.TemporalResolveEntryForTests = entry;
            rig.Frame();
            rig.Scene.RequestTemporalCounts();
            rig.Frame();

            TemporalResolvePrecision precision = TemporalResolvePrecisionPolicy.For(backend);
            string[] requested = rig.Factory.GraphicsPipelines.Select(p => p.FragmentGlsl).ToArray();
            string[] wanted = [.. TemporalResolveRenderer.EntryFragments(entry, precision),
                ShaderSources.TemporalDebugFragment(precision), ShaderSources.TemporalProbeFragment(precision)];
            foreach (string fragment in wanted) Assert.Contains(fragment, requested);
            TemporalResolvePrecision other = precision == TemporalResolvePrecision.Half
                ? TemporalResolvePrecision.Full : TemporalResolvePrecision.Half;
            foreach (string fragment in new[]
            {
                ShaderSources.TemporalResolveFragment(other), ShaderSources.TemporalAccumulateFragment(other),
                ShaderSources.TemporalDebugFragment(other), ShaderSources.TemporalProbeFragment(other),
            })
            {
                Assert.DoesNotContain(fragment, requested);
            }
        }
    }
}
