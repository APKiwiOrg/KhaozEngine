using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE PIN ON <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/640">#640</see>: a second
    /// <see cref="Scene3D"/> compiles no shader source the first one already compiled, on the same device or on a
    /// fresh one.
    /// <para>
    /// <b>WHAT WENT WRONG AND WHY A COUNTER PINS IT RATHER THAN A CLOCK.</b> A scene's constructor asks its device
    /// for 34 shader sets, which is 68 stage compiles across 48 distinct sources, and every one of them was a fresh
    /// glslang run against a <c>const string</c> that had not changed. Measured on Metal, that was 2515 ms of a
    /// 2560 ms constructor, and it did not fall on the second scene, the tenth, or the first scene on a second
    /// device. The regression this test exists to catch is a caller or a backend stepping back around
    /// <see cref="SpirvCompileCache"/>, and that is a fact about how many times the compiler ran rather than about
    /// how long anything took, so a compile count is what is asserted. A duration would have to be a threshold,
    /// and a threshold on a hosted software rasterizer is a flake with a schedule.
    /// </para>
    /// <para>
    /// <b>IT HOLDS ON EVERY BACKEND, INCLUDING THE ONES THAT NEVER REACH THE FRONT END.</b> The incumbent Veldrid
    /// device and the native Vulkan and Direct3D 11 backends compile through
    /// <see cref="SpirvCompileCache.Shared"/>, so a repeat is a hit and the counter stands still. The native Metal
    /// backend serves a warm run out of its MSL disk cache and never asks the front end at all, so the counter
    /// stands still there for a different reason. Zero either way is the claim, which is what makes this one test
    /// rather than a per-backend family.
    /// </para>
    /// <para>
    /// <b>UNDER <c>KE_SPIRV_CACHE=off</c> BOTH CASES ASSERT THE OPPOSITE</b> rather than skipping. That switch is
    /// on `cross-platform-gpu.yml`'s cacheless dispatch, which exists to rule the caches out of a flake
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/614">#614</see>), so a run that asked for no
    /// memo must not go red for getting none. A skip would leave the kill switch itself unexercised on the one
    /// run that uses it, and a switch nothing checks is how a cacheless run ends up quietly caching.
    /// </para>
    /// <para>
    /// <b>WITH ONE LEGITIMATE ZERO, WHICH ONLY THE KILL SWITCH'S OWN A/B EVER REACHES.</b> Two backends answer a
    /// repeat off DISK before the front end is reached at all, so on those the counter stands still whatever
    /// <c>KE_SPIRV_CACHE</c> says: <c>MetalShaderBuild.Pair</c> consults <c>MetalMslCache</c> ahead of
    /// <c>SpirvFrontEnd.ToSpirv</c>, and <c>D3D11ShaderBuild.Pair</c> consults <c>D3D11DxbcCache</c> ahead of the
    /// cross-compile that calls it. Skipping BOTH halves is the entire reason those caches exist. The cacheless
    /// dispatch never meets this, because it sets every cache variable together, but the switch's own documented
    /// A/B does, and it used to get two red rows accusing a cache that does not exist. So a zero delta is accepted
    /// when the running backend's disk cache is on, and asserted against when it is not.
    /// </para>
    /// <para>
    /// Enlisted in <c>SpirvCompileCacheSerial</c> because it reads a process-global counter every other GPU class
    /// in this assembly also moves.
    /// </para>
    /// </summary>
    [Collection("SpirvCompileCacheSerial")]
    public sealed class Scene3DConstructionCostGpuTests
    {
        const uint W = 64, H = 64;

        readonly ITestOutputHelper _output;

        public Scene3DConstructionCostGpuTests(ITestOutputHelper output) => _output = output;

        [GpuFact]
        public void A_second_scene_on_the_same_device_compiles_nothing_new()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            using IGpuTexture target = gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer framebuffer = gd.Factory.CreateFramebuffer(null, target);

            using (var warm = new Scene3D(gd, framebuffer.Outputs, null)) { }

            long before = SpirvCompileCache.Shared.CompileCount;
            using (var second = new Scene3D(gd, framebuffer.Outputs, null)) { }
            long after = SpirvCompileCache.Shared.CompileCount;

            AssertMemoDidItsJob(gpu.Backend, before, after);
        }

        [GpuFact]
        public void A_scene_on_a_FRESH_device_compiles_nothing_new_either()
        {
            // The shape the GPU suite actually has: Render3DSnapshot.Capture builds a device AND a scene per call,
            // so a memo that lived on the device would leave every capture in the assembly paying the full compile.
            // SPIR-V is device-free, which is what lets the memo outlive the device that first asked for it.
            using (GpuDeviceContext first = GpuDeviceContext.CreateHeadless())
            {
                BuildOneScene(first.GpuDevice);
            }

            long before = SpirvCompileCache.Shared.CompileCount;
            GpuBackendKind backend;
            using (GpuDeviceContext second = GpuDeviceContext.CreateHeadless())
            {
                backend = second.Backend;
                BuildOneScene(second.GpuDevice);
            }

            AssertMemoDidItsJob(backend, before, SpirvCompileCache.Shared.CompileCount);
        }

        /// <summary>
        /// The memo compiled nothing across that span, or, when the kill switch is set, it compiled the whole
        /// scene's worth again. Both are a claim, and the second one is what keeps `KE_SPIRV_CACHE=off` from
        /// being a variable nothing has ever checked does anything.
        /// <para>
        /// EXCEPT WHERE THE BACKEND'S OWN DISK CACHE ANSWERED FIRST, in which case the front end was never asked
        /// and this counter cannot move however the kill switch is set. That is a real, correct zero rather than a
        /// caching memo, so it is accepted and REPORTED, with the variable that makes the recompile observable
        /// named in the line. The assertion is kept for every backend that has nothing in front of the front end,
        /// which is where a stray memo would actually be visible.
        /// </para>
        /// </summary>
        void AssertMemoDidItsJob(GpuBackendKind backend, long before, long after)
        {
            if (SpirvCompileCache.Shared.Enabled)
            {
                Assert.Equal(before, after);
                return;
            }

            if (after > before) return;

            if (ShaderDiskCacheAheadOfTheFrontEnd(backend) is { } diskCacheVariable)
            {
                _output.WriteLine(
                    $"{SpirvCompileCache.DisableVariable} is off and the front end compiled nothing, which is "
                    + $"correct on {backend}: it answers a repeat out of its own shader disk cache before the "
                    + $"front end is reached, so no memo of this one's was ever consulted. Set "
                    + $"{diskCacheVariable}=off alongside it to make the recompile observable.");
                return;
            }

            Assert.Fail(
                $"{SpirvCompileCache.DisableVariable} is set, so this run asked for no memo at all, and the second "
                + "scene should have compiled its sources from scratch. It compiled none, and this backend "
                + $"({backend}) has no shader disk cache sitting in front of the front end to account for it, so "
                + "something else is caching them and the cacheless dispatch is not cacheless.");
        }

        /// <summary>
        /// The variable that switches off the running backend's OWN shader disk cache, when that cache is enabled
        /// AND sits in front of the front end, and null when nothing on this backend can answer a repeat without
        /// asking glslang.
        /// <para>
        /// EACH CACHE'S OWN <c>Resolve</c> DECIDES, rather than a second copy of the disable-word list. Both are
        /// documented as the pure decision with no directory sweep, which is exactly what a caller wanting the
        /// answer and no side effect needs. The native Vulkan backend is deliberately absent: its only disk cache
        /// holds PIPELINES, and its shader path compiles every module through <c>SpirvFrontEnd</c> first because
        /// the module hash is what its per-device dedup keys on.
        /// </para>
        /// </summary>
        static string? ShaderDiskCacheAheadOfTheFrontEnd(GpuBackendKind backend) => backend switch
        {
            GpuBackendKind.MetalNative when MetalMslCache.Resolve(
                Environment.GetEnvironmentVariable(MetalMslCache.EnvVarName)) is not null
                => MetalMslCache.EnvVarName,
            GpuBackendKind.Direct3D11Native when D3D11DxbcCache.Resolve(
                Environment.GetEnvironmentVariable(D3D11DxbcCache.EnvVarName)) is not null
                => D3D11DxbcCache.EnvVarName,
            _ => null,
        };

        static void BuildOneScene(IGpuDevice gd)
        {
            using IGpuTexture target = gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer framebuffer = gd.Factory.CreateFramebuffer(null, target);
            using var scene = new Scene3D(gd, framebuffer.Outputs, null);
        }
    }
}
