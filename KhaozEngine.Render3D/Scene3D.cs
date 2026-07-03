using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render2D;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D
{
    /// <summary>Blend mode for <see cref="Scene3D.DrawBillboard(System.Numerics.Vector3, float, KhaozEngine.Primitives.Color, KhaozEngine.Render3D.BillboardBlend)"/>: standard <see cref="Alpha"/> transparency,
    /// or <see cref="Additive"/> (source-alpha/one) for glowy accumulation (sparks, muzzle flashes).</summary>
    public enum BillboardBlend { Alpha, Additive }

    /// <summary>
    /// A drawable 3D scene: an <see cref="IsoCamera3D"/>, a set of uploaded meshes, a per-frame instance queue,
    /// and the pixel post chain. Load meshes once with <see cref="LoadMesh(KhaozEngine.Render3D.GltfMesh)"/>; each frame call
    /// <see cref="Begin"/>, queue instances with <see cref="Draw(KhaozEngine.Render3D.MeshHandle, System.Numerics.Matrix4x4)"/>, then have the surface/host render. Owns its
    /// GPU resources (via the KhaozEngine.Gpu seam) but records into a caller-supplied command list (see
    /// <see cref="Render3DSurface"/>); the public surface stays backend-free.
    /// </summary>
    public sealed class Scene3D : IDisposable
    {
        readonly IGpuDevice _gd;
        readonly GpuOutputDescription _targetOutput;
        readonly ModelRenderer _model;
        readonly PixelPostProcess _post;
        readonly LineRenderer _lines;
        readonly FillRenderer _fills;
        readonly BillboardRenderer _billboards;
        readonly TexturedBillboardRenderer _texBillboards;
        readonly BeamRenderer _beams;
        readonly Rendering.GroundDecalRenderer _decalRenderer;
        readonly Rendering.OverlayMeshRenderer _overlayMeshes;
        readonly RenderResources _res;
        // Slot-indexed GPU mesh storage parallel to _slots; a freed slot's entry is null until reused.
        readonly List<Mesh?> _meshes = new();
        readonly MeshSlotMap _slots = new();
        // Loaded albedo textures, indexed by TextureHandle.Index. Shared across meshes; disposed in Dispose.
        readonly List<IGpuTexture?> _textures = new();   // a slot is nulled by UnloadTexture (handle stays stable, not recycled)
        // Loaded splat-terrain materials, indexed by SplatMaterialHandle.ListIndex. Each owns its two texture
        // arrays + params UBO + resource set; shared across meshes; disposed in Dispose / UnloadSplatMaterial.
        readonly List<SplatMaterialEntry?> _splatMaterials = new();
        // Per-texture billboard resource sets, parallel to _textures (ListIndex), created lazily the first time a
        // texture is used for a textured billboard. Disposed in Dispose.
        readonly List<IGpuResourceSet?> _texBillboardSets = new();
        readonly SceneInstances _instances = new();
        // Per-frame dynamic point lights, cleared each Begin() like the instance queue. The host adds them
        // (already N-nearest-culled); the renderer clamps to MaxPointLights and zero-fills the rest.
        readonly List<ModelRenderer.PointLightData> _lights = new();
        readonly List<LineRenderer.LineVertex> _lineVerts = new();
        readonly List<FillRenderer.FillVertex> _fillVerts = new();
        readonly List<GroundDecal> _decals = new();
        // Translucent unlit overlay-mesh draws (collision proxies etc.): queued in submission order, flushed into the
        // model FB after the beams and before the post chain (depth-interleaved). Cleared each Begin().
        readonly List<(MeshHandle Mesh, Matrix4x4 World)> _overlayMeshDraws = new();
        readonly List<BillboardRenderer.BillboardVertex> _billboardAlpha = new();
        readonly List<BillboardRenderer.BillboardVertex> _billboardAdditive = new();
        // Textured depth-interleaved billboards: queued in submission order (NOT split by blend, so additive and
        // alpha quads stay correctly ordered against each other), coalesced into same-texture+blend runs at render.
        readonly List<TexturedBillboardItem> _texBillboardItems = new();
        readonly List<TexturedBillboardRun> _texBillboardRuns = new();
        readonly List<BillboardRenderer.BillboardVertex> _texBillboardVerts = new();
        // Glowing beams (lasers/thrusters/tethers): queued in submission order, flushed as one additive draw
        // into the model FB alongside the textured billboards (depth-interleaved, so geometry occludes them).
        readonly List<BeamItem> _beamItems = new();
        readonly List<BeamRenderer.BeamVertex> _beamVerts = new();
        // Reused per-frame grouping buffers (cleared, not realloc) for GPU instancing.
        readonly List<ModelRenderer.InstanceData> _instanceData = new();
        readonly List<MeshRun> _runs = new();
        // Skinned mesh storage, parallel to the rigid mesh storage above.
        readonly List<SkinnedMeshEntry?> _skinnedMeshes = new();
        readonly MeshSlotMap _skinnedSlots = new();
        readonly SkinnedSceneInstances _skinnedInstances = new();
        // Per-frame composed bone palette for every skinned draw (cleared each Begin), and reused grouping buffers.
        // Per-frame bone palette, slot-packed: draw i's composed matrices live at [i*MaxBonesPerDraw ..], padded to
        // the per-draw window so each draw's dynamic-offset bind selects exactly its slice. Cleared each Begin().
        readonly List<Matrix4x4> _boneMatrices = new();
        // CPU skinning (the bone-buffer GPU read corrupts past element 0 in the windowed Veldrid/Metal swapchain
        // context, so skinned meshes are deformed on the CPU and drawn through the proven-clean no-bone model
        // pipeline). _skinnedCpuVerts caches each loaded mesh's source vertices (parallel to _skinnedMeshes); the
        // three reused lists are the per-frame deformed-vertex stream, the per-draw instance data, and the draw list.
        readonly List<SkinnedVertex[]?> _skinnedCpuVerts = new();
        readonly List<ModelVertex> _cpuSkinnedVerts = new();
        readonly List<ModelRenderer.InstanceData> _cpuSkinnedInstances = new();
        readonly List<CpuSkinnedDraw> _cpuSkinnedDraws = new();
        Vector3 _billboardRight, _billboardUp;
        bool _billboardBasisValid;

        public IsoCamera3D Camera { get; } = new();

        /// <summary>
        /// Optional camera that overrides the built-in <see cref="Camera"/> for rendering this scene. Set it to a
        /// sibling camera (e.g. <see cref="FollowCamera3D"/>) to drive the view/projection from something other than
        /// the iso camera; null (the default) uses <see cref="Camera"/>. The override supplies only the read-only
        /// camera surface (<see cref="IIsoCamera3D"/>), so the caller owns its aspect ratio: set it from the
        /// framebuffer each frame. <see cref="Camera"/>'s aspect is still maintained by the scene.
        /// </summary>
        public IIsoCamera3D? CameraOverride { get; set; }

        /// <summary>The camera the render path reads this frame: <see cref="CameraOverride"/> if set, else <see cref="Camera"/>.</summary>
        IIsoCamera3D ActiveCamera => CameraOverride ?? Camera;

        public PixelPostProcessSettings Post { get; } = new();

        /// <summary>Host-set per-frame clock (seconds) driving beam pulse/scroll (see <see cref="DrawBeam"/> /
        /// <see cref="BeamStyle"/>). Set it once per frame in your draw callback (it runs after <see cref="Begin"/>),
        /// e.g. <c>scene.EffectTimeSeconds = totalSeconds</c>. NOT cleared by <see cref="Begin"/> - the host owns it.
        /// Presentation only; zero (never set) renders a static beam. A generic clock so future time-driven 3D
        /// effects can share it.</summary>
        public float EffectTimeSeconds { get; set; }

        /// <summary>Maximum dynamic point lights consumed in one frame. <see cref="AddLight"/> accepts any number,
        /// but only the first <see cref="MaxPointLights"/> queued are uploaded (extras are dropped); the host is
        /// expected to pick the N nearest per frame so a dense bullet-hell stays within budget.</summary>
        public const int MaxPointLights = ModelRenderer.MaxPointLights;

        internal Scene3D(IGpuDevice gd, GpuOutputDescription targetOutput)
        {
            _gd = gd;
            _targetOutput = targetOutput;
            _res = new RenderResources(gd, Post.RenderWidth, Post.RenderHeight);
            _model = new ModelRenderer(gd, _res.ModelFB.Outputs);
            _post = new PixelPostProcess(gd, _res.PingAFB.Outputs, targetOutput);
            _post.BindTargets(_res);
            _lines = new LineRenderer(gd, targetOutput);
            _fills = new FillRenderer(gd, targetOutput);
            _billboards = new BillboardRenderer(gd, targetOutput);
            // Textured billboards draw INTO the model MRT (depth-interleaved with meshes), so they target the model
            // framebuffer's output description, not the final target like the overlay renderers above.
            _texBillboards = new TexturedBillboardRenderer(gd, _res.ModelFB.Outputs);
            // Beams draw into the same model MRT as the textured billboards (depth-interleaved), so they target the
            // model framebuffer's output description.
            _beams = new BeamRenderer(gd, _res.ModelFB.Outputs);
            // Ground decals render into the lit color attachment + read-only scene depth (ColorDepthFB) before the
            // post chain, so they pass that framebuffer's output description (color format + depth format).
            _decalRenderer = new Rendering.GroundDecalRenderer(gd, _res.ColorDepthFB.Outputs);
            _overlayMeshes = new Rendering.OverlayMeshRenderer(gd, _res.ModelFB.Outputs);
        }

        /// <summary>An opaque handle to an albedo texture loaded with <see cref="LoadTexture(string)"/> /
        /// <see cref="LoadTexture(byte[],int,int)"/>. Pass it to <see cref="LoadMesh(GltfMesh,TextureHandle)"/> to
        /// texture a mesh. Wraps an index into Scene3D's internal texture list; the GPU texture stays internal.</summary>
        public readonly struct TextureHandle
        {
            /// <summary>Index into the owning scene's texture list (0-based). Internal detail; do not interpret.</summary>
            internal readonly int Index;
            internal TextureHandle(int index) { Index = index + 1; } // store +1 so default == Invalid (Index 0)
            /// <summary>An invalid handle (the same as <c>default</c>). Loading a mesh with this is untextured.</summary>
            public static TextureHandle Invalid => default;
            /// <summary>True when this handle refers to a loaded texture (not the <c>default</c>/Invalid handle).</summary>
            public bool IsValid => Index != 0;
            /// <summary>The 0-based list index this handle refers to. Only meaningful when <see cref="IsValid"/>.</summary>
            internal int ListIndex => Index - 1;
        }

        /// <summary>An opaque handle to a splat-terrain material (5 tileable layers + triplanar params) loaded with
        /// <see cref="LoadSplatMaterial"/>. Pass it to <see cref="LoadMesh(GltfMesh,SplatMaterialHandle)"/> to draw a
        /// mesh through the splat pipeline. Shared across many meshes (e.g. every terrain chunk).</summary>
        public readonly struct SplatMaterialHandle
        {
            internal readonly int Index;
            internal SplatMaterialHandle(int index) { Index = index + 1; } // store +1 so default == Invalid
            public static SplatMaterialHandle Invalid => default;
            public bool IsValid => Index != 0;
            internal int ListIndex => Index - 1;
        }

        /// <summary>A bundle of optional surface maps for <see cref="LoadMesh(GltfMesh,SurfaceMaps)"/> and
        /// <see cref="LoadSkinnedMesh(SkinnedGltfMesh,SurfaceMaps)"/>:
        /// albedo, tangent-space normal, and roughness (glTF metallic-roughness .g convention). Any invalid
        /// (<c>default</c>) handle falls back to the renderer's default for that slot (white albedo, flat
        /// normal, zero roughness), so binding only some maps is fine. Load each map with
        /// <see cref="LoadTexture(string)"/> / <see cref="LoadTexture(byte[],int,int)"/>.</summary>
        public readonly struct SurfaceMaps
        {
            public readonly TextureHandle Albedo;
            public readonly TextureHandle Normal;
            public readonly TextureHandle Roughness;
            public SurfaceMaps(TextureHandle albedo, TextureHandle normal = default, TextureHandle roughness = default)
            {
                Albedo = albedo; Normal = normal; Roughness = roughness;
            }
        }

        /// <summary>Upload a loaded mesh to the GPU once; returns a handle to instance it with <see cref="Draw(KhaozEngine.Render3D.MeshHandle, System.Numerics.Matrix4x4)"/>.
        /// Reuses a slot freed by <see cref="UnloadMesh"/> when one is available. The mesh is untextured (samples the
        /// renderer's 1x1 white default, so its colour is the baked vertex colour times any per-instance tint).</summary>
        public MeshHandle LoadMesh(GltfMesh mesh) => LoadMeshInternal(mesh, null);

        /// <summary>Upload a loaded mesh to the GPU once and bind <paramref name="texture"/> as its albedo. The
        /// fragment shader multiplies the sampled texel into the lit albedo (<c>texRgb * vColor * vTint</c>). An
        /// invalid/<c>default</c> <paramref name="texture"/> handle falls back to untextured (no throw).</summary>
        public MeshHandle LoadMesh(GltfMesh mesh, TextureHandle texture)
        {
            IGpuResourceSet? material = null;
            if (texture.IsValid)
                material = _model.CreateMaterialSet(_textures[texture.ListIndex]!);
            return LoadMeshInternal(mesh, material);
        }

        /// <summary>Upload a mesh and bind a full PBR-lite material (<paramref name="maps"/>): albedo + optional
        /// normal + optional roughness. Invalid handles fall back to the renderer defaults. Normal perturbation
        /// requires the mesh to carry tangents (glTF meshes via <see cref="GltfLoader"/>, or
        /// MeshAssembler output); primitives have none and are lit by their geometric normal.</summary>
        public MeshHandle LoadMesh(GltfMesh mesh, SurfaceMaps maps)
        {
            IGpuTexture? a = maps.Albedo.IsValid ? _textures[maps.Albedo.ListIndex] : null;
            IGpuTexture? n = maps.Normal.IsValid ? _textures[maps.Normal.ListIndex] : null;
            IGpuTexture? r = maps.Roughness.IsValid ? _textures[maps.Roughness.ListIndex] : null;
            IGpuResourceSet? material = (a != null || n != null || r != null)
                ? _model.CreateMaterialSet(a, n, r)
                : null;
            return LoadMeshInternal(mesh, material);
        }

        /// <summary>Upload a mesh and draw it through the splat-terrain pipeline with <paramref name="material"/>
        /// (its vertex <c>Color</c> carries the packed splat weights). An invalid handle falls back to the untextured
        /// model path. The splat material is shared (owned by the scene); unloading the mesh does NOT free it.</summary>
        public MeshHandle LoadMesh(GltfMesh mesh, SplatMaterialHandle material)
        {
            if (!material.IsValid) return LoadMesh(mesh);
            return LoadMeshInternal(mesh, null, material.ListIndex);
        }

        MeshHandle LoadMeshInternal(GltfMesh mesh, IGpuResourceSet? material, int splatMaterial = -1)
        {
            var f = _gd.Factory;
            var vb = f.CreateBuffer(new GpuBufferDescription((uint)(mesh.Vertices.Length * ModelVertex.SizeInBytes), GpuBufferUsage.VertexBuffer));
            _gd.UpdateBuffer(vb, 0, mesh.Vertices);
            var ib = CreateIndexBuffer(mesh.Indices32, mesh.IndexFormat);

            int index = _slots.Alloc(out int generation);
            var slot = new Mesh(vb, ib, mesh.Indices32.Length, mesh.IndexFormat, material, splatMaterial);
            if (index < _meshes.Count) _meshes[index] = slot;   // reused freed slot
            else _meshes.Add(slot);                              // fresh appended slot
            return new MeshHandle(index, generation);
        }

        /// <summary>Create + fill a GPU index buffer matching the mesh's chosen <see cref="GpuIndexFormat"/>. A
        /// 16-bit mesh uploads a narrowed <see cref="ushort"/> buffer (byte-identical to the pre-32-bit path, so
        /// existing renders are unchanged); a 32-bit mesh uploads the full <see cref="uint"/> indices.</summary>
        IGpuBuffer CreateIndexBuffer(uint[] indices32, GpuIndexFormat format)
        {
            var f = _gd.Factory;
            if (format == GpuIndexFormat.UInt32)
            {
                var ib = f.CreateBuffer(new GpuBufferDescription((uint)(indices32.Length * sizeof(uint)), GpuBufferUsage.IndexBuffer));
                _gd.UpdateBuffer(ib, 0, indices32);
                return ib;
            }
            var i16 = new ushort[indices32.Length];
            for (int i = 0; i < i16.Length; i++) i16[i] = (ushort)indices32[i];
            var ib16 = f.CreateBuffer(new GpuBufferDescription((uint)(i16.Length * sizeof(ushort)), GpuBufferUsage.IndexBuffer));
            _gd.UpdateBuffer(ib16, 0, i16);
            return ib16;
        }

        /// <summary>Decode a PNG/JPG file into an albedo texture (RGBA8) and return a handle for
        /// <see cref="LoadMesh(GltfMesh,TextureHandle)"/>. The texture is owned by the scene and freed in
        /// <see cref="Dispose"/>; it may be shared across several meshes.</summary>
        public TextureHandle LoadTexture(string pngPath)
        {
            ImageRgba img = ImageRgba.Load(pngPath);
            return LoadTexture(img.Pixels, img.Width, img.Height);
        }

        /// <summary>Create an albedo texture from raw RGBA8 bytes (row-major, <paramref name="width"/> *
        /// <paramref name="height"/> * 4 bytes) and return a handle. For procedural textures and tests. The texture
        /// is owned by the scene and freed in <see cref="Dispose"/>.</summary>
        public TextureHandle LoadTexture(byte[] rgba, int width, int height)
        {
            uint w = (uint)width, h = (uint)height, mips = SplatMaterialConfig.MipLevelCount(width, height);
            // A full mip chain is what stops distant model/prop surfaces from aliasing into "pixely" sparkle when the
            // camera moves (level 0 alone point-minifies at range). Generate it exactly like the splat path; the model
            // pass samples through the trilinear LinearSampler, which now has real mips to blend between. Skip the
            // generate for a 1-level texture (e.g. a 1x1 default) so those stay byte-identical.
            GpuTextureUsage usage = GpuTextureUsage.Sampled | (mips > 1 ? GpuTextureUsage.GenerateMipmaps : 0);
            var tex = _gd.Factory.CreateTexture(new GpuTextureDescription(w, h, GpuPixelFormat.R8G8B8A8UNorm, usage, mips));
            _gd.UpdateTexture(tex, rgba, 0, 0, w, h);
            if (mips > 1)
            {
                using var cl = _gd.Factory.CreateCommandList();
                cl.Begin();
                cl.GenerateMipmaps(tex);
                cl.End();
                _gd.Submit(cl);
                _gd.WaitForIdle();
            }
            _textures.Add(tex);
            return new TextureHandle(_textures.Count - 1);
        }

        /// <summary>Upload a 5-layer splat-terrain material: two texture arrays (albedo + tangent-space normal, one
        /// layer per <see cref="SplatLayerImage"/>, all the same <paramref name="width"/> x <paramref name="height"/>
        /// RGBA8), with full mip chains generated, plus a params UBO (per-layer tint/tiling/roughness + triplanar
        /// sharpness + projection + base specular). Returns a handle to draw meshes through the splat pipeline. The
        /// material is owned by the scene and freed in <see cref="Dispose"/> (or <see cref="UnloadSplatMaterial"/>);
        /// it is shared across every mesh that references it (e.g. all terrain chunks).</summary>
        public SplatMaterialHandle LoadSplatMaterial(int width, int height, IReadOnlyList<SplatLayerImage> layers,
            float triplanarSharpness = 8f, SplatProjection projection = SplatProjection.Triplanar, float baseSpecStrength = 0.15f,
            TerrainSamplerConfig? sampler = null)
        {
            if (layers.Count != SplatMaterialConfig.LayerCount)
                throw new ArgumentException($"a splat material needs exactly {SplatMaterialConfig.LayerCount} layers, got {layers.Count}.", nameof(layers));
            var f = _gd.Factory;
            uint w = (uint)width, h = (uint)height, mips = SplatMaterialConfig.MipLevelCount(width, height);
            const GpuTextureUsage usage = GpuTextureUsage.Sampled | GpuTextureUsage.GenerateMipmaps;
            var albedo = f.CreateTexture(GpuTextureDescription.Texture2DArray(w, h, GpuPixelFormat.R8G8B8A8UNorm, usage, (uint)layers.Count, mips));
            var normal = f.CreateTexture(GpuTextureDescription.Texture2DArray(w, h, GpuPixelFormat.R8G8B8A8UNorm, usage, (uint)layers.Count, mips));
            for (int L = 0; L < layers.Count; L++)
            {
                _gd.UpdateTexture(albedo, layers[L].AlbedoRgba, 0, 0, w, h, mipLevel: 0, arrayLayer: (uint)L);
                _gd.UpdateTexture(normal, layers[L].NormalRgba, 0, 0, w, h, mipLevel: 0, arrayLayer: (uint)L);
            }
            // Generate both mip chains in one transient command list.
            using var cl = f.CreateCommandList();
            cl.Begin();
            cl.GenerateMipmaps(albedo);
            cl.GenerateMipmaps(normal);
            cl.End();
            _gd.Submit(cl);
            _gd.WaitForIdle();

            var data = SplatMaterialConfig.BuildParams(layers, triplanarSharpness, projection, baseSpecStrength);
            // Combined UBO: frame uniforms (re-synced each frame in the splat pass) + these params appended. One
            // uniform buffer for the whole splat pipeline (Metal mis-binds a second UBO; see ModelRenderer).
            var ubo = _model.CreateSplatParamsUbo(in data);

            // A material that overrides the sampler gets its own (owned, disposed with the material); otherwise the
            // set binds the renderer's shared default sampler and nothing extra is owned here.
            IGpuSampler? ownedSampler = sampler.HasValue ? _model.CreateTerrainSampler(sampler.Value) : null;
            var set = ownedSampler is null
                ? _model.CreateSplatMaterialSet(ubo, albedo, normal)
                : _model.CreateSplatMaterialSet(ubo, albedo, normal, ownedSampler);
            _splatMaterials.Add(new SplatMaterialEntry(albedo, normal, ubo, set, ownedSampler));
            return new SplatMaterialHandle(_splatMaterials.Count - 1);
        }

        /// <summary>Upload a glTF material's auto-read <see cref="GltfMaterialMaps"/> (from
        /// <see cref="GltfLoader.LoadWithMaterial"/> / <see cref="GltfLoader.LoadSkinnedWithMaterial"/>) into a
        /// <see cref="SurfaceMaps"/>: one <see cref="LoadTexture(byte[],int,int)"/> per present map. An absent map
        /// stays a <c>default</c> handle (the renderer falls back to its default for that slot - white albedo, flat
        /// normal, zero roughness), so an all-absent <paramref name="maps"/> yields an all-default
        /// <see cref="SurfaceMaps"/>. The uploaded textures are owned by the scene and freed in
        /// <see cref="Dispose"/>. Pass the result to <see cref="LoadMesh(GltfMesh,SurfaceMaps)"/> /
        /// <see cref="LoadSkinnedMesh(SkinnedGltfMesh,SurfaceMaps)"/>.</summary>
        public SurfaceMaps LoadSurfaceMaps(GltfMaterialMaps maps)
        {
            TextureHandle Upload(DecodedImage? img) =>
                img is { } i ? LoadTexture(i.Rgba, i.Width, i.Height) : default;
            return new SurfaceMaps(Upload(maps.Albedo), Upload(maps.Normal), Upload(maps.Roughness));
        }

        /// <summary>Opt-in convenience: upload a mesh and bind a glTF material's auto-read
        /// <see cref="GltfMaterialMaps"/> in one call - equivalent to
        /// <c>LoadMesh(mesh, LoadSurfaceMaps(maps))</c>. Absent maps fall back to the renderer defaults; an
        /// all-absent <paramref name="maps"/> loads the mesh untextured.</summary>
        public MeshHandle LoadMesh(GltfMesh mesh, GltfMaterialMaps maps) => LoadMesh(mesh, LoadSurfaceMaps(maps));

        /// <summary>Opt-in convenience: upload a skinned mesh and bind a glTF material's auto-read
        /// <see cref="GltfMaterialMaps"/> in one call - equivalent to
        /// <c>LoadSkinnedMesh(mesh, LoadSurfaceMaps(maps))</c>.</summary>
        public SkinnedMeshHandle LoadSkinnedMesh(SkinnedGltfMesh mesh, GltfMaterialMaps maps) =>
            LoadSkinnedMesh(mesh, LoadSurfaceMaps(maps));

        /// <summary>
        /// Free the GPU buffers backing <paramref name="h"/> and release its slot for reuse. A <c>default</c>
        /// handle is a no-op. A stale or bogus handle (its generation no longer matches the slot, e.g. a
        /// double-free) throws <see cref="ArgumentException"/>.
        /// </summary>
        public void UnloadMesh(MeshHandle h)
        {
            if (h.Generation == 0) return;          // default handle: no-op
            _slots.Free(h.Index, h.Generation);     // throws on stale/invalid
            var m = _meshes[h.Index];
            // Dispose the per-mesh material set alongside the buffers, but NOT the texture: it is owned in
            // _textures and may be shared by other meshes (freed in Dispose).
            if (m is { } mesh) { mesh.Vb.Dispose(); mesh.Ib.Dispose(); mesh.MaterialSet?.Dispose(); }
            _meshes[h.Index] = null;
        }

        /// <summary>Free a splat-terrain material's GPU resources (its texture arrays, params UBO, resource set) and
        /// release its slot. A <c>default</c>/Invalid handle is a no-op. Meshes still referencing it must be unloaded
        /// first (they hold no reference after this).</summary>
        public void UnloadSplatMaterial(SplatMaterialHandle h)
        {
            if (!h.IsValid) return;
            var m = _splatMaterials[h.ListIndex];
            m?.Dispose();
            _splatMaterials[h.ListIndex] = null;
        }

        /// <summary>Diagnostic: read one mip level (and array layer) of a splat material's ALBEDO texture array back
        /// to the CPU as packed RGBA8; <paramref name="width"/>/<paramref name="height"/> receive that mip's own
        /// dimensions. Lets a game/test verify the generated mip chain on a real device - e.g. whether a high mip is
        /// a real blurred downsample (its average colour matches mip 0, low detail) versus a copy of mip 0 (still
        /// detailed) or empty (near-black), which is how a broken GPU mip generation shows up. Requires a mappable
        /// device; not on the per-frame path.</summary>
        public byte[] DebugReadSplatAlbedoMip(SplatMaterialHandle h, int mipLevel, int arrayLayer, out int width, out int height)
        {
            if (!h.IsValid) throw new ArgumentException("splat material handle is Invalid.", nameof(h));
            var m = _splatMaterials[h.ListIndex] ?? throw new ArgumentException("splat material is not loaded (already unloaded).", nameof(h));
            var tex = m.AlbedoArray;
            if (mipLevel < 0 || (uint)mipLevel >= tex.MipLevels)
                throw new ArgumentOutOfRangeException(nameof(mipLevel), $"mip {mipLevel} out of range (texture has {tex.MipLevels} levels).");
            if (arrayLayer < 0)
                throw new ArgumentOutOfRangeException(nameof(arrayLayer));
            width = Math.Max(1, (int)tex.Width >> mipLevel);
            height = Math.Max(1, (int)tex.Height >> mipLevel);
            return GpuReadback.ToRgbaMip(_gd, tex, (uint)mipLevel, (uint)arrayLayer, width, height);
        }

        /// <summary>Free the GPU texture backing <paramref name="h"/> (and its lazily-created textured-billboard
        /// resource set) and null its slot. A <c>default</c>/Invalid handle is a no-op; unloading an
        /// already-unloaded slot is also a no-op. The slot is NOT recycled, so handles stay stable. Because a
        /// texture can be shared by several meshes/materials, the scene can't know who else references it - any mesh
        /// still bound to this texture must be unloaded first or simply not drawn afterwards (mirrors
        /// <see cref="UnloadSplatMaterial"/>). Without this, textures only free at <see cref="Dispose"/>, so a
        /// long-lived scene that streams or reloads textured assets leaks one native texture per load.</summary>
        public void UnloadTexture(TextureHandle h)
        {
            if (!h.IsValid) return;
            int i = h.ListIndex;
            _textures[i]?.Dispose();
            _textures[i] = null;
            if (i < _texBillboardSets.Count) { _texBillboardSets[i]?.Dispose(); _texBillboardSets[i] = null; }
        }

        /// <summary>Number of texture slots still holding a live GPU texture (loaded and not yet unloaded). For tests.</summary>
        internal int LiveTextureCount
        {
            get { int n = 0; foreach (var t in _textures) if (t != null) n++; return n; }
        }

        /// <summary>Mip-level count of the GPU texture backing <paramref name="h"/> (0 if the slot is empty). For tests
        /// (guards the mip-chain invariant that keeps distant model/prop surfaces from aliasing).</summary>
        internal uint MipLevelsOf(TextureHandle h) => h.IsValid ? _textures[h.ListIndex]?.MipLevels ?? 0u : 0u;

        /// <summary>Number of mesh slots still holding a live GPU mesh (loaded and not yet unloaded). For tests
        /// (e.g. a streaming sink's teardown must return this to its pre-load baseline - no leaked chunk meshes).</summary>
        internal int LiveMeshCount
        {
            get { int n = 0; foreach (var m in _meshes) if (m != null) n++; return n; }
        }

        /// <summary>Number of splat-material slots still holding a live material (loaded and not yet unloaded). For tests.</summary>
        internal int LiveSplatMaterialCount
        {
            get { int n = 0; foreach (var m in _splatMaterials) if (m != null) n++; return n; }
        }

        /// <summary>Upload a skinned mesh to the GPU once; returns a handle to draw it with
        /// <see cref="DrawSkinned(KhaozEngine.Render3D.SkinnedMeshHandle, System.ReadOnlySpan{System.Numerics.Matrix4x4}, System.Numerics.Matrix4x4, KhaozEngine.Primitives.Color)"/>. Untextured (samples the 1x1 white default, so colour is the baked vertex
        /// colour times any per-instance tint).</summary>
        public SkinnedMeshHandle LoadSkinnedMesh(SkinnedGltfMesh mesh) => LoadSkinnedInternal(mesh, null);

        /// <summary>Upload a skinned mesh and bind <paramref name="texture"/> as its albedo
        /// (<c>texRgb * vColor * vTint</c>). An invalid handle falls back to untextured.</summary>
        public SkinnedMeshHandle LoadSkinnedMesh(SkinnedGltfMesh mesh, TextureHandle texture)
        {
            IGpuResourceSet? material = texture.IsValid ? _model.CreateMaterialSet(_textures[texture.ListIndex]!) : null;
            return LoadSkinnedInternal(mesh, material);
        }

        /// <summary>Upload a skinned mesh and bind a full PBR-lite material (<paramref name="maps"/>): albedo +
        /// optional normal + optional roughness, mirroring <see cref="LoadMesh(GltfMesh,SurfaceMaps)"/>. Invalid
        /// handles fall back to the renderer defaults (white albedo / flat normal / zero roughness). Normal
        /// perturbation requires the mesh to carry tangents - skinned glTF via <see cref="GltfLoader.LoadSkinned"/>
        /// or <see cref="SkinnedMeshBuilder"/> output both compute them; a tangent-less skinned vertex is lit by its
        /// geometric normal. The tangent rides the per-frame CPU skin deform so the TBN tracks the pose.</summary>
        public SkinnedMeshHandle LoadSkinnedMesh(SkinnedGltfMesh mesh, SurfaceMaps maps)
        {
            IGpuTexture? a = maps.Albedo.IsValid ? _textures[maps.Albedo.ListIndex] : null;
            IGpuTexture? n = maps.Normal.IsValid ? _textures[maps.Normal.ListIndex] : null;
            IGpuTexture? r = maps.Roughness.IsValid ? _textures[maps.Roughness.ListIndex] : null;
            IGpuResourceSet? material = (a != null || n != null || r != null)
                ? _model.CreateMaterialSet(a, n, r)
                : null;
            return LoadSkinnedInternal(mesh, material);
        }

        SkinnedMeshHandle LoadSkinnedInternal(SkinnedGltfMesh mesh, IGpuResourceSet? material)
        {
            var f = _gd.Factory;
            var vb = f.CreateBuffer(new GpuBufferDescription((uint)(mesh.Vertices.Length * SkinnedVertex.SizeInBytes), GpuBufferUsage.VertexBuffer));
            _gd.UpdateBuffer(vb, 0, mesh.Vertices);
            var ib = CreateIndexBuffer(mesh.Indices32, mesh.IndexFormat);

            int index = _skinnedSlots.Alloc(out int generation);
            var entry = new SkinnedMeshEntry(vb, ib, mesh.Indices32.Length, mesh.IndexFormat, material, mesh.InverseBind);
            // Cache the source vertices (parallel to _skinnedMeshes) for per-frame CPU skinning - no GPU readback.
            if (index < _skinnedMeshes.Count) { _skinnedMeshes[index] = entry; _skinnedCpuVerts[index] = mesh.Vertices; }
            else { _skinnedMeshes.Add(entry); _skinnedCpuVerts.Add(mesh.Vertices); }
            return new SkinnedMeshHandle(index, generation);
        }

        /// <summary>Queue one skinned draw. <paramref name="boneMatrices"/> are this frame's joint world
        /// transforms (model space), one per bone in the mesh's skin; the engine composes them with the mesh's
        /// inverse-bind. Passing the mesh's <see cref="SkinnedGltfMesh.RestPose"/> yields no deformation.
        /// Presentation only - never feed sim/RNG/netcode from bone state.</summary>
        public void DrawSkinned(SkinnedMeshHandle h, ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 model, Color tint)
            => DrawSkinned(h, boneMatrices, model, tint, Material.None);

        /// <summary>As <see cref="DrawSkinned(SkinnedMeshHandle,ReadOnlySpan{Matrix4x4},Matrix4x4,Color)"/> with an
        /// explicit <paramref name="material"/> (emissive + specular).</summary>
        public void DrawSkinned(SkinnedMeshHandle h, ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 model, Color tint, Material material)
        {
            if (!_skinnedSlots.IsValid(h.Index, h.Generation)) return;
            var entry = _skinnedMeshes[h.Index];
            if (entry is null) return;
            // This draw's bones go into slot N (N = its submission index), padded to the per-draw window so the
            // dynamic-offset bind selects exactly this draw's palette. Slot N maps to bone byte offset
            // N * SlotBytes and to instance buffer element N in the render loop.
            int slot = _skinnedInstances.Items.Count;
            ComposeBonesIntoSlot(_boneMatrices, slot, boneMatrices, entry.InverseBind);
            _skinnedInstances.Add(h, model, tint, material);
        }

        /// <summary>Free a skinned mesh's GPU buffers and release its slot. A <c>default</c> handle is a no-op; a
        /// stale handle throws.</summary>
        public void UnloadSkinnedMesh(SkinnedMeshHandle h)
        {
            if (h.Generation == 0) return;
            _skinnedSlots.Free(h.Index, h.Generation);
            var m = _skinnedMeshes[h.Index];
            if (m is { } e) { e.Vb.Dispose(); e.Ib.Dispose(); e.MaterialSet?.Dispose(); }
            _skinnedMeshes[h.Index] = null;
            _skinnedCpuVerts[h.Index] = null;
        }

        /// <summary>Skinned draws queued this frame. Internal: lets tests assert Begin clears the queue.</summary>
        internal int SkinnedInstanceCount => _skinnedInstances.Items.Count;

        /// <summary>Start a frame: clear the instance queue, the point-light queue, the debug-line queue, the
        /// filled-overlay queue, and the billboard queues. Call before submitting.</summary>
        public void Begin()
        {
            _instances.Begin();
            _skinnedInstances.Begin();
            _boneMatrices.Clear();
            _lights.Clear();
            _lineVerts.Clear();
            _fillVerts.Clear();
            _decals.Clear();
            _overlayMeshDraws.Clear();
            _billboardAlpha.Clear();
            _billboardAdditive.Clear();
            _texBillboardItems.Clear();
            _beamItems.Clear();
            _billboardBasisValid = false;
        }

        /// <summary>Queue one instance: draw <paramref name="mesh"/> at world transform <paramref name="world"/> (no tint).</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world) => _instances.Add(mesh, world, Color.White);

        /// <summary>Queue one instance with a per-instance RGBA <paramref name="tint"/> that multiplies the lit color.</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world, Color tint) => _instances.Add(mesh, world, tint);

        /// <summary>Queue one instance with a per-instance <paramref name="tint"/> and <paramref name="material"/>
        /// (emissive glow + specular).</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world, Color tint, Material material) => _instances.Add(mesh, world, tint, material);

        // ---- Dynamic point/effect lights (muzzle flashes, explosions, thrusters, key projectiles). ----

        /// <summary>
        /// Queue a dynamic point light at <paramref name="worldPos"/> for this frame: it adds diffuse (and cheap
        /// specular) to the lit mesh pass, on top of the global key+fill+ambient term, falling smoothly to zero at
        /// <paramref name="radius"/> world units and scaled by <paramref name="intensity"/>. <paramref name="color"/>
        /// is the light's RGB (alpha ignored). Cleared each <see cref="Begin"/> like the instance queue.
        /// </summary>
        /// <remarks>
        /// Presentation only - never feed simulation/collision state from a light. Only the first
        /// <see cref="MaxPointLights"/> lights queued in a frame are uploaded (extras are dropped); pick the
        /// N nearest to the camera/action per frame so a dense scene stays within the GPU budget. Zero lights ==
        /// the historical key+fill+ambient render, bit-identical.
        /// </remarks>
        public void AddLight(Vector3 worldPos, Color color, float radius, float intensity)
        {
            Vector4 c = color;
            _lights.Add(new ModelRenderer.PointLightData
            {
                PosRadius = new Vector4(worldPos, radius),
                ColorIntensity = new Vector4(c.X, c.Y, c.Z, intensity),
            });
        }

        /// <summary>Count of point lights queued this frame (before the renderer's <see cref="MaxPointLights"/>
        /// clamp). Internal: lets tests assert <see cref="Begin"/> clears the queue and <see cref="AddLight"/>
        /// enqueues.</summary>
        internal int LightCount => _lights.Count;

        // ---- Debug line overlay (immediate-mode; queued this frame, drawn on top after post). ----

        /// <summary>Queue a single debug line from <paramref name="a"/> to <paramref name="b"/> in colour
        /// <paramref name="color"/> (RGBA). Cleared in <see cref="Begin"/>; drawn over the post image.</summary>
        public void DebugLine(Vector3 a, Vector3 b, Color color)
        {
            _lineVerts.Add(new LineRenderer.LineVertex(a, color));
            _lineVerts.Add(new LineRenderer.LineVertex(b, color));
        }

        /// <summary>Queue a ray from <paramref name="origin"/> along <paramref name="direction"/> for
        /// <paramref name="length"/> units.</summary>
        public void DebugRay(Vector3 origin, Vector3 direction, float length, Color color)
        {
            if (direction.LengthSquared() < 1e-12f) return;   // degenerate direction: nothing to draw
            DebugLine(origin, origin + Vector3.Normalize(direction) * length, color);
        }

        /// <summary>Queue the 12 edges of an axis-aligned box centred at <paramref name="center"/> with full
        /// extents <paramref name="size"/>.</summary>
        public void DebugBox(Vector3 center, Vector3 size, Color color)
        {
            _scratch.Clear();
            DebugShapes.Box(_scratch, center, size);
            AppendScratch(color);
        }

        /// <summary>Queue an XZ-plane grid through <paramref name="center"/>.Y: <c>cells+1</c> lines each way,
        /// spanning <c>cells*cellSize</c>.</summary>
        public void DebugGrid(Vector3 center, float cellSize, int cells, Color color)
        {
            _scratch.Clear();
            DebugShapes.Grid(_scratch, center, cellSize, cells);
            AppendScratch(color);
        }

        /// <summary>Queue 3 axis lines from <paramref name="origin"/> (X red, Y green, Z blue), each
        /// <paramref name="scale"/> long.</summary>
        public void DebugAxes(Vector3 origin, float scale)
        {
            DebugLine(origin, origin + new Vector3(scale, 0, 0), new Color(1f, 0.2f, 0.2f, 1f));
            DebugLine(origin, origin + new Vector3(0, scale, 0), new Color(0.2f, 1f, 0.2f, 1f));
            DebugLine(origin, origin + new Vector3(0, 0, scale), new Color(0.3f, 0.5f, 1f, 1f));
        }

        /// <summary>Queue a circle of <paramref name="segments"/> segments at <paramref name="radius"/> from
        /// <paramref name="center"/> in the plane perpendicular to <paramref name="normal"/>
        /// (use <see cref="Vector3.UnitY"/> for a ground ring).</summary>
        public void DebugCircle(Vector3 center, Vector3 normal, float radius, Color color, int segments = 32)
        {
            _scratch.Clear();
            DebugShapes.Circle(_scratch, center, normal, radius, segments);
            AppendScratch(color);
        }

        readonly List<Vector3> _scratch = new();

        void AppendScratch(Vector4 color)
        {
            foreach (var p in _scratch)
                _lineVerts.Add(new LineRenderer.LineVertex(p, color));
        }

        // ---- Filled (alpha-blended) overlay: flat, world-space translucent shapes painted on a plane (ground
        //      tiles, range/zone/AoE highlights). Queued this frame, drawn after post UNDER the debug lines so an
        //      outline reads crisp on top of a fill. The mesh pass is opaque, so a tinted plane mesh can't blend;
        //      these live here in the overlay pass. ----

        readonly List<Vector3> _fillScratch = new();

        /// <summary>Queue a flat translucent quad centred at <paramref name="center"/>, lying in the plane with the
        /// given <paramref name="normal"/>, its first in-plane axis along <paramref name="uAxis"/>.
        /// <paramref name="halfExtents"/>.X scales that axis, .Y the perpendicular one. <paramref name="color"/> is
        /// RGBA; its alpha is blended over the post image. Cleared in <see cref="Begin"/>; drawn under the debug
        /// lines.</summary>
        public void DebugFilledQuad(Vector3 center, Vector3 normal, Vector3 uAxis, Vector2 halfExtents, Color color)
        {
            _fillScratch.Clear();
            DebugFillShapes.FilledQuad(_fillScratch, center, normal, uAxis, halfExtents);
            AppendFillScratch(color);
        }

        /// <summary>Queue a flat translucent quad on the XZ ground plane (normal +Y, u axis +X) centred at
        /// <paramref name="center"/>, with the given <paramref name="halfExtents"/> (X along world X, Y along world
        /// Z) and RGBA <paramref name="color"/>.</summary>
        public void DebugFilledQuad(Vector3 center, Vector2 halfExtents, Color color) =>
            DebugFilledQuad(center, Vector3.UnitY, Vector3.UnitX, halfExtents, color);

        /// <summary>Queue a square translucent ground tile centred at <paramref name="center"/> on the XZ plane,
        /// half a <paramref name="halfSize"/> across each way, in RGBA <paramref name="color"/>. The board-tile
        /// convenience (range/coverage/AoE highlights).</summary>
        public void DebugFilledQuad(Vector3 center, float halfSize, Color color) =>
            DebugFilledQuad(center, Vector3.UnitY, Vector3.UnitX, new Vector2(halfSize, halfSize), color);

        /// <summary>Queue a flat translucent disc of <paramref name="segments"/> triangles at
        /// <paramref name="radius"/> from <paramref name="center"/>, in the plane perpendicular to
        /// <paramref name="normal"/> (use <see cref="Vector3.UnitY"/> for a ground disc), in RGBA
        /// <paramref name="color"/>.</summary>
        public void DebugFilledCircle(Vector3 center, Vector3 normal, float radius, Color color, int segments = 32)
        {
            _fillScratch.Clear();
            DebugFillShapes.FilledCircle(_fillScratch, center, normal, radius, segments);
            AppendFillScratch(color);
        }

        /// <summary>Queue a flat translucent triangle fan from <paramref name="center"/> out to an arbitrary,
        /// already-ordered boundary <paramref name="rim"/> (e.g. a turret's star-shaped line-of-sight area), in RGBA
        /// <paramref name="color"/>. When <paramref name="closed"/> (the default) the loop is sealed with a wrap
        /// triangle (center, rim[last], rim[0]); pass <c>false</c> for an open arc. Wind the rim CCW about the
        /// desired facing normal (use <see cref="Vector3.UnitY"/> for a ground fan, as with
        /// <see cref="DebugFilledCircle"/>). Cleared in <see cref="Begin"/>; drawn under the debug lines.</summary>
        public void DebugFilledFan(Vector3 center, IReadOnlyList<Vector3> rim, Color color, bool closed = true)
        {
            _fillScratch.Clear();
            DebugFillShapes.FilledFan(_fillScratch, center, rim, closed);
            AppendFillScratch(color);
        }

        void AppendFillScratch(Vector4 color)
        {
            foreach (var p in _fillScratch)
                _fillVerts.Add(new FillRenderer.FillVertex(p, color));
        }

        /// <summary>Count of queued filled-overlay vertices this frame (3 per triangle). Internal: lets tests
        /// assert <see cref="Begin"/> clears the queue and the builders queue the expected geometry.</summary>
        internal int FillVertexCount => _fillVerts.Count;

        /// <summary>Queue one generic shaped ground decal for this frame (painted onto the ground/terrain via the
        /// depth buffer, under the meshes, through the post chain). Presentation only; cleared in <see cref="Begin"/>.
        /// The telegraph wrappers build these from a style + progress.</summary>
        public void DrawGroundDecal(in GroundDecal decal) => _decals.Add(decal);

        /// <summary>Count of ground decals queued this frame. Internal: lets tests assert <see cref="Begin"/> clears
        /// the queue and <see cref="DrawGroundDecal"/> enqueues.</summary>
        internal int DecalCount => _decals.Count;

        /// <summary>Queue a translucent, UNLIT, depth-TESTED (not depth-writing) overlay draw of an already-loaded
        /// <paramref name="mesh"/> at world transform <paramref name="world"/> for this frame. The mesh's own
        /// per-vertex <see cref="ModelVertex.Color"/> (RGBA) supplies the colour and alpha, alpha-blended over the
        /// scene. It is occluded by nearer scene geometry (depth test) but never writes depth, so it never hides the
        /// scene. Drawn after the meshes/beams and before the pixel post, so it flows through the post chain like the
        /// rest of the model pass. A reusable overlay primitive: the collision-shape overlay is the first consumer;
        /// nav / AoI / chunk-bounds layers reuse it. Presentation only; cleared in <see cref="Begin"/>.
        /// Because depth-write is off, overlapping overlay meshes blend in submission order: there is no
        /// per-fragment depth sorting between overlay draws.</summary>
        public void DrawOverlayMesh(MeshHandle mesh, Matrix4x4 world) => _overlayMeshDraws.Add((mesh, world));

        /// <summary>Count of overlay-mesh draws queued this frame. Internal: lets tests assert <see cref="Begin"/>
        /// clears the queue and <see cref="DrawOverlayMesh"/> enqueues.</summary>
        internal int OverlayMeshDrawCount => _overlayMeshDraws.Count;

        // ---- Camera-facing billboard overlay (immediate-mode; queued this frame, drawn on top after lines). ----

        /// <summary>Queue a camera-facing soft-disc billboard centred at <paramref name="worldPos"/> with half-size
        /// <paramref name="size"/> (the quad spans 2*size across), tinted by <paramref name="color"/> (RGBA), using
        /// the given <paramref name="blend"/>. Cleared in <see cref="Begin"/>; drawn over the post image and the
        /// debug lines. The game loops its particle system's <c>Active</c> span and calls this per particle.</summary>
        public void DrawBillboard(Vector3 worldPos, float size, Color color, BillboardBlend blend = BillboardBlend.Alpha)
        {
            // Camera basis is constant across a frame's billboards; compute it once (on the first call) and reuse.
            if (!_billboardBasisValid)
            {
                BillboardGeometry.CameraBasis(ActiveCamera.Forward, out _billboardRight, out _billboardUp);
                _billboardBasisValid = true;
            }
            Span<Vector3> pos = stackalloc Vector3[6];
            Span<Vector2> uv = stackalloc Vector2[6];
            BillboardGeometry.Triangles(worldPos, size, _billboardRight, _billboardUp, pos, uv);

            var list = blend == BillboardBlend.Additive ? _billboardAdditive : _billboardAlpha;
            for (int i = 0; i < 6; i++)
                list.Add(new BillboardRenderer.BillboardVertex(pos[i], uv[i], color));
        }

        /// <summary>
        /// Queue a camera-facing TEXTURED billboard: a quad at <paramref name="worldPos"/> with half-size
        /// <paramref name="size"/> (spans 2*size across), sampling the sub-rect <paramref name="sourceUv"/>
        /// (<c>(u0,v0,u1,v1)</c> - bottom-left to top-right; pass <c>(0,0,1,1)</c> for the whole texture, or a frame
        /// rect for a sprite sheet) of the texture loaded as <paramref name="texture"/>, multiplied by
        /// <paramref name="tint"/> (RGBA), using <paramref name="blend"/>. Cleared in <see cref="Begin"/>.
        /// </summary>
        /// <remarks>
        /// Unlike the colour-only <see cref="DrawBillboard(Vector3,float,Color,BillboardBlend)"/> (an overlay drawn
        /// after the post chain), textured billboards draw INTO the model pass with the depth test on (no depth
        /// write): a nearer mesh occludes the quad and the quad draws over a farther mesh, so meshes and sprites
        /// interleave correctly. Depth write is off, so overlapping quads blend in SUBMISSION order - submit
        /// back-to-front for correct transparency. An invalid/<c>default</c> <paramref name="texture"/> draws
        /// nothing (no throw). Presentation only.
        /// </remarks>
        public void DrawBillboard(TextureHandle texture, Vector3 worldPos, float size, Vector4 sourceUv, Color tint,
            BillboardBlend blend = BillboardBlend.Alpha)
        {
            if (!texture.IsValid) return;   // nothing to sample: no-op, like the untextured-mesh fallback
            Vector4 c = tint;
            _texBillboardItems.Add(new TexturedBillboardItem
            {
                TexIndex = texture.ListIndex,
                Blend = blend,
                Center = worldPos,
                Size = size,
                SourceUv = sourceUv,
                Color = c,
            });
        }

        /// <summary>Queue a textured billboard sampling the WHOLE texture (source rect <c>(0,0,1,1)</c>); see
        /// <see cref="DrawBillboard(TextureHandle,Vector3,float,Vector4,Color,BillboardBlend)"/>.</summary>
        public void DrawBillboard(TextureHandle texture, Vector3 worldPos, float size, Color tint,
            BillboardBlend blend = BillboardBlend.Alpha) =>
            DrawBillboard(texture, worldPos, size, new Vector4(0f, 0f, 1f, 1f), tint, blend);

        /// <summary>Count of textured billboards queued this frame. Internal: lets tests assert
        /// <see cref="Begin"/> clears the queue and the overloads enqueue.</summary>
        internal int TexturedBillboardCount => _texBillboardItems.Count;

        // ---- Glowing beams (lasers/thrusters/tethers): a camera-facing strip a->b, additive, depth-interleaved
        //      into the model pass so geometry occludes it. Soft core+halo + optional taper/pulse/scroll in the
        //      fragment shader; animation reads EffectTimeSeconds. ----

        /// <summary>
        /// Queue an additive glowing beam from <paramref name="a"/> to <paramref name="b"/> (world points),
        /// <paramref name="width"/> world units across (the quad spans <paramref name="width"/>, i.e. ±width/2 from
        /// the axis), tinted by <paramref name="color"/> (the core colour unless <paramref name="style"/> overrides
        /// it). A camera-facing strip with a bright core + soft halo; optional end taper and time-driven pulse/scroll
        /// come from <paramref name="style"/> (null =&gt; <see cref="BeamStyle.Default"/>) and
        /// <see cref="EffectTimeSeconds"/>. Drawn INTO the model pass with the depth test on (no write), like the
        /// textured billboard, so a nearer mesh occludes the beam. Cleared in <see cref="Begin"/>. A degenerate beam
        /// (<paramref name="a"/>≈<paramref name="b"/> or <paramref name="width"/> &lt;= 0) is a silent no-op.
        /// Presentation only.
        /// </summary>
        public void DrawBeam(Vector3 a, Vector3 b, float width, Color color, BeamStyle? style = null)
        {
            if (width <= 0f || (b - a).LengthSquared() < 1e-12f) return;   // degenerate: nothing to draw
            BeamStyle s = style ?? BeamStyle.Default;
            Vector4 core = s.CoreColor ?? color;
            Vector4 glow = s.GlowColor is Color g ? g : new Vector4(core.X, core.Y, core.Z, core.W * 0.4f);
            _beamItems.Add(new BeamItem
            {
                A = a, B = b, Width = width,
                CoreColor = core,
                GlowColor = glow,
                Shape = new Vector4(s.CoreFraction, s.GlowSoftness, s.Taper, 0f),
                Anim = new Vector4(s.PulseSpeed, s.PulseAmount, s.ScrollSpeed, 0f),
            });
        }

        /// <summary>Count of beams queued this frame. Internal: lets tests assert <see cref="Begin"/> clears the
        /// queue and <see cref="DrawBeam"/> enqueues.</summary>
        internal int BeamCount => _beamItems.Count;

        /// <summary>The beams queued this frame (resolved colours/params). Internal: lets tests assert colour
        /// resolution.</summary>
        internal IReadOnlyList<BeamItem> BeamItems => _beamItems;

        // Current internal render-target size (physical pixels). Exposed for tests to assert MatchViewport resizes
        // and FixedInternal stays put; not part of the public surface.
        internal int RenderTargetWidth => _res.Width;
        internal int RenderTargetHeight => _res.Height;

        /// <summary>
        /// The internal render-target size for a given post config + viewport. <see cref="RenderScale.FixedInternal"/>
        /// returns <see cref="PixelPostProcessSettings.RenderWidth"/>/<c>RenderHeight</c> unchanged (the historical
        /// path). <see cref="RenderScale.MatchViewport"/> tracks the viewport, clamped to
        /// <see cref="PixelPostProcessSettings.MaxRenderWidth"/>/<c>MaxRenderHeight</c> with aspect preserved, each
        /// dimension at least 1. Pure + headless-testable (no GPU). Stable once the viewport is at/over the cap for a
        /// fixed aspect, so <see cref="EnsureSize"/> doesn't thrash.
        /// </summary>
        internal static (int W, int H) ComputeTargetSize(PixelPostProcessSettings s, int viewportW, int viewportH)
        {
            if (s.RenderScale == RenderScale.FixedInternal)
                return (s.RenderWidth, s.RenderHeight);

            // MatchViewport: render at the framebuffer size x the supersample factor (SSAA), capped
            // (aspect-preserving downscale) so a huge window / big factor doesn't allocate an unbounded target.
            // Guard against a zero/negative viewport during startup/minimise.
            float ss = MathF.Max(1f, s.Supersample);
            int vw = Math.Max(1, (int)MathF.Round(Math.Max(1, viewportW) * ss));
            int vh = Math.Max(1, (int)MathF.Round(Math.Max(1, viewportH) * ss));
            int maxW = Math.Max(1, s.MaxRenderWidth);
            int maxH = Math.Max(1, s.MaxRenderHeight);
            if (vw <= maxW && vh <= maxH) return (vw, vh);
            float scale = ViewportMath.Fit(vw, vh, maxW, maxH);
            int w = Math.Max(1, (int)MathF.Round(vw * scale));
            int h = Math.Max(1, (int)MathF.Round(vh * scale));
            return (w, h);
        }

        void EnsureSize(int viewportW, int viewportH)
        {
            var (tw, th) = ComputeTargetSize(Post, viewportW, viewportH);
            if (_res.Width != tw || _res.Height != th)
            {
                _res.Resize(tw, th);
                _post.BindTargets(_res);
            }
            // Aspect uses the true viewport (the post target is blit-stretched to fill it), not the clamped target.
            Camera.AspectRatio = viewportH > 0 ? (float)viewportW / viewportH : Camera.AspectRatio;
        }

        /// <summary>
        /// Record the scene (model pass over all queued instances -> post chain -> blit) into
        /// <paramref name="cl"/>, ending on <paramref name="target"/>. The caller owns Begin/End/Submit of
        /// <paramref name="cl"/>. <paramref name="viewportW"/>/<paramref name="viewportH"/> are the target size.
        /// </summary>
        internal void RenderInternal(IGpuCommandList cl, int viewportW, int viewportH, IGpuFramebuffer target)
        {
            EnsureSize(viewportW, viewportH);
            // Edge pass needs the camera's depth convention (perspective vs ortho + near/far) to linearize depth
            // under perspective; derived from the projection matrix so no camera-interface change is required.
            var camDepth = Internal.OutlineMath.ExtractCameraDepth(ActiveCamera.Projection);
            _post.PrepareUniforms(cl, _res, Post, camDepth);

            _model.BeginModelPass(cl, _res, Post);
            Matrix4x4 vp = ActiveCamera.ViewProjection;
            Vector3 eye = ActiveCamera.Eye;
            _model.SetFrameUniforms(cl, vp, eye, Post, CollectionsMarshal.AsSpan(_lights));
            _model.BindPass(cl);

            // GPU instancing: group queued instances by mesh into a flat instance array (ordered by mesh) + a
            // run per unique mesh. Reuses member buffers (cleared, not realloc) to stay per-frame alloc-free.
            GroupInstances(_instances.Items, _instanceData, _runs);
            if (_instanceData.Count > 0)
            {
                _model.UploadInstances(cl, CollectionsMarshal.AsSpan(_instanceData));
                foreach (var run in _runs)
                {
                    // Skip a run whose mesh was unloaded (stale handle): a destroyed entity may linger a frame.
                    // The instance data was uploaded contiguously, so skipping a run just leaves its slice undrawn.
                    if (!_slots.IsValid(run.Mesh.Index, run.Mesh.Generation)) continue;
                    var m = _meshes[run.Mesh.Index];
                    if (m is not { } mesh) continue;
                    if (mesh.SplatMaterial >= 0) continue;   // drawn in the splat pass below
                    _model.DrawMeshInstanced(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, run.Start, run.Count, mesh.MaterialSet);
                }
                // Splat-terrain pass: same uploaded instance buffer, the dedicated 5-layer texture-array pipeline.
                // Each material's combined UBO holds frame + params in one buffer, so re-sync this frame's uniforms
                // into every loaded material's UBO before drawing (usually one terrain material).
                for (int i = 0; i < _splatMaterials.Count; i++)
                    if (_splatMaterials[i] is { } syncSm) _model.WriteFrameUniformsTo(cl, syncSm.Ubo);
                bool splatBound = false;
                foreach (var run in _runs)
                {
                    if (!_slots.IsValid(run.Mesh.Index, run.Mesh.Generation)) continue;
                    var m = _meshes[run.Mesh.Index];
                    if (m is not { } mesh) continue;
                    if (mesh.SplatMaterial < 0) continue;
                    var sm = _splatMaterials[mesh.SplatMaterial];
                    if (sm is null) continue;
                    if (!splatBound) { _model.BindSplatPass(cl); splatBound = true; }
                    _model.DrawSplatMeshInstanced(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, run.Start, run.Count, sm.Set);
                }
            }

            // Skinned pass: CPU-skin each draw and route it through the no-bone model pipeline. The GPU bone-buffer
            // read corrupts past element 0 in the windowed Veldrid/Metal swapchain context (only bones[0] survives;
            // a constant bones[1] or any data-dependent index reads garbage - extensively bisected via the offscreen
            // repro), independent of buffer type / binding / dynamic offset / submit structure. The rigid model
            // pipeline (no bone read) renders cleanly in the same context, so skinned meshes are deformed on the CPU
            // here (SkinningMath.SkinVertex mirrors the shader's blend exactly) and drawn through it. _boneMatrices
            // holds each draw's slot-packed composed palette (built in DrawSkinned); deform the cached source verts
            // into one concatenated stream, upload it + the per-draw instance data, then draw each.
            var skinnedItems = _skinnedInstances.Items;
            if (skinnedItems.Count > 0)
            {
                _cpuSkinnedVerts.Clear();
                _cpuSkinnedInstances.Clear();
                _cpuSkinnedDraws.Clear();
                var boneSpan = CollectionsMarshal.AsSpan(_boneMatrices);
                const int cap = SkinningMath.MaxBonesPerDraw;
                for (int i = 0; i < skinnedItems.Count; i++)
                {
                    var it = skinnedItems[i];
                    if (!_skinnedSlots.IsValid(it.Mesh.Index, it.Mesh.Generation)) continue;
                    var entry = _skinnedMeshes[it.Mesh.Index];
                    if (entry is null) continue;
                    var src = _skinnedCpuVerts[it.Mesh.Index];
                    if (src is null) continue;
                    int baseVertex = _cpuSkinnedVerts.Count;
                    var palette = boneSpan.Slice(i * cap, cap);   // this draw's composed bone window (slot i)
                    for (int v = 0; v < src.Length; v++)
                        _cpuSkinnedVerts.Add(SkinningMath.SkinVertex(src[v], palette));
                    _cpuSkinnedInstances.Add(new ModelRenderer.InstanceData
                    {
                        Model = it.World,
                        Tint = it.Tint,
                        Emissive = it.Material.Emissive,
                        SpecParams = new Vector4(it.Material.Specular, it.Material.Shininess, 0f, 0f),
                    });
                    _cpuSkinnedDraws.Add(new CpuSkinnedDraw(entry.Ib, entry.IndexCount, entry.IndexFormat, baseVertex, entry.MaterialSet));
                }
                if (_cpuSkinnedDraws.Count > 0)
                {
                    _model.UploadCpuSkinned(cl, CollectionsMarshal.AsSpan(_cpuSkinnedVerts), CollectionsMarshal.AsSpan(_cpuSkinnedInstances));
                    _model.BindPass(cl);   // re-bind the model pipeline (the skinned draws follow the rigid run)
                    for (int d = 0; d < _cpuSkinnedDraws.Count; d++)
                    {
                        var dr = _cpuSkinnedDraws[d];
                        _model.DrawCpuSkinned(cl, dr.Ib, dr.IndexCount, dr.IndexFormat, dr.BaseVertex, (uint)d, dr.MaterialSet);
                    }
                }
            }

            // Textured billboards: drawn into the SAME model framebuffer (still bound), after the meshes, with the
            // depth test on (no write). This is what gives mesh/sprite depth interleaving; then the whole MRT
            // (meshes + textured billboards) goes through the post chain together.
            DrawTexturedBillboards(cl);

            // Beams: same model FB (still bound), after the textured billboards, before the post chain - so they
            // depth-interleave with the meshes and go through the pixel post like everything else in the model pass.
            DrawBeams(cl);

            // Overlay meshes (collision proxies etc.): after the model pass wrote depth (meshes + textured billboards
            // + beams), draw the queued translucent unlit proxies into the SAME model FB with the depth test on (no
            // write), so a proxy is occluded by nearer geometry yet blends over farther geometry, then flows through
            // the post chain with the rest of the model pass. Fully skipped when nothing is queued, so a frame with no
            // overlay draws renders byte-identical to before this pass existed.
            if (_overlayMeshDraws.Count > 0)
            {
                cl.SetFramebuffer(_res.ModelFB);
                _overlayMeshes.EnsureCapacity(_overlayMeshDraws.Count);
                _overlayMeshes.BeginFrame(GpuClip.Correct(ActiveCamera.ViewProjection, _gd.Capabilities));
                for (int i = 0; i < _overlayMeshDraws.Count; i++)
                {
                    var (handle, world) = _overlayMeshDraws[i];
                    if (!_slots.IsValid(handle.Index, handle.Generation)) continue;   // stale handle: skip
                    var m = _meshes[handle.Index];
                    if (m is not { } mesh) continue;
                    _overlayMeshes.Draw(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, i, world);
                }
            }

            // Ground decals: after the model pass wrote depth (meshes + textured billboards + beams), paint the
            // queued decals onto the reconstructed surface into ColorTex, BEFORE post - so they conform to the
            // ground, are occluded by geometry (Y-band), and flow through the pixel post like the meshes.
            if (_decals.Count > 0)
                _decalRenderer.Draw(cl, _res, ActiveCamera.ViewProjection, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_decals));

            _post.Run(cl, _res, target, Post);

            // Filled overlay: rebind `target` and draw the accumulated translucent triangles on top of the post
            // image, BEFORE the lines so an outline drawn on top of a fill reads crisp. Depth disabled + alpha
            // blend; same ActiveCamera.ViewProjection as the model pass (so fills line up with geometry and picking).
            if (_fillVerts.Count > 0)
                _fills.Draw(cl, ActiveCamera.ViewProjection, CollectionsMarshal.AsSpan(_fillVerts), target);

            // Debug overlay: rebind `target` and draw the accumulated lines on top of the post image, with
            // depth disabled and alpha blend. ActiveCamera.ViewProjection matches the model pass (unflipped, so
            // lines line up with rendered geometry and with ScreenToGround picking).
            if (_lineVerts.Count > 0)
                _lines.Draw(cl, ActiveCamera.ViewProjection, CollectionsMarshal.AsSpan(_lineVerts), target);

            // Billboards: after the line pass, additive first (glow) then alpha, same overlay framebuffer +
            // ViewProjection. Each rebinds `target` (no clear) and uploads its own vertex span.
            if (_billboardAdditive.Count > 0)
                _billboards.Draw(cl, ActiveCamera.ViewProjection, CollectionsMarshal.AsSpan(_billboardAdditive), target, additive: true);
            if (_billboardAlpha.Count > 0)
                _billboards.Draw(cl, ActiveCamera.ViewProjection, CollectionsMarshal.AsSpan(_billboardAlpha), target, additive: false);
        }

        /// <summary>Coalesce the queued textured billboards into same-(texture,blend) runs (submission order
        /// preserved), then draw each run into the model framebuffer. The model FB is still bound from the mesh
        /// pass; the depth buffer holds the meshes' depth so the quads interleave. No-op when nothing is queued.</summary>
        void DrawTexturedBillboards(IGpuCommandList cl)
        {
            if (_texBillboardItems.Count == 0) return;

            CoalesceTexturedBillboards(_texBillboardItems, _texBillboardRuns);

            // Camera basis is constant across the frame; compute once and reuse for every quad.
            BillboardGeometry.CameraBasis(ActiveCamera.Forward, out Vector3 right, out Vector3 up);
            _texBillboards.SetViewProj(cl, ActiveCamera.ViewProjection);

            Span<Vector3> pos = stackalloc Vector3[6];
            Span<Vector2> uv = stackalloc Vector2[6];
            foreach (var run in _texBillboardRuns)
            {
                _texBillboardVerts.Clear();
                for (int i = run.Start; i < run.Start + run.Count; i++)
                {
                    var it = _texBillboardItems[i];
                    BillboardGeometry.Triangles(it.Center, it.Size, right, up, it.SourceUv, pos, uv);
                    for (int v = 0; v < 6; v++)
                        _texBillboardVerts.Add(new BillboardRenderer.BillboardVertex(pos[v], uv[v], it.Color));
                }
                IGpuResourceSet set = GetTexBillboardSet(run.TexIndex);
                _texBillboards.Draw(cl, CollectionsMarshal.AsSpan(_texBillboardVerts), _res.ModelFB, set,
                    run.Blend == BillboardBlend.Additive);
            }
        }

        /// <summary>Build each queued beam's camera-facing strip (via <see cref="BeamGeometry"/>) into one vertex
        /// stream and draw them all in a single additive pass into the model FB. The model FB is still bound from
        /// the mesh pass; its depth buffer holds the meshes' depth so the beams interleave. No-op when nothing is
        /// queued.</summary>
        void DrawBeams(IGpuCommandList cl)
        {
            if (_beamItems.Count == 0) return;

            Vector3 viewDir = ActiveCamera.Forward;   // constant across the frame, matching the billboard basis
            _beamVerts.Clear();
            Span<Vector3> pos = stackalloc Vector3[6];
            Span<Vector2> uv = stackalloc Vector2[6];
            foreach (var it in _beamItems)
            {
                int n = BeamGeometry.Triangles(it.A, it.B, viewDir, it.Width, pos, uv);
                for (int v = 0; v < n; v++)
                    _beamVerts.Add(new BeamRenderer.BeamVertex(pos[v], uv[v], it.CoreColor, it.GlowColor, it.Shape, it.Anim));
            }
            if (_beamVerts.Count == 0) return;

            _beams.SetFrameUniforms(cl, ActiveCamera.ViewProjection, EffectTimeSeconds);
            _beams.Draw(cl, CollectionsMarshal.AsSpan(_beamVerts), _res.ModelFB);
        }

        /// <summary>Get (creating on first use) the textured-billboard resource set for the texture at
        /// <paramref name="texListIndex"/>. Cached parallel to <c>_textures</c>; disposed in <see cref="Dispose"/>.</summary>
        IGpuResourceSet GetTexBillboardSet(int texListIndex)
        {
            while (_texBillboardSets.Count <= texListIndex) _texBillboardSets.Add(null);
            var set = _texBillboardSets[texListIndex];
            if (set is null)
            {
                set = _texBillboards.CreateTextureSet(_textures[texListIndex]!);
                _texBillboardSets[texListIndex] = set;
            }
            return set;
        }

        public void Dispose()
        {
            _model.Dispose();
            _post.Dispose();
            _lines.Dispose();
            _fills.Dispose();
            _billboards.Dispose();
            _texBillboards.Dispose();
            _beams.Dispose();
            _decalRenderer.Dispose();
            _overlayMeshes.Dispose();
            _res.Dispose();
            foreach (var m in _meshes)
                if (m is { } mesh) { mesh.Vb.Dispose(); mesh.Ib.Dispose(); mesh.MaterialSet?.Dispose(); }
            foreach (var m in _skinnedMeshes)
                if (m is { } e) { e.Vb.Dispose(); e.Ib.Dispose(); e.MaterialSet?.Dispose(); }
            foreach (var s in _texBillboardSets) s?.Dispose();
            _texBillboardSets.Clear();
            foreach (var t in _textures) t?.Dispose();
            _textures.Clear();
            foreach (var s in _splatMaterials) s?.Dispose();
            _splatMaterials.Clear();
        }

        readonly struct Mesh
        {
            public readonly IGpuBuffer Vb, Ib;
            public readonly int IndexCount;
            /// <summary>GPU index width of <see cref="Ib"/> (UInt16 for meshes up to 65,536 verts, else UInt32).</summary>
            public readonly GpuIndexFormat IndexFormat;
            /// <summary>Per-mesh material resource set (UBO + albedo + sampler), or null => the renderer's white
            /// default. The texture itself is owned in Scene3D's <c>_textures</c> list, not here, so a texture can
            /// be shared by several meshes; only the set is owned per mesh.</summary>
            public readonly IGpuResourceSet? MaterialSet;
            /// <summary>Index into Scene3D's splat-material list when this mesh draws through the splat pipeline, else
            /// -1 (the normal model pipeline). Splat meshes carry no per-mesh <see cref="MaterialSet"/> (the splat set
            /// is shared and owned by the scene), so unload frees only Vb/Ib.</summary>
            public readonly int SplatMaterial;
            public Mesh(IGpuBuffer vb, IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat, IGpuResourceSet? materialSet = null, int splatMaterial = -1)
            {
                Vb = vb; Ib = ib; IndexCount = indexCount; IndexFormat = indexFormat; MaterialSet = materialSet; SplatMaterial = splatMaterial;
            }
        }

        /// <summary>A loaded splat-terrain material: the two 5-layer texture arrays (albedo, normal), the combined
        /// frame+params UBO (frame portion re-synced each frame), and the resource set. Owned by Scene3D; shared by
        /// every mesh that uses it.</summary>
        sealed class SplatMaterialEntry
        {
            public readonly IGpuTexture AlbedoArray, NormalArray;
            public readonly IGpuBuffer Ubo;
            public readonly IGpuResourceSet Set;
            readonly IGpuSampler? _ownedSampler;   // non-null only when the material overrode the shared sampler
            public SplatMaterialEntry(IGpuTexture albedo, IGpuTexture normal, IGpuBuffer ubo, IGpuResourceSet set, IGpuSampler? ownedSampler = null)
            { AlbedoArray = albedo; NormalArray = normal; Ubo = ubo; Set = set; _ownedSampler = ownedSampler; }
            public void Dispose() { Set.Dispose(); AlbedoArray.Dispose(); NormalArray.Dispose(); Ubo.Dispose(); _ownedSampler?.Dispose(); }
        }

        /// <summary>A GPU-resident skinned mesh: its vertex/index buffers, index count, optional material set, and
        /// the CPU-side inverse-bind matrices needed to compose per-frame bone palettes at DrawSkinned time.</summary>
        sealed class SkinnedMeshEntry
        {
            public readonly IGpuBuffer Vb, Ib;
            public readonly int IndexCount;
            public readonly GpuIndexFormat IndexFormat;
            public readonly IGpuResourceSet? MaterialSet;
            public readonly Matrix4x4[] InverseBind;
            public SkinnedMeshEntry(IGpuBuffer vb, IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat, IGpuResourceSet? materialSet, Matrix4x4[] inverseBind)
            {
                Vb = vb; Ib = ib; IndexCount = indexCount; IndexFormat = indexFormat; MaterialSet = materialSet; InverseBind = inverseBind;
            }
        }

        /// <summary>One CPU-skinned draw: the mesh's index buffer + count, the base vertex of its deformed verts in
        /// the shared skinned vertex stream, and its optional material set. Built per frame in RenderInternal.</summary>
        readonly struct CpuSkinnedDraw
        {
            public readonly IGpuBuffer Ib;
            public readonly int IndexCount;
            public readonly GpuIndexFormat IndexFormat;
            public readonly int BaseVertex;
            public readonly IGpuResourceSet? MaterialSet;
            public CpuSkinnedDraw(IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat, int baseVertex, IGpuResourceSet? materialSet)
            {
                Ib = ib; IndexCount = indexCount; IndexFormat = indexFormat; BaseVertex = baseVertex; MaterialSet = materialSet;
            }
        }

        /// <summary>A contiguous run of instances of one mesh handle inside the flat instance array.</summary>
        internal readonly struct MeshRun
        {
            public readonly MeshHandle Mesh;
            public readonly uint Start;
            public readonly uint Count;
            public MeshRun(MeshHandle mesh, uint start, uint count) { Mesh = mesh; Start = start; Count = count; }
            public MeshRun(int meshIndex, uint start, uint count) : this(new MeshHandle(meshIndex), start, count) { }
        }

        /// <summary>Two handles name the same mesh slot occupant (index AND generation match).</summary>
        internal static bool SameHandle(MeshHandle a, MeshHandle b) =>
            a.Index == b.Index && a.Generation == b.Generation;

        /// <summary>One queued textured billboard (resolved texture list index + blend + transform + source rect +
        /// tint). Stored in submission order; coalesced into runs at render time.</summary>
        internal struct TexturedBillboardItem
        {
            public int TexIndex;          // ListIndex into _textures
            public BillboardBlend Blend;
            public Vector3 Center;
            public float Size;
            public Vector4 SourceUv;      // (u0,v0,u1,v1)
            public Vector4 Color;
        }

        /// <summary>A contiguous run of textured-billboard items sharing one texture + blend, drawn as one call.</summary>
        internal readonly struct TexturedBillboardRun
        {
            public readonly int TexIndex;
            public readonly BillboardBlend Blend;
            public readonly int Start;
            public readonly int Count;
            public TexturedBillboardRun(int texIndex, BillboardBlend blend, int start, int count)
            {
                TexIndex = texIndex; Blend = blend; Start = start; Count = count;
            }
        }

        /// <summary>One queued beam: world endpoints + width, resolved core/glow colours (RGBA as Vector4), and two
        /// packed param vectors (Shape: coreFrac/glowSoftness/taper; Anim: pulseSpeed/pulseAmount/scrollSpeed).
        /// Built in <see cref="DrawBeam"/>; consumed in <see cref="DrawBeams"/>.</summary>
        internal struct BeamItem
        {
            public Vector3 A, B;
            public float Width;
            public Vector4 CoreColor;
            public Vector4 GlowColor;
            public Vector4 Shape;
            public Vector4 Anim;
        }

        /// <summary>
        /// Coalesce <paramref name="items"/> (in submission order) into <paramref name="runs"/>: each run is a
        /// maximal span of consecutive items sharing the same texture index AND blend. Submission order is
        /// preserved (a texture/blend change starts a new run rather than merging non-adjacent items), so
        /// alpha-blended quads keep the host's back-to-front ordering across textures. Pure + headless-testable;
        /// <paramref name="runs"/> is Cleared and refilled.
        /// </summary>
        internal static void CoalesceTexturedBillboards(IReadOnlyList<TexturedBillboardItem> items, List<TexturedBillboardRun> runs)
        {
            runs.Clear();
            if (items.Count == 0) return;

            int start = 0;
            for (int i = 1; i <= items.Count; i++)
            {
                bool boundary = i == items.Count
                    || items[i].TexIndex != items[start].TexIndex
                    || items[i].Blend != items[start].Blend;
                if (boundary)
                {
                    runs.Add(new TexturedBillboardRun(items[start].TexIndex, items[start].Blend, start, i - start));
                    start = i;
                }
            }
        }

        /// <summary>Compose <paramref name="boneMatrices"/> (per-frame joint world transforms) with
        /// <paramref name="inverseBind"/> and write them into <paramref name="dst"/> at bone slot
        /// <paramref name="slot"/> (matrix index <c>slot * MaxBonesPerDraw</c>). <paramref name="dst"/> is grown to
        /// hold the whole slot and any gap is identity-filled, so each draw's dynamic-offset window reads only its
        /// own (and harmless identity) matrices. Pure + headless-testable. Throws if the two inputs differ in length
        /// or the mesh exceeds the per-draw bone cap.</summary>
        internal static void ComposeBonesIntoSlot(List<Matrix4x4> dst, int slot,
            ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4[] inverseBind)
        {
            if (boneMatrices.Length != inverseBind.Length)
                throw new ArgumentException(
                    $"boneMatrices length {boneMatrices.Length} must equal the mesh bone count {inverseBind.Length}.");
            int cap = SkinningMath.MaxBonesPerDraw;
            if (boneMatrices.Length > cap)
                throw new ArgumentException($"a skinned mesh has {boneMatrices.Length} bones, over the {cap}-bone per-draw cap.");
            int need = (slot + 1) * cap;
            while (dst.Count < need) dst.Add(Matrix4x4.Identity);   // pad up to and including this slot (identity = no deform)
            int baseIdx = slot * cap;
            for (int b = 0; b < boneMatrices.Length; b++)
                dst[baseIdx + b] = SkinningMath.Compose(boneMatrices[b], inverseBind[b]);
            for (int b = boneMatrices.Length; b < cap; b++)
                dst[baseIdx + b] = Matrix4x4.Identity;             // clear the rest of the slot (reused list)
        }

        /// <summary>
        /// Group queued <paramref name="items"/> by mesh handle into <paramref name="instanceData"/> (a flat array
        /// ordered so all instances of one mesh are contiguous) and <paramref name="runs"/> (one
        /// <see cref="MeshRun"/> per unique mesh handle, in first-seen order). Pure + headless-testable; both output
        /// lists are Cleared and refilled (no realloc on the caller's reused buffers).
        /// </summary>
        internal static void GroupInstances(IReadOnlyList<SceneInstances.Instance> items,
            List<ModelRenderer.InstanceData> instanceData, List<MeshRun> runs)
        {
            instanceData.Clear();
            runs.Clear();
            if (items.Count == 0) return;

            // First-seen mesh order. Instances are usually already mesh-coherent (one mesh per kind), so the run
            // list stays short; we append each instance into its mesh's bucket by stable two-pass grouping.
            // Pass 1: collect distinct mesh indices in first-seen order + count per mesh.
            // Use the runs list as scratch for (meshIndex, count) accumulation.
            for (int i = 0; i < items.Count; i++)
            {
                MeshHandle mesh = items[i].Mesh;
                int slot = -1;
                for (int r = 0; r < runs.Count; r++)
                    if (SameHandle(runs[r].Mesh, mesh)) { slot = r; break; }
                if (slot < 0) runs.Add(new MeshRun(mesh, 0, 1));
                else runs[slot] = new MeshRun(mesh, 0, runs[slot].Count + 1);
            }

            // Assign each run a start offset (prefix sum), and record per-mesh write cursors.
            // runs currently holds (meshIndex, 0, count) in first-seen order.
            uint cursor = 0;
            Span<uint> writeCursor = runs.Count <= 64 ? stackalloc uint[runs.Count] : new uint[runs.Count];
            for (int r = 0; r < runs.Count; r++)
            {
                uint start = cursor;
                writeCursor[r] = start;
                cursor += runs[r].Count;
                runs[r] = new MeshRun(runs[r].Mesh, start, runs[r].Count);
            }

            // Size the flat array, then scatter each instance into its mesh's contiguous slot.
            int total = (int)cursor;
            for (int i = 0; i < total; i++) instanceData.Add(default);
            for (int i = 0; i < items.Count; i++)
            {
                var inst = items[i];
                MeshHandle mesh = inst.Mesh;
                int slot = -1;
                for (int r = 0; r < runs.Count; r++)
                    if (SameHandle(runs[r].Mesh, mesh)) { slot = r; break; }
                uint dst = writeCursor[slot]++;
                instanceData[(int)dst] = new ModelRenderer.InstanceData
                {
                    Model = inst.World,
                    Tint = inst.Tint,
                    Emissive = inst.Material.Emissive,
                    SpecParams = new Vector4(inst.Material.Specular, inst.Material.Shininess, 0f, 0f),
                };
            }
        }
    }
}
