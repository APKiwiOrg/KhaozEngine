using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>Issue #374: RenderInternal's per-frame <c>ApplyAlphaCutoffs</c> call built a new closure every call,
    /// capturing <c>this</c> (for <c>_slots</c>) and the <c>_meshes</c> list, in a path documented allocation-free.
    /// The cutoff lookup is now a delegate bound once, at construction, into <c>_alphaCutoffLookup</c>, so the
    /// per-frame wrapper reuses it instead of rebuilding it.
    /// <para>Measured through <see cref="Scene3D.ApplyAlphaCutoffsForTest"/>, the exact wrapper RenderInternal
    /// calls, rather than a full render: the closure was allocated at the call site regardless of whether the run
    /// list is empty (lambda construction is eager), so an empty run list is enough to prove the fix without
    /// needing a full render or loaded meshes. Needs a live Scene3D (GPU-backed construction), so this is a
    /// GpuFact, but no frame is actually rendered.</para></summary>
    [Collection("AllocSensitive")]   // a zero-allocation reading measures its neighbours too (#264)
    public sealed class Scene3DAlphaCutoffAllocationTests
    {
        [GpuFact]
        public void ApplyAlphaCutoffs_PerFrameCall_AllocatesNothing()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var f = gd.Factory;

            const int W = 64, H = 48;
            using IGpuTexture finalTex = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer finalFB = f.CreateFramebuffer(null, finalTex);

            using var scene = new Scene3D(gd, finalFB.Outputs);

            var instanceData = new List<ModelRenderer.InstanceData>();
            var runs = new List<Scene3D.MeshRun>();

            for (int i = 0; i < 4; i++) scene.ApplyAlphaCutoffsForTest(instanceData, runs);   // warm-up

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20; i++) scene.ApplyAlphaCutoffsForTest(instanceData, runs);
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0L, after - before);
        }
    }
}
