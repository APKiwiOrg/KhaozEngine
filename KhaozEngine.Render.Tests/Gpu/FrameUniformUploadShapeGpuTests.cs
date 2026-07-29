using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The regression guard for the D3D11 encode decay: a frame's per-frame uniform blocks must go up as ONE
    /// whole-buffer write each, not as a run of partial writes.
    /// <para>
    /// Why the shape matters, and why it matters on ONE backend in particular. Veldrid 4.9.0's
    /// <c>D3D11CommandList.UpdateBufferCore</c> splits three ways. A write to a non-Dynamic, non-Staging buffer takes
    /// <c>UpdateSubresource</c> on the deferred context - but for a UNIFORM buffer only when the write covers the
    /// whole buffer from offset 0, because D3D11 forbids a partial box on a constant buffer. Everything else falls
    /// through to the staging route: rent a staging buffer, hand it to <c>GraphicsDevice.UpdateBuffer</c>, which Maps
    /// the IMMEDIATE context with <c>D3D11_MAP_WRITE</c> (not WRITE_DISCARD, no DO_NOT_WAIT), then record a
    /// <c>CopySubresourceRegion</c>. That Map blocks until the GPU has finished with the staging buffer being
    /// recycled, so every partial uniform write is a CPU/GPU sync point sitting in the middle of a pass's encode.
    /// The model pass used to record five of them per destination and the shadow depth pass three per cascade, which
    /// is what turned a Windows client's shadow and model encode into 12-17 ms while the same scene encoded in under
    /// 1 ms on Metal. Metal and Vulkan have no such split, so this test asserts a property that is free there and
    /// load-bearing on D3D11, and it runs on both legs (Metal locally, WARP in CI).
    /// </para>
    /// <para>
    /// Asserted by destination SIZE rather than by a handle, because the buffers are private to the renderers. The
    /// two sizes are structural constants of the UBO layout: the model frame block is
    /// <c>ModelRenderer.UboBytes</c> = 1008 bytes and the shadow cascade buffer is
    /// <c>MaxCascades * 256</c> = 1024. The uniqueness assertion below is what keeps that indirection honest: if
    /// some other buffer ever lands on one of these sizes the test fails loudly instead of quietly asserting the
    /// wrong thing.
    /// </para>
    /// </summary>
    public sealed class FrameUniformUploadShapeGpuTests
    {
        const int W = 128, H = 96;

        // ModelRenderer.UboBytes: 176 header + 2 * 256 point-light arrays + 304 shadow tail + 16 render origin.
        const uint FrameUboBytes = 1008;
        // ShadowMapRenderer: MaxCascades (4) 256-byte dynamic slots.
        const uint ShadowCascadeUboBytes = 4 * 256;

        static void AssertOneWholeBufferWrite(RecordingGpuCommandList rec, uint size, string what)
        {
            List<RecordingGpuCommandList.Upload> hits = rec.ToBuffersOfSize(size);
            Assert.True(hits.Count > 0, $"{what}: no {size}-byte destination was written this frame at all");

            var distinct = new HashSet<IGpuBuffer>();
            foreach (RecordingGpuCommandList.Upload u in hits) distinct.Add(u.Buffer);
            Assert.True(distinct.Count == 1,
                $"{what}: {distinct.Count} different {size}-byte buffers were written, so size no longer identifies it - retarget this assertion");

            Assert.True(hits.Count == 1,
                $"{what}: expected ONE upload per frame, got {hits.Count}. A partial per-frame write to a uniform " +
                "buffer is a CPU/GPU sync point on D3D11 (see the class remarks); pack the block and upload it once.");
            Assert.True(hits[0].IsWholeBuffer,
                $"{what}: the upload covered [{hits[0].Offset}, {hits[0].Offset + hits[0].Bytes}) of {size} bytes. " +
                "Only a whole-buffer write from offset 0 escapes Veldrid's D3D11 partial-uniform-write staging route.");
        }

        [GpuFact]
        public void A_shadowed_frame_uploads_the_model_and_cascade_uniform_blocks_once_each()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var f = gd.Factory;

            using IGpuTexture finalTex = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer finalFB = f.CreateFramebuffer(null, finalTex);

            using var scene = new Scene3D(gd, finalFB.Outputs);
            scene.Post.Starfield = false;
            scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
            scene.Post.LightDirection = new Vector3(-0.55f, -0.8f, -0.25f);
            scene.Camera.Frame(new Vector3(0f, 0.4f, 0f), new Vector3(6f, 4.5f, 6f));

            MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1.4f));

            using IGpuCommandList real = f.CreateCommandList();
            var rec = new RecordingGpuCommandList(real);

            // Two frames. The first builds the shadow map; the second is the steady state a player actually sits in,
            // and is the one asserted, so nothing here rides on first-frame priming. The caster MOVES between them so
            // the depth pass is dirty on frame 2 and really re-uploads the cascade block (a skipped depth pass would
            // make the cascade assertion vacuous).
            for (int frame = 0; frame < 2; frame++)
            {
                scene.Begin();
                scene.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f));
                scene.Draw(box, Matrix4x4.CreateTranslation(-1.2f + frame * 0.35f, 0.7f, -0.4f));
                rec.Clear();
                rec.Begin();
                scene.RenderInternal(rec, W, H, finalFB);
                rec.End();
                gd.Submit(real);
                gd.WaitForIdle();
            }

            Assert.False(scene.ShadowPassSkippedLastFrame,
                "the asserted frame must have rendered the depth pass, otherwise the cascade assertion proves nothing");

            AssertOneWholeBufferWrite(rec, FrameUboBytes, "model frame UBO");
            AssertOneWholeBufferWrite(rec, ShadowCascadeUboBytes, "shadow cascade UBO");
        }
    }
}
