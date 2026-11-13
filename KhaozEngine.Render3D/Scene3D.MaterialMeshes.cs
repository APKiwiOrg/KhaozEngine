using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D
{
    /// <summary>Material-bound mesh, texture and prop uploads: the surface-map bundle, the textured mesh overloads,
    /// and the turn-key glTF uploads whose meshes own references to the shared material textures they bind (see
    /// Scene3D.MaterialTextureResidency.cs).</summary>
    public sealed partial class Scene3D
    {
        /// <summary>A bundle of optional surface maps for <see cref="LoadMesh(GltfMesh,SurfaceMaps)"/> and
        /// <see cref="LoadSkinnedMesh(SkinnedGltfMesh,SurfaceMaps)"/>:
        /// albedo, tangent-space normal, roughness (glTF metallic-roughness .g convention) and emissive. Any invalid
        /// (<c>default</c>) handle falls back to the renderer's default for that slot (white albedo, flat
        /// normal, zero roughness, white emissive), so binding only some maps is fine. Load each map with
        /// <see cref="LoadTexture(string,TextureMipPolicy)"/> / <see cref="LoadTexture(byte[],int,int,TextureMipPolicy)"/>. <see cref="AlphaCutoff"/>
        /// carries the material's alpha-cutout threshold (0 = OPAQUE, no clip, see
        /// <see cref="GltfMaterialMaps.AlphaCutoff"/>).</summary>
        public readonly struct SurfaceMaps
        {
            public readonly TextureHandle Albedo;
            public readonly TextureHandle Normal;
            public readonly TextureHandle Roughness;
            /// <summary>Alpha-cutout threshold: 0 = OPAQUE (no clip), else the MASK cutoff. The model fragment
            /// discards a texel whose albedo alpha is below this, so a foliage/leaf-card texture renders as its
            /// silhouette instead of a solid quad, and with an albedo bound the key light's shadow pass cuts it out
            /// too. OPAQUE (0) is byte-identical to the pre-cutout render.</summary>
            public readonly float AlphaCutoff;
            /// <summary>The emissive map, its RGB scaled by <see cref="EmissiveFactor"/>. Invalid samples a 1x1
            /// white default, so a factor on its own is a flat glow.</summary>
            public readonly TextureHandle Emissive;
            /// <summary>The emissive colour: HDR light added after lighting that the scene's lights and shadows never
            /// scale, which is what makes it bloom. It adds to the per-instance <see cref="Material.Emissive"/>, and
            /// zero (the default) adds nothing at all.</summary>
            public readonly Vector3 EmissiveFactor;

            public SurfaceMaps(TextureHandle albedo, TextureHandle normal = default, TextureHandle roughness = default,
                float alphaCutoff = 0f)
                : this(albedo, normal, roughness, alphaCutoff, default, Vector3.Zero)
            {
            }

            /// <summary>A bundle with an emissive map and colour as well. See <see cref="Emissive"/> and
            /// <see cref="EmissiveFactor"/>.</summary>
            public SurfaceMaps(TextureHandle albedo, TextureHandle normal, TextureHandle roughness, float alphaCutoff,
                TextureHandle emissive, Vector3 emissiveFactor)
            {
                Albedo = albedo; Normal = normal; Roughness = roughness; AlphaCutoff = alphaCutoff;
                Emissive = emissive; EmissiveFactor = emissiveFactor;
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
            IGpuResourceSet? outlineMaterial = null;
            if (texture.IsValid)
            {
                material = _model.CreateMaterialSet(_textures[texture.ListIndex]!);
                try
                {
                    outlineMaterial = _targetOutlines.CreateMaterialSet(_textures[texture.ListIndex]!);
                }
                catch
                {
                    material.Dispose();
                    throw;
                }
            }
            return LoadMeshInternal(mesh, material, outlineMaterial);
        }

        /// <summary>Upload a mesh and bind a full PBR-lite material (<paramref name="maps"/>): albedo + optional
        /// normal + optional roughness + optional emissive. Invalid handles fall back to the renderer defaults.
        /// Normal perturbation requires the mesh to carry tangents (glTF meshes via <see cref="GltfLoader"/>, or
        /// MeshAssembler output); primitives have none and are lit by their geometric normal. The mesh borrows the
        /// textures: the caller keeps owning them.</summary>
        public MeshHandle LoadMesh(GltfMesh mesh, SurfaceMaps maps)
            => LoadMesh(mesh, maps, null, ownedTextures: null);

        // ownedTextures are references this mesh takes over from the residency, released when it unloads. Null
        // borrows, which is every caller-facing SurfaceMaps overload.
        MeshHandle LoadMesh(GltfMesh mesh, SurfaceMaps maps, Vector3[]? outlineNormals, TextureHandle[]? ownedTextures)
        {
            IGpuTexture? a = TextureOrNull(maps.Albedo);
            IGpuTexture? n = TextureOrNull(maps.Normal);
            IGpuTexture? r = TextureOrNull(maps.Roughness);
            IGpuTexture? e = TextureOrNull(maps.Emissive);
            IGpuBuffer? materialParams = CreateEmissiveParams(maps.EmissiveFactor);
            IGpuResourceSet? material = null;
            IGpuResourceSet? outlineMaterial;
            try
            {
                material = (a != null || n != null || r != null || e != null || materialParams != null)
                    ? _model.CreateMaterialSet(a, n, r, e, materialParams)
                    : null;
                outlineMaterial = a != null ? _targetOutlines.CreateMaterialSet(a) : null;
            }
            catch
            {
                material?.Dispose();
                materialParams?.Dispose();
                throw;
            }
            MeshHandle handle;
            try
            {
                handle = LoadMeshInternal(mesh, material, outlineMaterial, alphaCutoff: maps.AlphaCutoff,
                    outlineNormals: outlineNormals, cutoutAlbedo: a);
            }
            catch
            {
                // LoadMeshInternal already freed both sets. The params buffer is this call's alone.
                materialParams?.Dispose();
                throw;
            }
            RecordMeshMaterial(handle, ownedTextures, materialParams);
            return handle;
        }

        /// <summary>Decode a PNG/JPG file into an albedo texture (RGBA8) and return a handle for
        /// <see cref="LoadMesh(GltfMesh,TextureHandle)"/>. The texture is owned by the scene and freed in
        /// <see cref="Dispose"/>; it may be shared across several meshes.</summary>
        /// <param name="pngPath">Path to the image file.</param>
        /// <param name="policy">How much of the mip chain to build. The default is
        /// <see cref="TextureMipPolicy.Full"/>.</param>
        public TextureHandle LoadTexture(string pngPath, TextureMipPolicy policy = default)
        {
            ImageRgba img = ImageRgba.Load(pngPath);
            return LoadTexture(img.Pixels, img.Width, img.Height, policy);
        }

        /// <summary>Create an albedo texture from raw RGBA8 bytes (row-major, <paramref name="width"/> *
        /// <paramref name="height"/> * 4 bytes) and return a handle. For procedural textures and tests. The texture
        /// is owned by the scene and freed in <see cref="Dispose"/>.
        /// <para>A MIPPED LOAD IS NOT A MID-FRAME CALL: generating the chain needs a command list of its own, so
        /// this refuses with <see cref="GpuNestedRecordingException"/> while a frame is recording (#424). A
        /// one-level load opens no list and is unaffected.</para></summary>
        /// <param name="rgba">Row-major RGBA8 pixels.</param>
        /// <param name="width">Texture width in texels.</param>
        /// <param name="height">Texture height in texels.</param>
        /// <param name="policy">How much of the mip chain to build. The default is
        /// <see cref="TextureMipPolicy.Full"/>, which is what every caller got before the policy existed. Pass
        /// <see cref="TextureMipPolicy.None"/> or <see cref="TextureMipPolicy.AtlasGrid"/> for an image whose
        /// regions must not average into each other.</param>
        public TextureHandle LoadTexture(byte[] rgba, int width, int height, TextureMipPolicy policy = default)
        {
            // Create, upload and mip in one place that also owns the failure path: the mid-frame refusal throws
            // after the texture exists and before the scene has taken it, so TextureUploads frees it rather than
            // leaking one per attempt (#424).
            _textures.Add(TextureUploads.CreateMipped(
                _gd, rgba, (uint)width, (uint)height, policy.LevelsFor(width, height), "Scene3D.LoadTexture"));
            return new TextureHandle(_textures.Count - 1);
        }

        /// <summary>Opt-in convenience: upload a mesh and bind a glTF material's auto-read
        /// <see cref="GltfMaterialMaps"/> in one call, like <c>LoadMesh(mesh, LoadSurfaceMaps(maps))</c> except that
        /// the mesh owns the map references: <see cref="UnloadMesh"/> releases them, and an image another mesh also
        /// uses stays resident for it. Absent maps fall back to the renderer defaults; an all-absent
        /// <paramref name="maps"/> with no emissive colour loads the mesh untextured.</summary>
        public MeshHandle LoadMesh(GltfMesh mesh, GltfMaterialMaps maps) => LoadOwnedMesh(mesh, maps, null);

        /// <summary>Opt-in convenience: upload a skinned mesh and bind a glTF material's auto-read
        /// <see cref="GltfMaterialMaps"/> in one call, like <c>LoadSkinnedMesh(mesh, LoadSurfaceMaps(maps))</c>
        /// except that the mesh owns the map references and <see cref="UnloadSkinnedMesh"/> releases them, so an
        /// avatar spawned many times shares one set of textures.</summary>
        public SkinnedMeshHandle LoadSkinnedMesh(SkinnedGltfMesh mesh, GltfMaterialMaps maps)
        {
            SurfaceMaps surface = LoadSurfaceMaps(maps);
            try
            {
                return LoadSkinnedMesh(mesh, surface, OwnedTextures(surface));
            }
            catch
            {
                UnloadSurfaceMaps(surface);
                throw;
            }
        }

        MeshHandle LoadOwnedMesh(GltfMesh mesh, GltfMaterialMaps maps, Vector3[]? outlineNormals)
        {
            SurfaceMaps surface = LoadSurfaceMaps(maps);
            try
            {
                return LoadMesh(mesh, surface, outlineNormals, OwnedTextures(surface));
            }
            catch
            {
                UnloadSurfaceMaps(surface);
                throw;
            }
        }

        /// <summary>An opaque handle to a multi-material prop loaded with <see cref="LoadProp"/>: one textured
        /// sub-mesh per source material (bark + leaves, post + sign, ...), each with its own texture binding.
        /// <see cref="Draw(PropHandle,Matrix4x4,Color)"/> queues every part at one world transform (so the whole
        /// prop instances as a unit); <see cref="UnloadProp"/> frees them all. Wraps the per-part
        /// <see cref="MeshHandle"/>s; the array is owned by the handle.</summary>
        public readonly struct PropHandle
        {
            internal readonly MeshHandle[] Parts;
            internal PropHandle(MeshHandle[] parts) { Parts = parts; }
            /// <summary>True when this handle refers to a loaded prop (not the <c>default</c>/empty handle).</summary>
            public bool IsValid => Parts is { Length: > 0 };
            /// <summary>The number of textured sub-meshes (material parts) this prop is drawn as.</summary>
            public int PartCount => Parts?.Length ?? 0;
        }

        /// <summary>Upload a multi-material prop (one textured sub-mesh per source material, from
        /// <see cref="PropLoader.LoadPropParts"/> or <see cref="GltfLoader.LoadPartsWithMaterials(string)"/>) and return a
        /// <see cref="PropHandle"/> that draws them as one unit. Each part is a normal textured mesh
        /// (<see cref="LoadMesh(GltfMesh,GltfMaterialMaps)"/>), so it flows through the same instanced draw path -
        /// this is the multi-texture-per-primitive render path: distinct textures on distinct sub-ranges, instanced
        /// correctly. Each part owns its map references, and an image shared by parts, props or glbs is one resident
        /// texture. Throws <see cref="ArgumentException"/> if <paramref name="parts"/> is empty.</summary>
        public PropHandle LoadProp(IReadOnlyList<GltfMeshPart> parts) => new PropHandle(LoadPartHandles(parts));

        /// <summary>Upload a multi-material prop's parts (from <see cref="PropLoader.LoadPropParts"/> /
        /// <see cref="PropLoader.LoadPropAuto"/> / <see cref="GltfLoader.LoadPartsWithMaterials(string)"/>) and return the raw
        /// per-part <see cref="MeshHandle"/> list, one textured sub-mesh per source material. This is the multi-part
        /// scatter form: drop the list into a <c>id -&gt; parts</c> map for
        /// <c>KhaozEngine.Terrain.PropRenderer</c>'s multi-part draw path, where each part instances at the shared
        /// placement transform. A single-part list (a flat prop) yields one handle, drawn byte-identically to a plain
        /// <see cref="LoadMesh(GltfMesh,GltfMaterialMaps)"/>. Prefer <see cref="LoadProp(IReadOnlyList{GltfMeshPart})"/>
        /// when you want the parts bundled as one <see cref="PropHandle"/> (owned/unloaded as a unit). Prefer this
        /// when the caller owns the handles (e.g. shared across streamed chunks). Each handle owns its part's map
        /// references, so <see cref="UnloadMesh"/> on every handle releases them. Throws
        /// <see cref="ArgumentException"/> if <paramref name="parts"/> is empty.</summary>
        public IReadOnlyList<MeshHandle> LoadPropMeshes(IReadOnlyList<GltfMeshPart> parts) => LoadPartHandles(parts);

        MeshHandle[] LoadPartHandles(IReadOnlyList<GltfMeshPart> parts)
        {
            if (parts == null) throw new ArgumentNullException(nameof(parts));
            if (parts.Count == 0) throw new ArgumentException("a prop needs at least one material part.", nameof(parts));
            var handles = new MeshHandle[parts.Count];
            Vector3[][] outlineNormals = OutlineNormalBuilder.Build(parts);
            int loaded = 0;
            try
            {
                for (; loaded < parts.Count; loaded++)
                    handles[loaded] = LoadOwnedMesh(parts[loaded].Mesh, parts[loaded].Maps, outlineNormals[loaded]);
                return handles;
            }
            catch
            {
                // Each loaded part releases its own map references as it unloads, and the failing part's
                // LoadOwnedMesh already released the ones it acquired.
                for (int i = 0; i < loaded; i++) UnloadMesh(handles[i]);
                throw;
            }
        }

        /// <summary>Queue every part of <paramref name="prop"/> at <paramref name="world"/> with a white tint. Each
        /// part is a separate instanced mesh sharing the transform, so the whole prop moves as one and multiple
        /// draws of the same prop batch as instances. A <c>default</c>/invalid handle is a no-op.</summary>
        public void Draw(PropHandle prop, Matrix4x4 world) => Draw(prop, world, Color.White);

        /// <summary>Queue every part of <paramref name="prop"/> at <paramref name="world"/> tinted by
        /// <paramref name="tint"/> (multiplied into each part's albedo). A <c>default</c>/invalid handle is a
        /// no-op.</summary>
        public void Draw(PropHandle prop, Matrix4x4 world, Color tint)
        {
            if (prop.Parts == null) return;
            foreach (MeshHandle part in prop.Parts) Draw(new RigidInstanceDraw(part, world) { Tint = tint });
        }

        IGpuTexture? TextureOrNull(TextureHandle handle) => handle.IsValid ? _textures[handle.ListIndex] : null;

        // A material with no emissive colour binds the renderer's shared zero block, so only a glowing material pays
        // for a buffer of its own.
        IGpuBuffer? CreateEmissiveParams(Vector3 emissive) =>
            emissive == Vector3.Zero ? null : _model.CreateMaterialParams(emissive);

        // The references a bundle acquired, one per valid slot, which a mesh takes over to release on unload.
        static TextureHandle[] OwnedTextures(in SurfaceMaps maps)
        {
            var owned = new List<TextureHandle>(4);
            if (maps.Albedo.IsValid) owned.Add(maps.Albedo);
            if (maps.Normal.IsValid) owned.Add(maps.Normal);
            if (maps.Roughness.IsValid) owned.Add(maps.Roughness);
            if (maps.Emissive.IsValid) owned.Add(maps.Emissive);
            return owned.ToArray();
        }
    }
}
