using System;
using KhaozEngine.Gpu;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.MapEdit;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;
using SampleDocs = KhaozEngine.Tests.MapDoc.MapDocumentFileTests;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>Covers the dispose-order guard on <see cref="Scene3D.UnloadSplatMaterial"/>: a
    /// <see cref="ViewportWorld"/> owns its splat material (built with the sink's <c>ownsMaterial: false</c>, see
    /// <see cref="ViewportWorld"/>'s own teardown comment) and frees it from its own <see cref="ViewportWorld.Dispose"/>
    /// via <c>TeardownKitMeshes</c>. <see cref="RenderService"/>'s render path always
    /// disposes the owning <see cref="Scene3D"/> first and deliberately leaves the world undisposed (its comment
    /// block explains why), but nothing stops a caller from disposing the world explicitly afterwards - and that
    /// path used to throw <see cref="ArgumentOutOfRangeException"/> because <see cref="Scene3D.Dispose"/> clears the
    /// backing splat-material list. This is a GpuFact (needs a real headless device, KE_GPU_TESTS=1/probe on the
    /// dev Mac's Metal) because <see cref="ViewportWorld.Build"/> touches the GPU.</summary>
    public sealed class ViewportWorldDisposeOrderGpuTests
    {
        static (Scene3D scene, GpuDeviceContext gpu, IGpuTexture tex, IGpuFramebuffer fb) NewScene()
        {
            var gpu = GpuDeviceContext.CreateHeadless();
            var f = gpu.GpuDevice.Factory;
            IGpuTexture tex = f.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            IGpuFramebuffer fb = f.CreateFramebuffer(null, tex);
            var scene = new Scene3D(gpu.GpuDevice, fb.Outputs);
            return (scene, gpu, tex, fb);
        }

        [GpuFact]
        public void WorldDispose_AfterOwningSceneAlreadyDisposed_IsSafeNoOp()
        {
            (Scene3D scene, GpuDeviceContext gpu, IGpuTexture tex, IGpuFramebuffer fb) = NewScene();
            using (gpu) using (tex) using (fb)
            {
                var world = new ViewportWorld(scene, Array.Empty<string>());
                world.Build(SampleDocs.SampleDoc(), MapDocRegistry.CreateDefault());

                // The render pipeline's real order (Render3DSnapshot.Capture / RenderService.CaptureToPng): the
                // Scene3D that owns every GPU resource, including the world's splat material and its streamed
                // terrain-chunk meshes, is disposed first.
                scene.Dispose();

                // Disposing the world afterwards runs its teardown (streamer/sink flush, then UnloadSplatMaterial)
                // through the already-disposed scene. Before the engine guard, UnloadSplatMaterial indexed into
                // Scene3D's now-cleared splat-material list and threw ArgumentOutOfRangeException. It must be a
                // silent no-op now, matching the sibling unloads' post-dispose behaviour.
                world.Dispose();
            }
        }
    }
}
