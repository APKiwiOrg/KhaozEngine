using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render2D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE SEVEN LATENT SITES OF <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/424">#424</see>, each
    /// driven from inside an open recording and each asserted to refuse by name. They were found by the #423
    /// root-cause investigation as SIBLINGS of the ocean prime: every one opens, submits and drains a command list
    /// of its own, which is safe exactly as long as nobody calls it while a frame is recording, and silently
    /// device-corrupting on the Direct3D11 immediate path the moment somebody does.
    /// <para>
    /// The point of testing them together is that the fix is one seam and not seven patches. Each test below is the
    /// same two lines against a different entry point, which is only possible because they all open through
    /// <see cref="GpuRecording"/> now.
    /// </para>
    /// <para>
    /// Device-free, so a plain <c>dotnet test</c> runs the lot on any machine. The fault they stand for reproduces
    /// on a backend the dev machine does not have.
    /// </para>
    /// </summary>
    public sealed class NestedRecordingSiteTests
    {
        const int W = 64, H = 48;

        static IGpuFramebuffer NewTarget(IGpuResourceFactory f)
        {
            IGpuTexture tex = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            return f.CreateFramebuffer(null, tex);
        }

        // A frame's list, open and holding the device, exactly as FramePhases holds it for the duration of a
        // windowed frame's record phase.
        static GpuRecordingScope OpenFrame(OpenListTrackingGpuDevice device, IGpuCommandList frameList) =>
            GpuRecording.Open(device, frameList, "the window's frame list");

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

        static void AssertRefused(Action call, string attemptedContains)
        {
            var ex = Assert.Throws<GpuNestedRecordingException>(call);
            Assert.Equal("the window's frame list", ex.Owner);
            Assert.Contains(attemptedContains, ex.Attempted);
        }

        // ---- Site 1 and 2: the two mip-generating loads on Scene3D ----

        [Fact]
        public void Scene3D_LoadTexture_refuses_a_mipped_load_inside_a_recording()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuFramebuffer fb = NewTarget(device.Factory);
            using var scene = new Scene3D(device, fb.Outputs);
            using IGpuCommandList frameList = device.Factory.CreateCommandList();

            using (OpenFrame(device, frameList))
                AssertRefused(() => scene.LoadTexture(new byte[4 * 4 * 4], 4, 4), "Scene3D.LoadTexture");
        }

        /// <summary>
        /// The single-level load opens no command list at all, so it stays legal mid-frame. Worth pinning: a guard
        /// that refused every texture load would be a worse rule than the one it replaced, and this is the line
        /// between the two.
        /// </summary>
        [Fact]
        public void Scene3D_LoadTexture_allows_an_unmipped_load_inside_a_recording()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuFramebuffer fb = NewTarget(device.Factory);
            using var scene = new Scene3D(device, fb.Outputs);
            using IGpuCommandList frameList = device.Factory.CreateCommandList();

            using (OpenFrame(device, frameList))
                scene.LoadTexture(new byte[4 * 4 * 4], 4, 4, TextureMipPolicy.None);

            Assert.Equal(1, device.Begins);          // the frame's list, and nothing else
            Assert.Equal(1, device.PeakOpenLists);
        }

        [Fact]
        public void Scene3D_LoadSplatMaterial_refuses_inside_a_recording()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuFramebuffer fb = NewTarget(device.Factory);
            using var scene = new Scene3D(device, fb.Outputs);
            using IGpuCommandList frameList = device.Factory.CreateCommandList();

            var layers = new List<SplatLayerImage>();
            for (int i = 0; i < SplatMaterialConfig.LayerCount; i++)
                layers.Add(new SplatLayerImage
                {
                    AlbedoRgba = new byte[4 * 4 * 4],
                    NormalRgba = new byte[4 * 4 * 4],
                });

            using (OpenFrame(device, frameList))
                AssertRefused(() => scene.LoadSplatMaterial(4, 4, layers), "Scene3D.LoadSplatMaterial");
        }

        // ---- Site 3: the shadow-map readback ----

        [Fact]
        public void Scene3D_DebugReadShadowMap_refuses_inside_a_recording()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuFramebuffer fb = NewTarget(device.Factory);
            using var scene = new Scene3D(device, fb.Outputs);
            using IGpuCommandList frameList = device.Factory.CreateCommandList();

            using (OpenFrame(device, frameList))
                AssertRefused(() => scene.DebugReadShadowMap(out _, out _), "Scene3D.DebugReadShadowMap");
        }

        // ---- Site 4: all three GpuReadback entry points ----

        [Fact]
        public void GpuReadback_refuses_every_entry_point_inside_a_recording()
        {
            using var device = new OpenListTrackingGpuDevice();
            var f = device.Factory;
            using IGpuTexture src = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuBuffer buffer = f.CreateBuffer(new GpuBufferDescription(64, GpuBufferUsage.StructuredBufferReadWrite));
            using IGpuCommandList frameList = f.CreateCommandList();

            using (OpenFrame(device, frameList))
            {
                AssertRefused(() => GpuReadback.ToRgba(device, src, W, H), "GpuReadback.ToRgba");
                AssertRefused(() => GpuReadback.ToRgbaMip(device, src, 0, 0, W, H), "GpuReadback.ToRgbaMip");
                AssertRefused(() => GpuReadback.ReadBuffer<float>(device, buffer, 16), "GpuReadback.ReadBuffer");
            }
        }

        // ---- Site 5: the offscreen 2D captures ----

        [Fact]
        public void Render2D_offscreen_captures_refuse_inside_a_recording()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuCommandList frameList = device.Factory.CreateCommandList();

            using (OpenFrame(device, frameList))
            {
                AssertRefused(
                    () => Render2DCore.RenderToTexture(device, W, H, Color.Black, _ => { }),
                    "Render2DSurface.CaptureToTexture");
                AssertRefused(
                    () => Render2DCore.RenderToRgba(device, W, H, Color.Black, _ => { }),
                    "Render2DSurface.CaptureToRgba");
            }
        }

        // ---- Site 6: the live 3D preview ----

        [Fact]
        public void Render3DPreview_Capture_refuses_inside_a_recording()
        {
            using var device = new OpenListTrackingGpuDevice();
            using var preview = new Render3DPreview(device, W, H);
            using IGpuCommandList frameList = device.Factory.CreateCommandList();

            using (OpenFrame(device, frameList))
                AssertRefused(() => preview.Capture(_ => { }), "Render3DPreview.Capture");
        }

        // ---- Site 7: the retire barrier, which submits from inside Scene3D.Begin ----

        /// <summary>
        /// The barrier is the site with the longest fuse: it only exists on a device that reports GPU completion
        /// fences, so on the Veldrid Direct3D11 leg it is unreachable and always was. It goes live the day a
        /// Direct3D11 backend issues real fences, which the engine's own native one now does. Driven here on a
        /// device that reports fences, so the shape is proved before the hardware makes it matter.
        /// </summary>
        [Fact]
        public void The_retire_barrier_refuses_when_the_scene_begins_inside_a_recording()
        {
            using var device = new OpenListTrackingGpuDevice(completionFences: true);
            using IGpuFramebuffer fb = NewTarget(device.Factory);
            using var scene = new Scene3D(device, fb.Outputs);
            using IGpuCommandList frameList = device.Factory.CreateCommandList();

            // A retired mesh, so the pool has something to seal and the barrier actually fires on the next Begin.
            scene.UnloadMesh(scene.LoadMesh(Triangle()));
            scene.Begin();                       // seals the batch and submits its fence, outside any recording
            scene.UnloadMesh(scene.LoadMesh(Triangle()));

            using (OpenFrame(device, frameList))
                AssertRefused(scene.Begin, "GpuRetireBarrier.Submit");
        }

        // ---- The refusal frees what the call already built (the fix round for #424) ----
        //
        // Four of the seven sites open the recording AFTER the expensive allocation, so before the fix round each
        // refusal threw past a GPU resource nothing owned. That is not a cosmetic leak: the refusal exists to be
        // RECOVERABLE (catch it, move the call into the pre-record phase, carry on), and a host that retries a
        // streaming load every frame leaked one texture per attempt. Each test below refuses three times and
        // asserts the count came back, so a per-attempt leak cannot hide inside a one-off.

        const int Attempts = 3;

        [Fact]
        public void Scene3D_LoadTexture_frees_the_texture_it_built_when_it_is_refused()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuFramebuffer fb = NewTarget(device.Factory);
            using var scene = new Scene3D(device, fb.Outputs);
            using IGpuCommandList frameList = device.Factory.CreateCommandList();

            using (OpenFrame(device, frameList))
            {
                int alive = device.TexturesAlive;                       // after the scene built its own
                for (int i = 0; i < Attempts; i++)
                    AssertRefused(() => scene.LoadTexture(new byte[4 * 4 * 4], 4, 4), "Scene3D.LoadTexture");

                Assert.Equal(alive, device.TexturesAlive);
            }
        }

        [Fact]
        public void Scene3D_LoadSplatMaterial_frees_both_arrays_when_it_is_refused()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuFramebuffer fb = NewTarget(device.Factory);
            using var scene = new Scene3D(device, fb.Outputs);
            using IGpuCommandList frameList = device.Factory.CreateCommandList();

            var layers = new List<SplatLayerImage>();
            for (int i = 0; i < SplatMaterialConfig.LayerCount; i++)
                layers.Add(new SplatLayerImage
                {
                    AlbedoRgba = new byte[4 * 4 * 4],
                    NormalRgba = new byte[4 * 4 * 4],
                });

            using (OpenFrame(device, frameList))
            {
                int alive = device.TexturesAlive;
                for (int i = 0; i < Attempts; i++)
                    AssertRefused(() => scene.LoadSplatMaterial(4, 4, layers), "Scene3D.LoadSplatMaterial");

                // Two 5-layer mipped arrays per attempt, which is the most expensive thing a refusal could have
                // stranded anywhere in the engine.
                Assert.Equal(alive, device.TexturesAlive);
            }
        }

        [Fact]
        public void Render2D_RenderToTexture_frees_its_target_when_it_is_refused()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuCommandList frameList = device.Factory.CreateCommandList();

            using (OpenFrame(device, frameList))
            {
                int alive = device.TexturesAlive;
                for (int i = 0; i < Attempts; i++)
                    AssertRefused(
                        () => Render2DCore.RenderToTexture(device, W, H, Color.Black, _ => { }),
                        "Render2DSurface.CaptureToTexture");

                // The target is the one resource this path's finally deliberately keeps, since on the SUCCESS path
                // it survives into the returned Texture2D. That is exactly why the throw path has to free it.
                Assert.Equal(alive, device.TexturesAlive);
            }
        }

        /// <summary>
        /// The barrier's leak is a fence rather than a texture, and it is invisible from the outside: a popped
        /// fence is already off the free stack and <c>Dispose</c> drains only that stack, so a refusal that loses
        /// one shows up nowhere except as a FRESH create on the next submission. So that is what this asserts.
        /// </summary>
        [Fact]
        public void The_retire_barrier_recycles_its_fence_when_it_is_refused()
        {
            using var device = new OpenListTrackingGpuDevice(completionFences: true);
            using IGpuFramebuffer fb = NewTarget(device.Factory);
            using var scene = new Scene3D(device, fb.Outputs);
            using IGpuCommandList frameList = device.Factory.CreateCommandList();

            scene.UnloadMesh(scene.LoadMesh(Triangle()));
            scene.Begin();                       // seals the first batch behind a fence, outside any recording
            scene.UnloadMesh(scene.LoadMesh(Triangle()));

            using (OpenFrame(device, frameList))
                AssertRefused(scene.Begin, "GpuRetireBarrier.Submit");

            int fences = device.FencesCreated;   // the refused submission's fence, wherever it went

            // Nothing was sealed, so the same retirement is still pending and the next Begin fires the barrier
            // again. It must REUSE the fence the refusal was holding rather than create another.
            scene.Begin();
            Assert.Equal(fences, device.FencesCreated);
        }

        /// <summary>
        /// THE BARRIER'S REFUSAL IS DATA-DEPENDENT, which is the half a host has to be told about, because it is
        /// what makes this site behave unlike the other six. <c>RetiredResourcePool.BeginFrame</c> only reaches the
        /// barrier when something was RETIRED since the previous Begin, so a mis-phased host boots perfectly clean,
        /// runs its whole menu and its first minutes of play, and then throws on the first mesh unload. Both halves
        /// are pinned here so neither can drift: no retirement is silent, one retirement refuses.
        /// </summary>
        [Fact]
        public void Scene3D_Begin_inside_a_recording_refuses_only_once_something_has_been_retired()
        {
            using var device = new OpenListTrackingGpuDevice(completionFences: true);
            using IGpuFramebuffer fb = NewTarget(device.Factory);
            using var scene = new Scene3D(device, fb.Outputs);
            using IGpuCommandList frameList = device.Factory.CreateCommandList();

            using (OpenFrame(device, frameList))
            {
                scene.Begin();                   // nothing retired, so the barrier is never reached
                scene.Begin();
            }

            scene.UnloadMesh(scene.LoadMesh(Triangle()));

            using (OpenFrame(device, frameList))
                AssertRefused(scene.Begin, "GpuRetireBarrier.Submit");
        }

        /// <summary>
        /// And the same barrier from where every host actually begins a scene, which is the frame's pre-record
        /// phase. It submits, nothing is refused, and the peak stays at one open recording.
        /// </summary>
        [Fact]
        public void The_retire_barrier_is_free_in_the_pre_record_phase()
        {
            using var device = new OpenListTrackingGpuDevice(completionFences: true);
            using IGpuFramebuffer fb = NewTarget(device.Factory);
            using var scene = new Scene3D(device, fb.Outputs);

            scene.UnloadMesh(scene.LoadMesh(Triangle()));
            scene.Begin();

            Assert.Equal(0, device.OpenLists);
            Assert.Equal(1, device.PeakOpenLists);   // the barrier's own list, opened and closed with nothing else up
        }
    }
}
