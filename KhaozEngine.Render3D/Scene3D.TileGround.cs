using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The tile-ground material family: the loaded-material list, the load, the mesh upload that binds a mesh to the
    /// tile-ground pipeline, and the per-frame draw partition. The sibling of Scene3D.SplatMaterial.cs, for the
    /// pipeline a tile world's ground draws through (one albedo layer per catalog material, four corner slots per
    /// triangle blended by the vertex weights, tile-world design section 7.5). The unload sits with every other
    /// mid-life unload in Scene3D.Unload.cs, where the retire-versus-destroy question is answered once.
    /// </summary>
    public sealed partial class Scene3D
    {
        // Loaded tile-ground materials, indexed by TileGroundMaterialHandle.ListIndex. Each owns its albedo texture
        // array + params UBO + resource set, shared across meshes, disposed in Dispose or UnloadTileGroundMaterial.
        readonly List<TileGroundMaterialEntry?> _tileGroundMaterials = new();

        /// <summary>An opaque handle to a tile-ground material (up to
        /// <see cref="TileGroundMaterialConfig.MaxMaterials"/> tileable albedo layers, one per catalog material)
        /// loaded with <see cref="LoadTileGroundMaterial"/>. Pass it to
        /// <see cref="LoadMesh(GltfMesh,TileGroundMaterialHandle)"/> to draw a mesh through the tile-ground
        /// pipeline. Shared across many meshes (every region-plane mesh of a tile world).</summary>
        public readonly struct TileGroundMaterialHandle
        {
            internal readonly int Index;
            internal TileGroundMaterialHandle(int index) { Index = index + 1; } // store +1 so default == Invalid
            /// <summary>An invalid handle (the same as <c>default</c>).</summary>
            public static TileGroundMaterialHandle Invalid => default;
            /// <summary>True when this handle refers to a loaded material (not the <c>default</c>/Invalid handle).</summary>
            public bool IsValid => Index != 0;
            /// <summary>The 0-based list index this handle refers to. Only meaningful when <see cref="IsValid"/>.</summary>
            internal int ListIndex => Index - 1;
        }

        /// <summary>Upload a tile-ground material: ONE texture array of 1 to
        /// <see cref="TileGroundMaterialConfig.MaxMaterials"/> tileable albedo layers, all the same
        /// <paramref name="width"/> x <paramref name="height"/> RGBA8, with a full mip chain generated, plus a
        /// params UBO carrying each layer's tint and tiles-per-metre and the base specular strength. A layer's INDEX
        /// is the slot a mesh vertex names, so the caller decides the material-to-slot mapping. Returns a handle to
        /// draw meshes through the tile-ground pipeline. The material is owned by the scene and freed in
        /// <see cref="Dispose"/> (or <see cref="UnloadTileGroundMaterial"/>), and it is shared across every mesh
        /// that references it.
        /// <para>NOT A MID-FRAME CALL: the mip chain needs a command list of its own, so this refuses with
        /// <see cref="GpuNestedRecordingException"/> while a frame is recording (#424). Load once per view
        /// construction or catalog change.</para></summary>
        /// <param name="width">Layer width in texels, shared by every layer.</param>
        /// <param name="height">Layer height in texels, shared by every layer.</param>
        /// <param name="layers">The albedo layers, in slot order. 1 to <see cref="TileGroundMaterialConfig.MaxMaterials"/>.</param>
        /// <param name="baseSpecStrength">Base specular strength for the whole set (the ground carries no per-layer roughness).</param>
        /// <param name="sampler">Optional sampler override. Null binds the renderer's shared terrain sampler.</param>
        public TileGroundMaterialHandle LoadTileGroundMaterial(int width, int height,
            IReadOnlyList<TileGroundLayerImage> layers, float baseSpecStrength = 0.15f,
            TerrainSamplerConfig? sampler = null)
        {
            // A non-positive size would reach the texture description as a huge uint and fail somewhere in the
            // seam, so it is caught by name here. Zero is the likelier mistake of the two, from an unset field.
            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), width, "tile-ground layer width must be positive.");
            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height), height, "tile-ground layer height must be positive.");
            if (layers.Count < 1 || layers.Count > TileGroundMaterialConfig.MaxMaterials)
                throw new ArgumentException(
                    $"a tile-ground material needs 1 to {TileGroundMaterialConfig.MaxMaterials} layers, got {layers.Count}.",
                    nameof(layers));
            // Every layer is one slice of ONE array texture, so a layer that is not exactly width*height*4 bytes
            // would either overrun the slice or leave it partly uninitialised. Name the layer and both sizes rather
            // than letting the upload fail somewhere in the GPU seam.
            int expected = width * height * 4;
            for (int i = 0; i < layers.Count; i++)
                if (layers[i].AlbedoRgba.Length != expected)
                    throw new ArgumentException(
                        $"tile-ground layer {i} is {layers[i].AlbedoRgba.Length} bytes, expected {expected} for {width}x{height} RGBA8.",
                        nameof(layers));

            uint w = (uint)width, h = (uint)height, mips = TileGroundMaterialConfig.MipLevelCount(width, height);
            // Created, uploaded and mipped in one transient list, and freed if that list is refused mid-frame (#424).
            IGpuTexture albedo = TextureUploads.CreateAlbedoArray(_gd, w, h, mips, layers, "Scene3D.LoadTileGroundMaterial");

            Vector4[] tail = TileGroundMaterialConfig.BuildParams(layers, baseSpecStrength);
            // Combined UBO: frame uniforms (re-synced each frame in the tile-ground pass) + these params appended.
            // One uniform buffer for the whole pipeline, which the retired one-uniform-buffer rule once required
            // and #604 no longer does. This pass keeps the shape on purpose (#727).
            TileGroundUniformBuffer ubo = _model.CreateTileGroundParamsUbo(tail);

            // A material that overrides the sampler gets its own, owned and disposed with the material. Otherwise
            // the set binds the renderer's shared default sampler and nothing extra is owned here.
            IGpuSampler? ownedSampler = sampler.HasValue ? _model.CreateTerrainSampler(sampler.Value) : null;
            IGpuResourceSet set = ownedSampler is null
                ? _model.CreateTileGroundMaterialSet(ubo.Buffer, albedo)
                : _model.CreateTileGroundMaterialSet(ubo.Buffer, albedo, ownedSampler);
            _tileGroundMaterials.Add(new TileGroundMaterialEntry(albedo, ubo, set, ownedSampler));
            return new TileGroundMaterialHandle(_tileGroundMaterials.Count - 1);
        }

        /// <summary>Upload a mesh and draw it through the tile-ground pipeline with <paramref name="material"/>: its
        /// vertex <c>Color</c> carries the four corner weights, <c>Uv.xy</c> plus <c>Tangent.xy</c> the four corner
        /// material slots, and <c>Tangent.z</c> the per-vertex brightness jitter. An invalid handle falls back to
        /// the untextured model path. The material is shared (owned by the scene), so unloading the mesh does NOT
        /// free it.
        /// <para>THE JITTER IS A MULTIPLIER, NOT AN OFFSET, so <c>Tangent.z</c> of 0 renders that vertex BLACK.
        /// A mesher that wants no jitter writes 1.0, never 0, and a mesh built for the model pipeline (where
        /// <c>Tangent</c> is a tangent frame and <c>Tangent.z</c> is whatever the exporter wrote) is not a
        /// tile-ground mesh. This is the failure that looks like a lighting bug and is not one.</para></summary>
        public MeshHandle LoadMesh(GltfMesh mesh, TileGroundMaterialHandle material)
        {
            if (!material.IsValid) return LoadMesh(mesh);
            return LoadMeshInternal(mesh, null, tileGroundMaterial: material.ListIndex);
        }

        /// <summary>Number of tile-ground material slots still holding a live material. For tests.</summary>
        internal int LiveTileGroundMaterialCount
        {
            get { int n = 0; foreach (var m in _tileGroundMaterials) if (m != null) n++; return n; }
        }

        // Tile-ground pass: same uploaded instance buffer, the dedicated albedo-array pipeline. Each material's
        // combined UBO holds frame + params in one buffer, so re-sync this frame's uniforms into every loaded
        // material's UBO before drawing, exactly as the splat pass does (usually one material per world).
        void DrawTileGroundRuns(IGpuCommandList cl)
        {
            for (int i = 0; i < _tileGroundMaterials.Count; i++)
                if (_tileGroundMaterials[i] is { } syncTg) _model.WriteFrameUniformsTo(cl, syncTg.Ubo);
            bool groundBound = false;
            foreach (var run in _runs)
            {
                if (!_slots.IsValid(run.Mesh.Index, run.Mesh.Generation)) continue;
                var m = _meshes[run.Mesh.Index];
                if (m is not { } mesh) continue;
                if (mesh.TileGroundMaterial < 0) continue;
                var tg = _tileGroundMaterials[mesh.TileGroundMaterial];
                if (tg is null) continue;
                if (!groundBound) { _model.BindTileGroundPass(cl); groundBound = true; }
                uint spanStart = run.Start; uint spanLen = 0;
                for (uint s = 0; s < run.Count; s++)
                {
                    if (_instanceVisible[run.Start + s]) { if (spanLen == 0) spanStart = run.Start + s; spanLen++; }
                    else if (spanLen > 0) { _model.DrawTileGroundMeshInstanced(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, spanStart, spanLen, tg.Set); CountMeshDraw(mesh.IndexCount, spanLen); spanLen = 0; }
                }
                if (spanLen > 0) { _model.DrawTileGroundMeshInstanced(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, spanStart, spanLen, tg.Set); CountMeshDraw(mesh.IndexCount, spanLen); }
            }
        }

        // Free every loaded tile-ground material and clear the list. Called from Dispose.
        void DisposeTileGroundMaterials()
        {
            foreach (var t in _tileGroundMaterials) t?.Dispose();
            _tileGroundMaterials.Clear();
        }

        /// <summary>A loaded tile-ground material: the albedo texture array (one layer per catalog material), the
        /// combined frame+params UBO (frame portion re-synced each frame), and the resource set. Owned by Scene3D
        /// and shared by every mesh that uses it.</summary>
        sealed class TileGroundMaterialEntry : IDisposable
        {
            public readonly IGpuTexture AlbedoArray;
            public readonly TileGroundUniformBuffer Ubo;
            public readonly IGpuResourceSet Set;
            readonly IGpuSampler? _ownedSampler;   // non-null only when the material overrode the shared sampler
            public TileGroundMaterialEntry(IGpuTexture albedo, TileGroundUniformBuffer ubo, IGpuResourceSet set, IGpuSampler? ownedSampler = null)
            { AlbedoArray = albedo; Ubo = ubo; Set = set; _ownedSampler = ownedSampler; }
            public void Dispose() { Set.Dispose(); AlbedoArray.Dispose(); Ubo.Dispose(); _ownedSampler?.Dispose(); }
        }
    }
}
