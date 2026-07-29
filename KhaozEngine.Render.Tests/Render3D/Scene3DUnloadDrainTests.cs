using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    // Regression coverage for the lavapipe mode-2 crash documented alongside the device-lifecycle gate
    // (GpuDeviceContext remarks): Scene3D used to Dispose() a mid-life resource immediately on Unload*, racing any
    // upload or draw still queued on the device (Mesa lavapipe executes queued work on its own thread and segfaults
    // on a resource freed out from under it). These tests pin WHERE the protection lands via a spy device (the
    // ordering inside a method is not observable through the device seam). The texture path still drains inside the
    // unload call. The mesh path retires, and how the retirement is proved safe now depends on the backend: a
    // device that signals a fence on GPU completion polls that fence and never drains, and one that does not keeps
    // the frame-count delay behind a single WaitForIdle. Both are covered below.
    // Scene3DTextureUnloadTests is the behavioural (slot-freeing) coverage.
    public sealed class Scene3DUnloadDrainTests
    {
        static readonly byte[] Pixel = new byte[] { 255, 255, 255, 255 };   // 1x1 RGBA8

        static (GpuDeviceContext Gpu, SpyGpuDevice Spy, Scene3D Scene, IGpuTexture Target, IGpuFramebuffer Fb) MakeScene(
            bool suppressFences = false)
        {
            GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            var spy = new SpyGpuDevice(gpu.GpuDevice, suppressFences);
            var f = spy.Factory;
            IGpuTexture tex = f.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            IGpuFramebuffer fb = f.CreateFramebuffer(null, tex);
            var scene = new Scene3D(spy, fb.Outputs);
            return (gpu, spy, scene, tex, fb);
        }

        [GpuFact]
        public void UnloadTexture_DrainsTheDevice()
        {
            var (gpu, spy, scene, tex, fb) = MakeScene();
            using (gpu) using (scene) using (tex) using (fb)
            {
                var h = scene.LoadTexture(Pixel, 1, 1);   // 1-mip path: returns with its upload still queued
                int before = spy.WaitForIdleCalls;

                scene.UnloadTexture(h);

                Assert.True(spy.WaitForIdleCalls > before,
                    "UnloadTexture must drain the device (WaitForIdle) before disposing the texture");
            }
        }

        static GltfMesh Triangle()
        {
            var verts = new[]
            {
                new ModelVertex(Vector3.Zero, Vector3.UnitZ, Vector4.One),
                new ModelVertex(Vector3.UnitX, Vector3.UnitZ, Vector4.One),
                new ModelVertex(Vector3.UnitY, Vector3.UnitZ, Vector4.One),
            };
            return new GltfMesh(verts, new uint[] { 0, 1, 2 });
        }

        // The mesh path moved off the per-unload drain: terrain streaming unloads meshes constantly (every chunk
        // leaving the ring, every HLOD layer with it), and a full-device drain per mesh stalled the frame thread
        // during ordinary movement. Buffers are retired instead and freed behind ONE drain at a later frame
        // boundary, so the lavapipe rule (never destroy while queued work may reference it) still holds.
        [GpuFact]
        public void UnloadMesh_DoesNotDrainTheDevice()
        {
            var (gpu, spy, scene, tex, fb) = MakeScene();
            using (gpu) using (scene) using (tex) using (fb)
            {
                MeshHandle h = scene.LoadMesh(Triangle());
                int before = spy.WaitForIdleCalls;

                scene.UnloadMesh(h);

                Assert.Equal(before, spy.WaitForIdleCalls);
            }
        }

        // The fallback policy, forced on by suppressing the fence capability so it is covered on a Metal box too:
        // no GPU-completion fence means a batch waits out the frame delay and dies behind exactly one drain.
        [GpuFact]
        public void RetiredMeshBuffers_OnAnUnfencedDevice_AreFreedBehindOneDrainAtALaterFrame()
        {
            var (gpu, spy, scene, tex, fb) = MakeScene(suppressFences: true);
            using (gpu) using (scene) using (tex) using (fb)
            {
                for (int i = 0; i < 8; i++) scene.UnloadMesh(scene.LoadMesh(Triangle()));
                int before = spy.WaitForIdleCalls;

                for (int i = 0; i < RetiredResourcePool.DefaultFrameDelay; i++) scene.Begin();

                Assert.Equal(before + 1, spy.WaitForIdleCalls);   // one drain for the whole batch, not one per mesh
                Assert.Equal(0, scene.RetiredResourceCount);

                scene.Begin();
                Assert.Equal(before + 1, spy.WaitForIdleCalls);   // nothing left pending, so no further drains
            }
        }

        // The shipped policy on Metal and Vulkan: the batch is sealed behind an empty fenced submission and freed
        // by polling that fence, so the frame boundary never stalls the CPU on the GPU.
        [GpuFact]
        public void RetiredMeshBuffers_OnAFencedDevice_AreFreedWithoutEverDraining()
        {
            var (gpu, spy, scene, tex, fb) = MakeScene();
            using (gpu) using (scene) using (tex) using (fb)
            {
                Assert.True(spy.Capabilities.SupportsCompletionFences,
                    "this leg is meaningless on a device without GPU-completion fences");

                for (int i = 0; i < 8; i++) scene.UnloadMesh(scene.LoadMesh(Triangle()));
                int drainsBefore = spy.WaitForIdleCalls;
                int fencedBefore = spy.FencedSubmitCalls;

                Assert.True(scene.RetiredResourceCount >= 16,      // at least a vertex + index buffer per mesh
                    $"expected the eight unloaded meshes to be held, got {scene.RetiredResourceCount}");

                // Poll to ripeness. The batch is sealed on the first boundary and freed on the first boundary its
                // fence has signaled, which may well be that same one: an empty submission behind an idle queue
                // completes in microseconds. WHICH boundary it lands on is not the contract and is not asserted
                // here (RetiredResourcePoolTests drives a fence by hand for that). The bound is generous only so a
                // loaded machine cannot flake it.
                for (int i = 0; i < 200 && scene.RetiredResourceCount > 0; i++)
                {
                    scene.Begin();
                    if (scene.RetiredResourceCount > 0) System.Threading.Thread.Sleep(1);
                }

                Assert.Equal(0, scene.RetiredResourceCount);
                Assert.Equal(fencedBefore + 1, spy.FencedSubmitCalls);   // one fence sealed the whole batch
                Assert.Equal(drainsBefore, spy.WaitForIdleCalls);        // the cycle stalled the CPU exactly never
            }
        }
    }
}
