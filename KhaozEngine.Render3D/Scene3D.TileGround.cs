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
        /// loaded with <see cref="LoadTileGroundMaterial(int,int,IReadOnlyList{TileGroundLayerImage},float,TerrainSamplerConfig?)"/>. Pass it to
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
        /// that references it. A layer carrying surface texels needs the surface size, so it loads through the
        /// overload that takes one.
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
            => LoadTileGroundMaterial(width, height, layers, 0, 0, baseSpecStrength, sampler);

        /// <summary>Upload a tile-ground material whose layers may carry relief members (<see cref="TileGroundRelief"/>).
        /// A set none of whose layers uses one loads exactly as the overload without a surface size does and draws
        /// through the plain pipeline. Otherwise the set draws through the relief pipeline: the params UBO is
        /// <see cref="TileGroundMaterialConfig.ReliefParamsBytes"/>, and the layers carrying
        /// <see cref="TileGroundLayerImage.SurfaceRgba"/> upload, in slot order, into a second mipped array of
        /// <paramref name="surfaceWidth"/> x <paramref name="surfaceHeight"/> RGBA8, which is independent of the
        /// albedo size. A relief set with no surface texels binds a one-texel flat surface array instead, so memory
        /// follows only the layers that use a map. Every refusal is raised before any GPU resource exists.</summary>
        /// <param name="width">Albedo layer width in texels, shared by every layer.</param>
        /// <param name="height">Albedo layer height in texels, shared by every layer.</param>
        /// <param name="layers">The layers, in slot order. 1 to <see cref="TileGroundMaterialConfig.MaxMaterials"/>.</param>
        /// <param name="surfaceWidth">Surface layer width in texels, shared by every surface layer. Read only when a
        /// layer carries surface texels, and then it must be positive.</param>
        /// <param name="surfaceHeight">Surface layer height in texels, the same rule.</param>
        /// <param name="baseSpecStrength">Base specular strength for the whole set: the map-less specular, and the
        /// strength a roughness map of 0.5 reproduces.</param>
        /// <param name="sampler">Optional sampler override. Null binds the renderer's shared terrain sampler.</param>
        public TileGroundMaterialHandle LoadTileGroundMaterial(int width, int height,
            IReadOnlyList<TileGroundLayerImage> layers, int surfaceWidth, int surfaceHeight,
            float baseSpecStrength = 0.15f, TerrainSamplerConfig? sampler = null)
        {
            // A non-positive size would reach the texture description as a huge uint and fail somewhere in the
            // seam, so it is caught by name here. Zero is the likelier mistake of the two, from an unset field.
            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), width, "tile-ground layer width must be positive.");
            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height), height, "tile-ground layer height must be positive.");
            if (layers.Count < 1 || layers.Count > TileGroundMaterialConfig.MaxMaterials)
                throw new TileGroundUploadArgumentException(TileGroundUploadRefusal.LayerCountOutsideRange,
                    $"a tile-ground material needs 1 to {TileGroundMaterialConfig.MaxMaterials} layers, got {layers.Count}.",
                    nameof(layers))
                { LayerCount = layers.Count, MinimumLayerCount = 1, MaximumLayerCount = TileGroundMaterialConfig.MaxMaterials };
            // Every layer is one slice of ONE array texture, so a layer that is not exactly width*height*4 bytes
            // would either overrun the slice or leave it partly uninitialised. Name the layer and both sizes rather
            // than letting the upload fail somewhere in the GPU seam.
            int expected = width * height * 4;
            for (int i = 0; i < layers.Count; i++)
                if (layers[i].AlbedoRgba.Length != expected)
                    throw new TileGroundUploadArgumentException(TileGroundUploadRefusal.LayerByteLengthMismatch,
                        $"tile-ground layer {i} is {layers[i].AlbedoRgba.Length} bytes, expected {expected} for {width}x{height} RGBA8.",
                        nameof(layers))
                    {
                        LayerIndex = i,
                        ActualByteLength = layers[i].AlbedoRgba.Length,
                        ExpectedByteLength = expected,
                        Width = width,
                        Height = height,
                    };
            CheckSurfaceTexels(layers, surfaceWidth, surfaceHeight);

            // Packed, and so validated, BEFORE any GPU resource exists: a layer whose steep companion or relief
            // members are refused throws here, and a throw after the upload below would leak the arrays it created.
            bool relief = TileGroundRelief.UsesRelief(layers);
            Vector4[] tail = relief
                ? TileGroundMaterialConfig.BuildReliefParams(layers, baseSpecStrength)
                : TileGroundMaterialConfig.BuildParams(layers, baseSpecStrength);
            // Each layer's program need, so a mesh can draw with the cheapest program its slots need. Only the relief
            // programs some layer can need are built.
            TileGroundProgram[]? needs = relief ? TileGroundPrograms.ForLayers(layers) : null;
            if (needs is not null)
            {
                if (Array.IndexOf(needs, TileGroundProgram.Relief) >= 0) _model.EnsureTileGroundRelief(hex: false);
                if (Array.IndexOf(needs, TileGroundProgram.ReliefHex) >= 0) _model.EnsureTileGroundRelief(hex: true);
            }

            uint w = (uint)width, h = (uint)height, mips = TileGroundMaterialConfig.MipLevelCount(width, height);
            // Created, uploaded and mipped in one transient list, and freed if that list is refused mid-frame (#424).
            IGpuTexture albedo = TextureUploads.CreateAlbedoArray(_gd, w, h, mips, layers, "Scene3D.LoadTileGroundMaterial");
            IGpuTexture? surface = null;
            try
            {
                if (relief) surface = CreateTileGroundSurfaceArray(layers, surfaceWidth, surfaceHeight);
            }
            catch
            {
                albedo.Dispose();
                throw;
            }

            // The material's OWN uniform buffer, holding these params alone and written once, here. The frame
            // uniforms are not in it: the pipeline reads those from the renderer's shared block at set 0 (#727).
            IGpuBuffer ubo = _model.CreateTileGroundParamsUbo(tail);

            return LoadTileGroundMaterial(albedo, ubo, sampler, null, surface, needs);
        }

        // Surface texels must match the declared surface size, which must be positive once any layer carries them.
        static void CheckSurfaceTexels(IReadOnlyList<TileGroundLayerImage> layers, int surfaceWidth, int surfaceHeight)
        {
            if (TileGroundRelief.SurfaceLayerCount(layers) == 0) return;
            if (surfaceWidth <= 0)
                throw new ArgumentOutOfRangeException(nameof(surfaceWidth), surfaceWidth,
                    "a tile-ground layer carries surface texels, so the surface width must be positive.");
            if (surfaceHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(surfaceHeight), surfaceHeight,
                    "a tile-ground layer carries surface texels, so the surface height must be positive.");
            int expected = surfaceWidth * surfaceHeight * 4;
            for (int i = 0; i < layers.Count; i++)
                if (layers[i].SurfaceRgba is { } texels && texels.Length != expected)
                    throw new TileGroundUploadArgumentException(TileGroundUploadRefusal.SurfaceByteLengthMismatch,
                        $"tile-ground layer {i}'s surface texels are {texels.Length} bytes, expected {expected} for the " +
                        $"{surfaceWidth}x{surfaceHeight} RGBA8 surface size.", nameof(layers))
                    {
                        LayerIndex = i,
                        ActualByteLength = texels.Length,
                        ExpectedByteLength = expected,
                        Width = surfaceWidth,
                        Height = surfaceHeight,
                    };
        }

        // The relief set's surface array: the layers that carry texels, in slot order, which is the numbering
        // TileGroundRelief.Pack gave them. None at all binds one flat texel, a straight-up normal and roughness 0.5,
        // which no layer reads because none says it has a map.
        IGpuTexture CreateTileGroundSurfaceArray(IReadOnlyList<TileGroundLayerImage> layers, int surfaceWidth, int surfaceHeight)
        {
            var texels = new List<byte[]>();
            foreach (TileGroundLayerImage layer in layers)
                if (layer.SurfaceRgba is { } t) texels.Add(t);
            if (texels.Count == 0)
                return TextureUploads.CreateLayerArray(_gd, 1, 1, 1, new[] { new byte[] { 128, 128, 255, 128 } },
                    "Scene3D.LoadTileGroundMaterial");
            uint w = (uint)surfaceWidth, h = (uint)surfaceHeight;
            return TextureUploads.CreateLayerArray(_gd, w, h, TileGroundMaterialConfig.MipLevelCount(surfaceWidth, surfaceHeight),
                texels, "Scene3D.LoadTileGroundMaterial");
        }

        /// <summary>Upload a mesh and draw it through the tile-ground pipeline with <paramref name="material"/>: its
        /// vertex <c>Color</c> carries the four corner weights, <c>Uv.xy</c> plus <c>Tangent.xy</c> the four corner
        /// material slots, and <c>Tangent.z</c> the per-vertex brightness jitter. An invalid handle falls back to
        /// the untextured model path. The material is shared (owned by the scene), so unloading the mesh does NOT
        /// free it.
        /// <para>THE JITTER IS A MULTIPLIER, NOT AN OFFSET, so <c>Tangent.z</c> of 0 renders that vertex BLACK.
        /// A mesher that wants no jitter writes 1.0, never 0, and a mesh built for the model pipeline (where
        /// <c>Tangent</c> is a tangent frame and <c>Tangent.z</c> is whatever the exporter wrote) is not a
        /// tile-ground mesh. This is the failure that looks like a lighting bug and is not one.</para>
        /// <para>A mesh of a relief material draws with the cheapest program its slots need: the plain program
        /// when it names no relief layer, the hex-tiling one only when it names a hex-tiled layer. A tile world's
        /// region meshes are separate meshes, so a region that paints no relief material costs what it always did.</para></summary>
        public MeshHandle LoadMesh(GltfMesh mesh, TileGroundMaterialHandle material)
        {
            if (!material.IsValid) return LoadMesh(mesh);
            TileGroundProgram program = material.ListIndex < _tileGroundMaterials.Count
                && _tileGroundMaterials[material.ListIndex] is { LayerPrograms: { } needs }
                ? TileGroundPrograms.ForMesh(mesh.Vertices, needs) : TileGroundProgram.Plain;
            return LoadMeshInternal(mesh, null, tileGroundMaterial: material.ListIndex, tileGroundProgram: program);
        }

        /// <summary>Number of tile-ground material slots still holding a live material. For tests.</summary>
        internal int LiveTileGroundMaterialCount
        {
            get { int n = 0; foreach (var m in _tileGroundMaterials) if (m != null) n++; return n; }
        }

        /// <summary>The program a loaded tile-ground mesh draws with. For tests.</summary>
        internal TileGroundProgram TileGroundProgramForTests(MeshHandle mesh) =>
            _meshes[mesh.Index] is { } m && _tileGroundMaterials[m.TileGroundMaterial] is { ReliefSet: not null }
                ? m.TileGroundProgram : TileGroundProgram.Plain;

        // Tile-ground pass: same uploaded instance buffer, the dedicated albedo-array pipeline. Since #727 the
        // frame block is the renderer's shared one, bound at set 0, so there is nothing to re-sync into a
        // per-material buffer before drawing and a frame costs one frame-UBO write however many ground materials
        // are loaded. The material's own params went up once, at load.
        void DrawTileGroundRuns(IGpuCommandList cl)
        {
            // Which program is bound, none yet. A run whose mesh needs another program rebinds.
            TileGroundProgram? bound = null;
            foreach (var run in _runs)
            {
                if (!_slots.IsValid(run.Mesh.Index, run.Mesh.Generation)) continue;
                var m = _meshes[run.Mesh.Index];
                if (m is not { } mesh) continue;
                if (mesh.TileGroundMaterial < 0) continue;
                var tg = _tileGroundMaterials[mesh.TileGroundMaterial];
                if (tg is null) continue;
                // A plain material has the plain set only. A relief material's plain-program meshes bind its plain
                // set, whose larger params buffer the plain block reads the prefix of.
                TileGroundProgram program = tg.ReliefSet is null ? TileGroundProgram.Plain : mesh.TileGroundProgram;
                if (bound != program) { _model.BindTileGroundPass(cl, program); bound = program; }
                IGpuResourceSet set = program == TileGroundProgram.Plain ? tg.Set : tg.ReliefSet!;
                uint spanStart = run.Start; uint spanLen = 0;
                for (uint s = 0; s < run.Count; s++)
                {
                    if (_instanceVisible[run.Start + s]) { if (spanLen == 0) spanStart = run.Start + s; spanLen++; }
                    else if (spanLen > 0) { _model.DrawTileGroundMeshInstanced(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, spanStart, spanLen, set); CountMeshDraw(mesh.IndexCount, spanLen); spanLen = 0; }
                }
                if (spanLen > 0) { _model.DrawTileGroundMeshInstanced(cl, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, spanStart, spanLen, set); CountMeshDraw(mesh.IndexCount, spanLen); }
            }
        }

        // Release independent bundles and report whether every shared shadow receiver set released.
        bool DisposeTileGroundMaterials(Internal.ResourceDisposal disposal)
        {
            bool retained = false;
            for (int i = 0; i < _tileGroundMaterials.Count; i++)
                if (_tileGroundMaterials[i] is { } entry)
                {
                    if (entry.Dispose(disposal)) _tileGroundMaterials[i] = null;
                    else retained = true;
                }
            if (!retained) _tileGroundMaterials.Clear();
            return !retained;
        }

        /// <summary>A loaded tile-ground material: the albedo texture array (one layer per catalog material), the
        /// surface array of a relief material, the params UBO (written once at load, never re-uploaded), and its resource
        /// sets. Every material has the plain program's set. A relief material also has the relief programs' set and each
        /// layer's program need, which is how a mesh picks its program at load. Owned by Scene3D and shared by every
        /// mesh that uses it.</summary>
        sealed class TileGroundMaterialEntry : IDisposable
        {
            public readonly IGpuTexture AlbedoArray;
            public readonly IGpuTexture? SurfaceArray;
            public readonly IGpuBuffer Ubo;
            /// <summary>The plain program's set. A relief material's larger params buffer binds here too, because the
            /// plain block is its prefix.</summary>
            public IGpuResourceSet Set { get; private set; }
            /// <summary>The relief programs' set, null for a plain material.</summary>
            public IGpuResourceSet? ReliefSet { get; private set; }
            /// <summary>Each slot's program need, null for a plain material.</summary>
            public readonly TileGroundProgram[]? LayerPrograms;
            readonly IGpuSampler? _ownedSampler;   // non-null only when the material overrode the shared sampler
            public TileGroundMaterialEntry(IGpuTexture albedo, IGpuBuffer ubo, IGpuResourceSet set, IGpuSampler? ownedSampler = null,
                IGpuTexture? surface = null, IGpuResourceSet? reliefSet = null, TileGroundProgram[]? layerPrograms = null)
            {
                AlbedoArray = albedo; Ubo = ubo; Set = set; _ownedSampler = ownedSampler; SurfaceArray = surface;
                ReliefSet = reliefSet; LayerPrograms = layerPrograms;
            }
            /// <summary>Swap in each set's replacement after a shadow layout change.</summary>
            public void ReplaceSets(Func<IGpuResourceSet, IGpuResourceSet> replacementFor)
            {
                Set = replacementFor(Set);
                if (ReliefSet is not null) ReliefSet = replacementFor(ReliefSet);
            }
            public void Dispose()
            {
                var disposal = new Internal.ResourceDisposal();
                Dispose(disposal);
                disposal.ThrowIfFailed();
            }
            // Shared bindings are relinquished once the sets release, even if a later child release fails. A released
            // relief set is dropped at once, so a retry after the plain set failed cannot release it twice.
            internal bool Dispose(Internal.ResourceDisposal disposal)
            {
                if (ReliefSet is { } relief)
                {
                    if (!disposal.Release("tileGround.reliefSet", relief.Dispose)) return false;
                    ReliefSet = null;
                }
                if (!disposal.Release("tileGround.set", Set.Dispose)) return false;
                disposal.Release("tileGround.albedo", AlbedoArray.Dispose);
                disposal.Release("tileGround.surface", () => SurfaceArray?.Dispose());
                disposal.Release("tileGround.uniform", Ubo.Dispose);
                disposal.Release("tileGround.sampler", () => _ownedSampler?.Dispose());
                return true;
            }
        }
    }
}
