using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render2D.Internal;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The regression guard for the D3D11 encode decay: a frame's per-frame uniform blocks must go up as ONE
    /// whole-buffer write each, not as a run of partial writes.
    /// <para>
    /// Why the shape matters, and why it matters on ONE backend in particular. Direct3D 11 forbids a partial box
    /// on a constant buffer, so a partial write to a uniform buffer that is not ring-backed cannot go up as one
    /// <c>UpdateSubresource</c>: it has to take a staging copy, and a staging copy that maps the immediate context
    /// blocks until the GPU is done with the buffer being recycled. That is a CPU/GPU sync point sitting in the
    /// middle of a pass's encode. It was measured rather than reasoned: the model pass recorded five partial
    /// writes per destination and the shadow depth pass three per cascade, which turned a Windows client's shadow
    /// and model encode into 12 to 17 ms while the same scene encoded in under 1 ms on Metal. The native
    /// Direct3D 11 backend's uniform ring (<c>D3D11UniformRing</c>) is what makes such a write a memcpy into
    /// already-mapped memory today, and Metal and Vulkan never had the split at all, so what this test holds is
    /// the SHAPE: a renderer that goes back to a run of partial writes has grown that cost back the moment a
    /// destination leaves the ring. It runs on both legs (Metal locally, WARP in CI).
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
        // One GPU-skinned draw starts each growable UBO at its minimum eight slots.
        const uint SkinnedMainUboBytes = 8 * 9472;
        const uint SkinnedShadowUboBytes = 8 * 8448;
        // WaterRenderer: min four planes of SlotBytes (768), the 256-aligned round-up of the 672-byte payload.
        const uint WaterUboBytes = 4 * 768;
        // OverlayMeshRenderer / SpriteBatch: both start at eight 256-byte dynamic-offset slots.
        const uint OverlayUboBytes = 8 * 256;
        const uint ViewProjUboBytes = 8 * 256;
        // ModelRenderer.CreateSplatParamsUbo: the frame block plus the material's retained params tail.
        const uint SplatCombinedUboBytes = FrameUboBytes + 112;

        static void AssertOneWholeBufferWrite(RecordingGpuCommandList rec, uint size, string what)
        {
            List<RecordingGpuCommandList.Upload> hits = rec.ToBuffersOfSize(size);
            Assert.True(hits.Count > 0, $"{what}: no {size}-byte destination was written this frame at all");

            var distinct = new HashSet<IGpuBuffer>();
            foreach (RecordingGpuCommandList.Upload u in hits) distinct.Add(u.Buffer);
            Assert.True(distinct.Count == 1,
                $"{what}: {distinct.Count} different {size}-byte buffers were written, so size no longer identifies it - retarget this assertion");

            Assert.True(hits.Count == 1,
                $"{what}: expected ONE upload per frame, got {hits.Count}. A run of partial per-frame writes to a " +
                "uniform buffer is what the D3D11 encode decay was made of (see the class remarks). Pack the " +
                "block and upload it once.");
            Assert.True(hits[0].IsWholeBuffer,
                $"{what}: the upload covered [{hits[0].Offset}, {hits[0].Offset + hits[0].Bytes}) of {size} bytes. " +
                "Only a whole-buffer write from offset 0 avoids a staging copy on a Direct3D 11 constant buffer.");
        }

        /// <summary>As <see cref="AssertOneWholeBufferWrite"/>, but for a destination a frame legitimately writes
        /// more than once. <c>SpriteBatch</c> is the only one: a Begin's draws are recorded before the next Begin
        /// exists, so its uploads cannot be folded into one the way a pass that knows every slot up front can. What
        /// still has to hold is that every one of them is WHOLE, because that is the half D3D11 charges for.</summary>
        static void AssertOnlyWholeBufferWrites(RecordingGpuCommandList rec, uint size, int expected, string what)
        {
            List<RecordingGpuCommandList.Upload> hits = rec.ToBuffersOfSize(size);
            Assert.True(hits.Count > 0, $"{what}: no {size}-byte destination was written this frame at all");

            var distinct = new HashSet<IGpuBuffer>();
            foreach (RecordingGpuCommandList.Upload u in hits) distinct.Add(u.Buffer);
            Assert.True(distinct.Count == 1,
                $"{what}: {distinct.Count} different {size}-byte buffers were written, so size no longer identifies it - retarget this assertion");

            Assert.True(hits.Count == expected, $"{what}: expected {expected} uploads this frame, got {hits.Count}");
            for (int i = 0; i < hits.Count; i++)
                Assert.True(hits[i].IsWholeBuffer,
                    $"{what}: upload {i} covered [{hits[i].Offset}, {hits[i].Offset + hits[i].Bytes}) of {size} bytes. " +
                    "Only a whole-buffer write from offset 0 avoids a staging copy on a Direct3D 11 constant buffer.");
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
                scene.PrepareFrame();
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

        [GpuFact]
        public void A_shadowed_gpu_skinned_frame_uploads_the_main_and_shadow_slot_buffers_once_each()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var f = gd.Factory;

            using IGpuTexture finalTex = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer finalFB = f.CreateFramebuffer(null, finalTex);

            using var scene = new Scene3D(gd, finalFB.Outputs);
            scene.UseGpuSkinning = true;
            scene.Post.Starfield = false;
            scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
            scene.Post.LightDirection = new Vector3(-0.55f, -0.8f, -0.25f);
            scene.Camera.Frame(new Vector3(0f, 0.4f, 0f), new Vector3(6f, 4.5f, 6f));

            MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            SkinnedGltfMesh tube = SkinnedMeshBuilder.BuildTube(0.5f, 4f, 10, 10, 6, Axis.Z);
            SkinnedMeshHandle caster = scene.LoadSkinnedMesh(tube);
            SkinnedMeshHandle caster2 = scene.LoadSkinnedMesh(tube);

            using IGpuCommandList real = f.CreateCommandList();
            var rec = new RecordingGpuCommandList(real);

            scene.Begin();
            scene.Draw(floor, Matrix4x4.Identity);
            scene.DrawSkinned(caster, tube.RestPose, Matrix4x4.CreateTranslation(0f, 0.6f, 0f), Color.White);
            // A SECOND caster, because PackSkinnedShadowSlot packs one slot per caster PER CASCADE (#408 names it
            // that way). One caster would leave the assertion unable to tell a per-pass upload from a per-caster one.
            scene.DrawSkinned(caster2, tube.RestPose, Matrix4x4.CreateTranslation(1.6f, 0.6f, 0.4f), Color.White);
            scene.PrepareFrame();
            rec.Begin();
            scene.RenderInternal(rec, W, H, finalFB);
            rec.End();
            gd.Submit(real);
            gd.WaitForIdle();

            Assert.False(scene.ShadowPassSkippedLastFrame,
                "a GPU-skinned caster must keep the depth pass dirty so the shadow-slot upload is exercised");
            AssertOneWholeBufferWrite(rec, SkinnedMainUboBytes, "skinned main UBO");
            AssertOneWholeBufferWrite(rec, SkinnedShadowUboBytes, "skinned shadow UBO");
        }

        [GpuFact]
        public void A_multi_plane_water_frame_uploads_every_plane_slot_in_one_whole_write()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var f = gd.Factory;

            using IGpuTexture finalTex = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer finalFB = f.CreateFramebuffer(null, finalTex);

            using var scene = new Scene3D(gd, finalFB.Outputs);
            scene.Post.Starfield = false;
            scene.Camera.Frame(new Vector3(0f, 0.4f, 0f), new Vector3(10f, 6f, 10f));

            MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(20f, 0.1f));

            using IGpuCommandList real = f.CreateCommandList();
            var rec = new RecordingGpuCommandList(real);

            scene.Begin();
            scene.Draw(floor, Matrix4x4.Identity);
            // THREE planes at different levels. One would make the assertion vacuous: a per-plane write and a
            // single whole-buffer write are the same command when there is only one plane.
            scene.DrawWater(new WaterPlane(centerX: -4f, surfaceY: 0f, centerZ: 0f, halfExtentX: 3f));
            scene.DrawWater(new WaterPlane(centerX: 0f, surfaceY: 0.4f, centerZ: 0f, halfExtentX: 3f));
            scene.DrawWater(new WaterPlane(centerX: 4f, surfaceY: -0.3f, centerZ: 0f, halfExtentX: 3f));
            scene.PrepareFrame();
            rec.Begin();
            scene.RenderInternal(rec, W, H, finalFB);
            rec.End();
            gd.Submit(real);
            gd.WaitForIdle();

            AssertOneWholeBufferWrite(rec, WaterUboBytes, "water plane UBO");
        }

        [GpuFact]
        public void An_overlay_proxy_frame_uploads_every_draw_slot_in_one_whole_write()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var f = gd.Factory;

            using IGpuTexture finalTex = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer finalFB = f.CreateFramebuffer(null, finalTex);

            using var scene = new Scene3D(gd, finalFB.Outputs);
            scene.Post.Starfield = false;
            scene.Camera.Frame(new Vector3(0f, 0.4f, 0f), new Vector3(6f, 4.5f, 6f));

            MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            MeshHandle proxy = scene.LoadMesh(MeshPrimitives.Box(0.9f));

            using IGpuCommandList real = f.CreateCommandList();
            var rec = new RecordingGpuCommandList(real);

            scene.Begin();
            scene.Draw(floor, Matrix4x4.Identity);
            // Four proxies, at different depths so the pass's back-to-front sort really reorders them and the slot
            // each draw binds is the sorted index rather than the queue index.
            scene.DrawOverlayMesh(proxy, Matrix4x4.CreateTranslation(-1.5f, 0.6f, -1.5f));
            scene.DrawOverlayMesh(proxy, Matrix4x4.CreateTranslation(1.5f, 0.6f, 1.5f));
            scene.DrawOverlayMesh(proxy, Matrix4x4.CreateTranslation(-1.5f, 0.6f, 1.5f));
            scene.DrawOverlayMesh(proxy, Matrix4x4.CreateTranslation(1.5f, 0.6f, -1.5f));
            scene.PrepareFrame();
            rec.Begin();
            scene.RenderInternal(rec, W, H, finalFB);
            rec.End();
            gd.Submit(real);
            gd.WaitForIdle();

            AssertOneWholeBufferWrite(rec, OverlayUboBytes, "overlay proxy UBO");
        }

        [GpuFact]
        public void A_splat_terrain_frame_uploads_the_combined_material_block_in_one_whole_write()
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

            Scene3D.SplatMaterialHandle mat = scene.LoadSplatMaterial(4, 4, FiveFlatLayers(4));
            var w = new Vector4(1f, 0f, 0f, 0f);
            const float e = 6f;
            var verts = new[]
            {
                new ModelVertex(new Vector3(-e, 0, -e), Vector3.UnitY, w, new Vector2(0, 0)),
                new ModelVertex(new Vector3( e, 0, -e), Vector3.UnitY, w, new Vector2(1, 0)),
                new ModelVertex(new Vector3( e, 0,  e), Vector3.UnitY, w, new Vector2(1, 1)),
                new ModelVertex(new Vector3(-e, 0,  e), Vector3.UnitY, w, new Vector2(0, 1)),
            };
            MeshHandle terrain = scene.LoadMesh(new GltfMesh(verts, new ushort[] { 0, 1, 2, 0, 2, 3 }), mat);
            MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1.2f));

            using IGpuCommandList real = f.CreateCommandList();
            var rec = new RecordingGpuCommandList(real);

            scene.Begin();
            scene.Draw(terrain, Matrix4x4.Identity, Color.White);
            scene.Draw(box, Matrix4x4.CreateTranslation(-1.2f, 0.6f, -0.4f));
            scene.PrepareFrame();
            rec.Begin();
            scene.RenderInternal(rec, W, H, finalFB);
            rec.End();
            gd.Submit(real);
            gd.WaitForIdle();

            AssertOneWholeBufferWrite(rec, SplatCombinedUboBytes, "splat combined material UBO");
        }

        [GpuFact]
        public void A_sprite_frame_writes_its_view_projection_slots_whole_once_per_begin()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var f = gd.Factory;

            using IGpuTexture target = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, target);
            using var core = new Render2DCore(gd, fb.Outputs, ownsDevice: false);
            SpriteBatch batch = core.Batch;
            Texture2D tex = core.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);

            using IGpuCommandList real = f.CreateCommandList();
            var rec = new RecordingGpuCommandList(real);
            rec.Begin();
            rec.SetFramebuffer(fb);
            rec.ClearColorTarget(0, new Color(0, 0, 0, 1));
            batch.NewFrame(rec, W, H);

            // Three Begins, each with a draw, so three slots are claimed and three uploads recorded. A batch cannot
            // fold these into one (see AssertOnlyWholeBufferWrites); what it CAN do is make each one whole.
            const int Begins = 3;
            for (int i = 0; i < Begins; i++)
            {
                batch.Begin(Matrix4x4.CreateTranslation(i * 2f, 0f, 0f));
                batch.Draw(tex, new Vector2(i * 4f, 4f), new Color(1f, 1f, 1f, 1f));
                batch.End();
            }

            rec.End();
            gd.Submit(real);
            gd.WaitForIdle();

            AssertOnlyWholeBufferWrites(rec, ViewProjUboBytes, Begins, "sprite view-projection UBO");
        }

        /// <summary>Five flat single-colour splat layers, the cheapest material the splat pipeline accepts.</summary>
        static List<SplatLayerImage> FiveFlatLayers(int size)
        {
            var layers = new List<SplatLayerImage>();
            for (int i = 0; i < SplatMaterialConfig.LayerCount; i++)
            {
                var albedo = new byte[size * size * 4];
                var normal = new byte[size * size * 4];
                for (int p = 0; p < albedo.Length; p += 4)
                {
                    albedo[p] = (byte)(40 + i * 30); albedo[p + 1] = 110; albedo[p + 2] = 60; albedo[p + 3] = 255;
                    normal[p] = 128; normal[p + 1] = 128; normal[p + 2] = 255; normal[p + 3] = 255;
                }
                layers.Add(new SplatLayerImage { AlbedoRgba = albedo, NormalRgba = normal, TilesPerMetre = 0.25f, Roughness = 0.8f });
            }
            return layers;
        }
    }
}
