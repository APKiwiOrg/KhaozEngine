using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The three sibling unload paths that used to drain the whole device on the frame thread, once per call
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/383">#383</see>). <c>UnloadMesh</c> moved off that
    /// first (#99) and these followed it: skinned mesh, texture, splat material. The tile-ground material is covered
    /// here too, as the one path that was written retiring rather than converted, so nothing can quietly reintroduce
    /// a per-unload drain through the newest pipeline.
    /// <para>
    /// Two things are asserted per path and they are different claims. The UNLOAD CALL must not drain, which is the
    /// stall being removed, and an MMO client paid one per despawned avatar. And the resource must still not be
    /// destroyed in the frame it was retired in, which is the rule the drain was there to keep: queued GPU work may
    /// still reference it, and Mesa lavapipe executes that work on its own thread and segfaults on a resource freed
    /// out from under it (8c2a6c6b).
    /// </para>
    /// <para>
    /// The scene's own teardown rides along at the bottom, because it frees the same skinned pair through the same
    /// two lists and was freeing only half of it.
    /// </para>
    /// <para>
    /// This runs headless on <see cref="FakeGpuDevice"/>, so it is an ordinary <c>[Fact]</c> rather than a
    /// <c>[GpuFact]</c> that skips without <c>KE_GPU_TESTS</c>. The fake reports no completion fences, so the queue
    /// takes its frame-count-plus-one-drain fallback, which is the policy where the batching is OBSERVABLE: a batch
    /// dies on an exact frame instead of whenever a real fence happened to signal. <c>Scene3DUnloadDrainTests</c> is
    /// the same coverage against a real device, including the fenced policy.
    /// </para>
    /// </summary>
    public sealed class Scene3DUnloadRetireTests
    {
        static readonly byte[] Pixel = new byte[] { 255, 255, 255, 255 };   // 1x1 RGBA8

        sealed class Harness
        {
            internal required SpyGpuDevice Spy { get; init; }
            internal required FakeGpuResourceFactory Factory { get; init; }
            internal required Scene3D Scene { get; init; }
            internal required IGpuFramebuffer Target { get; init; }
        }

        static Harness NewHarness()
        {
            var fake = new FakeGpuDevice();
            var spy = new SpyGpuDevice(fake);
            var factory = (FakeGpuResourceFactory)fake.Factory;
            IGpuTexture tex = factory.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            IGpuFramebuffer fb = factory.CreateFramebuffer(null, tex);
            var scene = new Scene3D(spy, fb.Outputs);
            return new Harness { Spy = spy, Factory = factory, Scene = scene, Target = fb };
        }

        // Everything the factory handed out after `from`, which is how a test names the resources ONE load created
        // without the scene having to expose them: constructing a Scene3D creates plenty of its own.
        static List<FakeTexture> TexturesSince(FakeGpuResourceFactory f, int from)
            => f.Textures.GetRange(from, f.Textures.Count - from);
        static List<FakeBuffer> BuffersSince(FakeGpuResourceFactory f, int from)
            => f.Buffers.GetRange(from, f.Buffers.Count - from);
        static List<FakeResourceSet> SetsSince(FakeGpuResourceFactory f, int from)
            => f.ResourceSets.GetRange(from, f.ResourceSets.Count - from);

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

        static List<SplatLayerImage> Layers()
        {
            var layers = new List<SplatLayerImage>();
            for (int i = 0; i < SplatMaterialConfig.LayerCount; i++)
                layers.Add(new SplatLayerImage { AlbedoRgba = Pixel, NormalRgba = Pixel });
            return layers;
        }

        static List<TileGroundLayerImage> GroundLayers()
            => new() { new TileGroundLayerImage { AlbedoRgba = Pixel } };

        [Fact]
        public void UnloadSkinnedMesh_does_not_drain_and_frees_both_material_sets_at_a_later_frame()
        {
            // The high-value one. An MMO client despawns avatars and corpses continuously as they leave interest
            // range, and every one of those used to stall the frame thread on a full-device drain.
            Harness h = NewHarness();
            Scene3D.TextureHandle albedo = h.Scene.LoadTexture(Pixel, 1, 1);
            int bufFrom = h.Factory.Buffers.Count, setFrom = h.Factory.ResourceSets.Count;
            SkinnedGltfMesh tube = SkinnedMeshBuilder.BuildTube(0.5f, 4f, 6, 6, 4, Axis.Z);
            SkinnedMeshHandle mesh = h.Scene.LoadSkinnedMesh(tube, albedo);

            List<FakeBuffer> buffers = BuffersSince(h.Factory, bufFrom);
            List<FakeResourceSet> sets = SetsSince(h.Factory, setFrom);
            Assert.Equal(2, buffers.Count);   // vertex + index
            Assert.Equal(2, sets.Count);      // the CPU-path material set and the GPU-skinning one
            int before = h.Spy.WaitForIdleCalls;

            h.Scene.UnloadSkinnedMesh(mesh);

            Assert.Equal(before, h.Spy.WaitForIdleCalls);   // the whole point: the despawn costs no stall
            foreach (FakeBuffer b in buffers) Assert.False(b.Disposed);
            foreach (FakeResourceSet s in sets) Assert.False(s.Disposed);

            h.Scene.Begin();
            foreach (FakeBuffer b in buffers)
                Assert.False(b.Disposed);   // never destroyed in the frame it was retired in

            for (int i = 1; i < GpuRetireQueue.DefaultFrameDelay; i++) h.Scene.Begin();

            foreach (FakeBuffer b in buffers) Assert.True(b.Disposed);
            foreach (FakeResourceSet s in sets) Assert.True(s.Disposed);
            Assert.Equal(before + 1, h.Spy.WaitForIdleCalls);   // one drain for the batch, not one per unload
        }

        [Fact]
        public void UnloadTexture_does_not_drain_and_frees_the_texture_at_a_later_frame()
        {
            Harness h = NewHarness();
            int texFrom = h.Factory.Textures.Count;
            Scene3D.TextureHandle t = h.Scene.LoadTexture(Pixel, 1, 1);
            List<FakeTexture> textures = TexturesSince(h.Factory, texFrom);
            FakeTexture uploaded = Assert.Single(textures);
            int before = h.Spy.WaitForIdleCalls;

            h.Scene.UnloadTexture(t);

            Assert.Equal(before, h.Spy.WaitForIdleCalls);
            Assert.False(uploaded.Disposed);
            Assert.Equal(0, h.Scene.LiveTextureCount);   // the slot is released immediately, the texture is not

            h.Scene.Begin();
            Assert.False(uploaded.Disposed);

            for (int i = 1; i < GpuRetireQueue.DefaultFrameDelay; i++) h.Scene.Begin();

            Assert.True(uploaded.Disposed);
            Assert.Equal(before + 1, h.Spy.WaitForIdleCalls);
        }

        [Fact]
        public void UnloadTexture_does_not_drain_through_the_particle_set_cache_either()
        {
            // The half of the texture path that is easy to miss. The particle renderer caches a resource set per
            // atlas keyed by the texture's list index, so UnloadTexture drops that cache too, and the renderer used
            // to drain the device itself to do it. Any scene that drew particles at all would have kept its
            // per-unload stall through there, with the Scene3D-side drain gone and the test still green.
            Harness h = NewHarness();
            Scene3D.TextureHandle atlas = h.Scene.LoadTexture(Pixel, 1, 1);
            int setFrom = h.Factory.ResourceSets.Count;

            h.Scene.Begin();
            h.Scene.DrawParticle(new ParticleSprite
            {
                Position = Vector3.Zero,
                Size = 1f,
                Color = Color.White,
                Flipbook = new ParticleFlipbook(atlas, 1, 1),
            });
            h.Scene.PrepareFrame();
            using (IGpuCommandList sink = h.Factory.CreateCommandList())
            {
                sink.Begin();
                h.Scene.RenderInternal(sink, 16, 16, h.Target);
                sink.End();
            }

            // The set the renderer cached for this atlas, which is what UnloadTexture has to drop.
            Assert.NotEmpty(SetsSince(h.Factory, setFrom));
            List<FakeResourceSet> cached = SetsSince(h.Factory, setFrom);
            int before = h.Spy.WaitForIdleCalls;

            h.Scene.UnloadTexture(atlas);

            Assert.Equal(before, h.Spy.WaitForIdleCalls);
            foreach (FakeResourceSet s in cached) Assert.False(s.Disposed);   // retired, not destroyed on the spot

            for (int i = 0; i < GpuRetireQueue.DefaultFrameDelay; i++) h.Scene.Begin();

            foreach (FakeResourceSet s in cached) Assert.True(s.Disposed);
        }

        [Fact]
        public void UnloadSplatMaterial_does_not_drain_and_frees_the_whole_bundle_at_a_later_frame()
        {
            Harness h = NewHarness();
            int texFrom = h.Factory.Textures.Count, bufFrom = h.Factory.Buffers.Count,
                setFrom = h.Factory.ResourceSets.Count;
            Scene3D.SplatMaterialHandle m = h.Scene.LoadSplatMaterial(1, 1, Layers());
            List<FakeTexture> arrays = TexturesSince(h.Factory, texFrom);
            List<FakeBuffer> ubos = BuffersSince(h.Factory, bufFrom);
            List<FakeResourceSet> sets = SetsSince(h.Factory, setFrom);
            Assert.Equal(2, arrays.Count);   // albedo + normal, five layers each
            int before = h.Spy.WaitForIdleCalls;

            h.Scene.UnloadSplatMaterial(m);

            Assert.Equal(before, h.Spy.WaitForIdleCalls);
            foreach (FakeTexture a in arrays) Assert.False(a.Disposed);
            Assert.Equal(0, h.Scene.LiveSplatMaterialCount);

            h.Scene.Begin();
            foreach (FakeTexture a in arrays) Assert.False(a.Disposed);

            for (int i = 1; i < GpuRetireQueue.DefaultFrameDelay; i++) h.Scene.Begin();

            // The whole bundle goes together: the material is retired as ONE resource, so its arrays, its params
            // UBO, its set and any sampler it owned die in the same batch.
            foreach (FakeTexture a in arrays) Assert.True(a.Disposed);
            foreach (FakeBuffer b in ubos) Assert.True(b.Disposed);
            foreach (FakeResourceSet s in sets) Assert.True(s.Disposed);
            Assert.Equal(before + 1, h.Spy.WaitForIdleCalls);
        }

        [Fact]
        public void UnloadTileGroundMaterial_does_not_drain_and_frees_the_whole_bundle_at_a_later_frame()
        {
            // The splat test's sibling, for the pipeline a tile world's ground draws through. A view rebuilding its
            // material on a catalog change unloads one, and an editor session does that repeatedly.
            Harness h = NewHarness();
            int texFrom = h.Factory.Textures.Count, bufFrom = h.Factory.Buffers.Count,
                setFrom = h.Factory.ResourceSets.Count;
            Scene3D.TileGroundMaterialHandle m = h.Scene.LoadTileGroundMaterial(1, 1, GroundLayers());
            List<FakeTexture> arrays = TexturesSince(h.Factory, texFrom);
            List<FakeBuffer> ubos = BuffersSince(h.Factory, bufFrom);
            List<FakeResourceSet> sets = SetsSince(h.Factory, setFrom);
            FakeTexture array = Assert.Single(arrays);   // one albedo array, a layer per catalog material
            int before = h.Spy.WaitForIdleCalls;

            h.Scene.UnloadTileGroundMaterial(m);

            Assert.Equal(before, h.Spy.WaitForIdleCalls);
            Assert.False(array.Disposed);
            Assert.Equal(0, h.Scene.LiveTileGroundMaterialCount);

            h.Scene.Begin();
            Assert.False(array.Disposed);

            for (int i = 1; i < GpuRetireQueue.DefaultFrameDelay; i++) h.Scene.Begin();

            // One resource in the queue, the whole material behind it: array, params UBO, set and any owned sampler.
            Assert.True(array.Disposed);
            foreach (FakeBuffer b in ubos) Assert.True(b.Disposed);
            foreach (FakeResourceSet st in sets) Assert.True(st.Disposed);
            Assert.Equal(before + 1, h.Spy.WaitForIdleCalls);
        }

        [Fact]
        public void A_burst_of_mixed_unloads_costs_one_drain_between_them_all()
        {
            // What the per-call drain actually cost, stated as a number. A streaming boundary unloads a mix at once
            // (chunk meshes, their splat material, the avatars that were standing on them), and every one of those
            // used to be its own full-device drain. They are one batch now.
            Harness h = NewHarness();
            Scene3D.TextureHandle albedo = h.Scene.LoadTexture(Pixel, 1, 1);
            SkinnedGltfMesh tube = SkinnedMeshBuilder.BuildTube(0.5f, 4f, 6, 6, 4, Axis.Z);
            var skinned = new List<SkinnedMeshHandle>();
            for (int i = 0; i < 8; i++) skinned.Add(h.Scene.LoadSkinnedMesh(tube, albedo));
            MeshHandle rigid = h.Scene.LoadMesh(Triangle());
            Scene3D.SplatMaterialHandle splat = h.Scene.LoadSplatMaterial(1, 1, Layers());
            Scene3D.TileGroundMaterialHandle ground = h.Scene.LoadTileGroundMaterial(1, 1, GroundLayers());
            Scene3D.TextureHandle streamed = h.Scene.LoadTexture(Pixel, 1, 1);
            int before = h.Spy.WaitForIdleCalls;

            foreach (SkinnedMeshHandle s in skinned) h.Scene.UnloadSkinnedMesh(s);
            h.Scene.UnloadMesh(rigid);
            h.Scene.UnloadSplatMaterial(splat);
            h.Scene.UnloadTileGroundMaterial(ground);
            h.Scene.UnloadTexture(streamed);

            Assert.Equal(before, h.Spy.WaitForIdleCalls);   // twelve unloads, zero drains
            Assert.True(h.Scene.RetiredResourceCount > 0);

            for (int i = 0; i < GpuRetireQueue.DefaultFrameDelay; i++) h.Scene.Begin();

            Assert.Equal(0, h.Scene.RetiredResourceCount);
            Assert.Equal(before + 1, h.Spy.WaitForIdleCalls);   // and one drain to free the lot of them
        }

        [Fact]
        public void Dispose_frees_both_of_a_skinned_mesh_s_material_sets()
        {
            // Teardown rather than an unload, and the same pair of sets. Dispose freed the set-0 CPU-path set and
            // walked past the set-1 GPU-skinning one that LoadSkinnedMesh builds alongside it whenever the mesh is
            // textured, so a textured skinned mesh still loaded at teardown leaked one resource set. The native
            // Vulkan backend reports that class of leak as a VUID-vkDestroyDevice-device-05137 object leak.
            Harness h = NewHarness();
            Scene3D.TextureHandle albedo = h.Scene.LoadTexture(Pixel, 1, 1);
            int setFrom = h.Factory.ResourceSets.Count;
            SkinnedGltfMesh tube = SkinnedMeshBuilder.BuildTube(0.5f, 4f, 6, 6, 4, Axis.Z);
            h.Scene.LoadSkinnedMesh(tube, albedo);

            List<FakeResourceSet> sets = SetsSince(h.Factory, setFrom);
            Assert.Equal(2, sets.Count);   // set 0 (CPU path) and set 1 (GPU skinning), the leak was the second

            h.Scene.Dispose();

            foreach (FakeResourceSet s in sets) Assert.True(s.Disposed);
        }
    }
}
