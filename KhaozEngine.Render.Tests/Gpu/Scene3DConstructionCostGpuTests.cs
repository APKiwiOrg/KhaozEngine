using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D;
using Xunit;

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
    /// Enlisted in <c>SpirvCompileCacheSerial</c> because it reads a process-global counter every other GPU class
    /// in this assembly also moves.
    /// </para>
    /// </summary>
    [Collection("SpirvCompileCacheSerial")]
    public sealed class Scene3DConstructionCostGpuTests
    {
        const uint W = 64, H = 64;

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

            AssertMemoDidItsJob(before, after);
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
            using (GpuDeviceContext second = GpuDeviceContext.CreateHeadless())
            {
                BuildOneScene(second.GpuDevice);
            }

            AssertMemoDidItsJob(before, SpirvCompileCache.Shared.CompileCount);
        }

        /// <summary>
        /// The memo compiled nothing across that span, or, when the kill switch is set, it compiled the whole
        /// scene's worth again. Both are a claim, and the second one is what keeps `KE_SPIRV_CACHE=off` from
        /// being a variable nothing has ever checked does anything.
        /// </summary>
        static void AssertMemoDidItsJob(long before, long after)
        {
            if (SpirvCompileCache.Shared.Enabled)
            {
                Assert.Equal(before, after);
                return;
            }

            Assert.True(after > before,
                $"{SpirvCompileCache.DisableVariable} is set, so this run asked for no memo at all, and the second "
                + "scene should have compiled its sources from scratch. It compiled none, which means something "
                + "else is caching them and the cacheless dispatch is not cacheless.");
        }

        static void BuildOneScene(IGpuDevice gd)
        {
            using IGpuTexture target = gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer framebuffer = gd.Factory.CreateFramebuffer(null, target);
            using var scene = new Scene3D(gd, framebuffer.Outputs, null);
        }
    }
}
