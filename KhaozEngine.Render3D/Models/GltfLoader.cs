using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D.Rendering;
using SharpGLTF.Schema2;
// KhaozEngine.Render3D now defines its own Material struct; alias the glTF one to disambiguate.
using GltfMaterial = SharpGLTF.Schema2.Material;

namespace KhaozEngine.Render3D
{
    /// <summary>One decoded material texture: tightly-packed RGBA8 pixels (row-major, top-left origin,
    /// <c>Rgba.Length == Width * Height * 4</c>) plus its dimensions. Carries no GPU resources; upload it with
    /// <see cref="Scene3D.LoadTexture(byte[],int,int)"/> (or via <see cref="Scene3D.LoadSurfaceMaps"/>).</summary>
    public readonly struct DecodedImage
    {
        /// <summary>Tightly-packed RGBA8 pixels, row-major, top-left origin (length = Width*Height*4).</summary>
        public byte[] Rgba { get; }
        public int Width { get; }
        public int Height { get; }
        public DecodedImage(byte[] rgba, int width, int height) { Rgba = rgba; Width = width; Height = height; }
    }

    /// <summary>The optional textures auto-read from a glTF material by <see cref="GltfLoader.LoadWithMaterial"/> /
    /// <see cref="GltfLoader.LoadSkinnedWithMaterial"/>, each decoded to raw RGBA8 (the loader has no GPU device, so
    /// it never creates a <see cref="Scene3D.TextureHandle"/>; the upload stays in <see cref="Scene3D"/>). Any map a
    /// material doesn't reference - or whose image is missing/external-unresolved/undecodable - is left
    /// <c>null</c> (absent, never a throw), so a material with no textures yields an all-null
    /// <see cref="GltfMaterialMaps"/>. <see cref="MetallicRoughness"/> is glTF's packed ORM-style texture passed
    /// through unchanged (the model shader samples <c>.g</c> for roughness); <see cref="Normal"/> is the
    /// tangent-space RGB normal map unchanged. Feed the bundle to <see cref="Scene3D.LoadSurfaceMaps"/> (or the
    /// <see cref="Scene3D.LoadMesh(GltfMesh,GltfMaterialMaps)"/> overload) to upload only the present maps.</summary>
    public readonly struct GltfMaterialMaps
    {
        /// <summary>Decoded baseColor (albedo) texture, or <c>null</c> if the material has none.</summary>
        public DecodedImage? Albedo { get; }
        /// <summary>Decoded tangent-space normal map (RGB unchanged), or <c>null</c> if the material has none.</summary>
        public DecodedImage? Normal { get; }
        /// <summary>Decoded glTF metallic-roughness texture, passed through unchanged (roughness in <c>.g</c>), or
        /// <c>null</c> if the material has none.</summary>
        public DecodedImage? Roughness { get; }
        public GltfMaterialMaps(DecodedImage? albedo, DecodedImage? normal, DecodedImage? roughness)
        {
            Albedo = albedo; Normal = normal; Roughness = roughness;
        }
        /// <summary>True when the material referenced (and the loader decoded) no textures at all.</summary>
        public bool IsEmpty => Albedo is null && Normal is null && Roughness is null;
    }

    /// <summary>Loads a glTF/GLB at runtime via SharpGLTF into a flat-shaded <see cref="GltfMesh"/>.
    /// Reads POSITION/NORMAL/TEXCOORD_0/TANGENT; a missing TANGENT is computed from UV+position by
    /// MeshAssembler. By default material textures (normal/roughness) are NOT auto-read - bind them explicitly via
    /// <see cref="Scene3D.SurfaceMaps"/>. Opt into auto-read with <see cref="LoadWithMaterial"/> /
    /// <see cref="LoadSkinnedWithMaterial"/>, which also return the material's decoded
    /// <see cref="GltfMaterialMaps"/>.</summary>
    public static class GltfLoader
    {
        public static GltfMesh Load(string path) => BuildRigid(ModelRoot.Load(path), path);

        /// <summary>Opt-in convenience: load a rigid glb/glTF AND auto-read its first textured material's baseColor,
        /// normal, and metallicRoughness textures, decoded to raw RGBA8 (no GPU - the returned
        /// <see cref="GltfMaterialMaps"/> holds CPU pixels). The <see cref="GltfMesh"/> is byte-identical to
        /// <see cref="Load"/>; only the extra maps are new. Upload them with <see cref="Scene3D.LoadSurfaceMaps"/>
        /// (or the <see cref="Scene3D.LoadMesh(GltfMesh,GltfMaterialMaps)"/> overload). A material with no textures -
        /// or one whose images are missing/external-unresolved/undecodable - yields an all-absent
        /// <see cref="GltfMaterialMaps"/> (<see cref="GltfMaterialMaps.IsEmpty"/>), never a throw, so this degrades
        /// to the untextured render. Embedded GLB images and external image files (resolved by SharpGLTF relative to
        /// the glb on load) are both read.</summary>
        public static (GltfMesh Mesh, GltfMaterialMaps Maps) LoadWithMaterial(string path)
        {
            ModelRoot root = ModelRoot.Load(path);
            GltfMesh mesh = BuildRigid(root, path);
            return (mesh, ReadMaterialMaps(FirstTexturedMaterial(root)));
        }

        static GltfMesh BuildRigid(ModelRoot root, string path)
        {
            var corners = new List<MeshCorner>();

            foreach (var mesh in root.LogicalMeshes)
            foreach (var prim in mesh.Primitives)
            {
                var pos = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
                if (pos == null) continue;
                // NORMAL / TEXCOORD_0 / TANGENT if present (SharpGLTF exposes the standard glTF attributes by
                // name, same accessor pattern as POSITION). Source normals are honoured so the artist's hard
                // edges survive; when absent, MeshAssembler computes a smooth normal from winding.
                // TANGENT is a vec4 (xyz = tangent direction, w = bitangent sign per glTF spec); when absent,
                // MeshAssembler computes tangents from UV+position.
                var srcNormals = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
                var texcoords = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
                var srcTangents = prim.GetVertexAccessor("TANGENT")?.AsVector4Array();
                Vector4 baseColor = ReadBaseColor(prim.Material);

                Vector3? Norm(int i) => srcNormals != null && i < srcNormals.Count ? srcNormals[i] : (Vector3?)null;
                Vector2 Uv(int i) => texcoords != null && i < texcoords.Count ? texcoords[i] : Vector2.Zero;
                Vector4? Tan(int i) => srcTangents != null && i < srcTangents.Count ? srcTangents[i] : (Vector4?)null;

                foreach (var (a, b, c) in prim.GetTriangleIndices())
                {
                    corners.Add(new MeshCorner(pos[a], Norm(a), baseColor, Uv(a), Tan(a)));
                    corners.Add(new MeshCorner(pos[b], Norm(b), baseColor, Uv(b), Tan(b)));
                    corners.Add(new MeshCorner(pos[c], Norm(c), baseColor, Uv(c), Tan(c)));
                }
            }
            if (corners.Count == 0) throw new InvalidOperationException("glTF has no triangles: " + path);

            return MeshAssembler.Build(corners);
        }

        /// <summary>Load a rigged glb/glTF as a <see cref="SkinnedGltfMesh"/>: reads POSITION/NORMAL/TEXCOORD_0/TANGENT
        /// plus JOINTS_0/WEIGHTS_0 and the skin's inverse-bind matrices + rest-pose joint world transforms. A missing
        /// TANGENT is computed from UV+position (Lengyel, accumulated over the index list then Gram-Schmidt
        /// orthogonalized against the normal) so a normal-mapped skinned mesh perturbs correctly; a mesh with no UV
        /// gradient keeps a zero tangent (lit by the geometric normal). Material textures are NOT auto-read (bind a
        /// PNG albedo separately, as with <see cref="Load"/>, or opt into auto-read with
        /// <see cref="LoadSkinnedWithMaterial"/>). Throws if the mesh has no skin/joint data (use
        /// <see cref="Load"/> for rigid meshes). Indexed directly (no re-weld) so joints/weights stay aligned to
        /// their vertices; emits 32-bit indices so rigs past the 65,536-vertex ceiling load
        /// (<see cref="SkinnedGltfMesh"/> picks the GPU index width).</summary>
        public static SkinnedGltfMesh LoadSkinned(string path) => BuildSkinned(ModelRoot.Load(path), path);

        /// <summary>Opt-in convenience: load a rigged glb/glTF AND auto-read its first textured material's
        /// baseColor/normal/metallicRoughness textures, decoded to raw RGBA8 (no GPU). The
        /// <see cref="SkinnedGltfMesh"/> is identical to <see cref="LoadSkinned"/>; only the extra
        /// <see cref="GltfMaterialMaps"/> are new. Upload them with <see cref="Scene3D.LoadSurfaceMaps"/> and pass
        /// the result to <see cref="Scene3D.LoadSkinnedMesh(SkinnedGltfMesh,Scene3D.SurfaceMaps)"/>. A material with
        /// no (or missing/undecodable) textures yields an all-absent <see cref="GltfMaterialMaps"/>, never a
        /// throw.</summary>
        public static (SkinnedGltfMesh Mesh, GltfMaterialMaps Maps) LoadSkinnedWithMaterial(string path)
        {
            ModelRoot root = ModelRoot.Load(path);
            SkinnedGltfMesh mesh = BuildSkinned(root, path);
            return (mesh, ReadMaterialMaps(FirstTexturedMaterial(root)));
        }

        static SkinnedGltfMesh BuildSkinned(ModelRoot root, string path)
        {
            var verts = new List<SkinnedVertex>();
            var indices = new List<uint>();
            var srcTangent = new List<Vector4?>();   // glTF TANGENT per vertex if present; null => compute
            Skin? skin = null;

            foreach (var mesh in root.LogicalMeshes)
            foreach (var prim in mesh.Primitives)
            {
                var pos = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
                var joints = prim.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
                var weights = prim.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();
                if (pos == null || joints == null || weights == null) continue;

                // Single-skin assumption: the first skin found supplies InverseBind/RestPose for all meshes.
                // A glb where different meshes use different skins is not supported; later meshes would mis-bind against this skin.
                skin ??= root.LogicalNodes.FirstOrDefault(n => n.Mesh == mesh && n.Skin != null)?.Skin
                         ?? root.LogicalSkins.FirstOrDefault();

                var normals = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
                var texcoords = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
                // TANGENT is a vec4 (xyz = tangent direction, w = bitangent sign per glTF spec); when absent,
                // it is computed below from UV+position.
                var tangents = prim.GetVertexAccessor("TANGENT")?.AsVector4Array();
                Vector4 baseColor = ReadBaseColor(prim.Material);

                int baseIndex = verts.Count;
                for (int i = 0; i < pos.Count; i++)
                {
                    Vector4 w = SkinningMath.NormalizeWeights(weights[i]);
                    verts.Add(new SkinnedVertex
                    {
                        Position = pos[i],
                        Normal = normals != null && i < normals.Count ? normals[i] : Vector3.UnitY,
                        Color = baseColor,
                        Uv = texcoords != null && i < texcoords.Count ? texcoords[i] : Vector2.Zero,
                        BoneIndices = joints[i],
                        BoneWeights = w,
                    });
                    srcTangent.Add(tangents != null && i < tangents.Count ? tangents[i] : (Vector4?)null);
                }
                foreach (var (a, b, c) in prim.GetTriangleIndices())
                {
                    indices.Add((uint)(baseIndex + a));
                    indices.Add((uint)(baseIndex + b));
                    indices.Add((uint)(baseIndex + c));
                }
            }

            if (verts.Count == 0 || skin == null)
                throw new InvalidOperationException("glTF has no skinned mesh (JOINTS_0/WEIGHTS_0 + skin): " + path);

            ComputeSkinnedTangents(verts, indices, srcTangent);

            int boneCount = skin.JointsCount;

            // Reject a malformed/malicious rig at load. The CPU skinning path slices the bone palette to
            // exactly MaxBonesPerDraw and reads it unconditionally per vertex (SkinningMath.BlendSkinMatrix),
            // so an oversized joint count or an out-of-range JOINTS_0 index would otherwise throw mid-frame.
            if (boneCount > SkinnedModelRenderer.MaxBonesPerDraw)
                throw new InvalidOperationException(
                    $"glTF skin has {boneCount} joints, over the {SkinnedModelRenderer.MaxBonesPerDraw}-bone per-draw cap: {path}");
            foreach (SkinnedVertex v in verts)
                if (!SkinningMath.AreBoneIndicesValid(v.BoneIndices, boneCount))
                    throw new InvalidOperationException(
                        $"glTF JOINTS_0 references a bone index outside [0,{boneCount}): {path}");

            var inverseBind = new Matrix4x4[boneCount];
            var restPose = new Matrix4x4[boneCount];
            for (int b = 0; b < boneCount; b++)
            {
                var (node, ibm) = skin.GetJoint(b);
                inverseBind[b] = ibm;
                restPose[b] = node.WorldMatrix;     // bind-pose joint world transform
            }

            return new SkinnedGltfMesh(verts.ToArray(), indices.ToArray(), inverseBind, restPose);
        }

        // Resolve a per-vertex tangent for the skinned mesh and write it into each SkinnedVertex.Tangent. A glTF
        // TANGENT (srcTangent[i]) wins; otherwise accumulate the Lengyel UV-space direction over the triangle list
        // (directly indexed, no weld - shared corners share a global index, so their face contributions sum) and
        // Gram-Schmidt orthogonalize against the vertex normal. Degenerate UVs => zero tangent (geometric normal).
        // Mirrors MeshAssembler's tangent path via the shared TangentMath helper.
        static void ComputeSkinnedTangents(List<SkinnedVertex> verts, List<uint> indices, List<Vector4?> srcTangent)
        {
            int n = verts.Count;
            var tan1 = new Vector3[n];
            var tan2 = new Vector3[n];
            for (int t = 0; t + 2 < indices.Count; t += 3)
            {
                int i0 = (int)indices[t], i1 = (int)indices[t + 1], i2 = (int)indices[t + 2];
                TangentMath.FaceDirections(
                    verts[i0].Position, verts[i1].Position, verts[i2].Position,
                    verts[i0].Uv, verts[i1].Uv, verts[i2].Uv, out Vector3 sdir, out Vector3 tdir);
                tan1[i0] += sdir; tan1[i1] += sdir; tan1[i2] += sdir;
                tan2[i0] += tdir; tan2[i1] += tdir; tan2[i2] += tdir;
            }
            for (int i = 0; i < n; i++)
            {
                Vector3 nrm = verts[i].Normal;
                nrm = nrm.LengthSquared() > 1e-12f ? Vector3.Normalize(nrm) : Vector3.UnitY;
                SkinnedVertex v = verts[i];
                v.Tangent = TangentMath.Resolve(nrm, tan1[i], tan2[i], srcTangent[i]);
                verts[i] = v;
            }
        }

        static Vector4 ReadBaseColor(GltfMaterial? mat)
        {
            var fallback = new Vector4(0.8f, 0.8f, 0.8f, 1f);
            if (mat == null) return fallback;
            var ch = mat.FindChannel("BaseColor");
            if (ch == null) return fallback;
            return ch.Value.Color;
        }

        /// <summary>The first logical material that references at least one of the auto-read texture channels
        /// (BaseColor / Normal / MetallicRoughness), or the first primitive's material, or null. Single-material
        /// assumption mirrors the loaders flattening all primitives into one mesh: the auto-read path is a
        /// convenience for the common one-material asset, not a multi-material atlas system.</summary>
        static GltfMaterial? FirstTexturedMaterial(ModelRoot root)
        {
            foreach (var m in root.LogicalMaterials)
                if (HasAnyAutoReadTexture(m)) return m;
            // No textured material: fall back to the first primitive's material so an explicit-but-untextured
            // material still resolves (ReadMaterialMaps then just returns all-absent).
            return root.LogicalMeshes.SelectMany(me => me.Primitives).Select(p => p.Material).FirstOrDefault(m => m != null);
        }

        static bool HasAnyAutoReadTexture(GltfMaterial? mat)
        {
            if (mat == null) return false;
            return ChannelTexture(mat, "BaseColor") != null
                || ChannelTexture(mat, "Normal") != null
                || ChannelTexture(mat, "MetallicRoughness") != null;
        }

        /// <summary>Decode a material's baseColor / normal / metallicRoughness textures to raw RGBA8. Each absent or
        /// undecodable channel becomes a null map (graceful degrade, never a throw). metallicRoughness is passed
        /// through unchanged (the shader reads roughness from .g); normal stays tangent-space RGB.</summary>
        static GltfMaterialMaps ReadMaterialMaps(GltfMaterial? mat)
        {
            if (mat == null) return default;   // all-null
            return new GltfMaterialMaps(
                DecodeChannel(mat, "BaseColor"),
                DecodeChannel(mat, "Normal"),
                DecodeChannel(mat, "MetallicRoughness"));
        }

        /// <summary>The encoded image bytes (PNG/JPG/...) for a material channel's texture, or null if the channel
        /// has no texture / no primary image / the image is empty or unresolved (e.g. an external file that did not
        /// resolve relative to the glb).</summary>
        static ReadOnlyMemory<byte>? ChannelTexture(GltfMaterial mat, string key)
        {
            var ch = mat.FindChannel(key);
            var content = ch?.Texture?.PrimaryImage?.Content;
            if (content is not { IsValid: true } img) return null;
            return img.Content;
        }

        /// <summary>Decode one material channel's texture to RGBA8 via the engine's shared image decoder
        /// (<see cref="ImageRgba.Decode"/>); returns null when the channel has no usable image OR the bytes fail to
        /// decode (a corrupt/unsupported image is treated as absent, so the mesh just renders without that map).</summary>
        static DecodedImage? DecodeChannel(GltfMaterial mat, string key)
        {
            var bytes = ChannelTexture(mat, key);
            if (bytes is not { } b) return null;
            try
            {
                ImageRgba img = ImageRgba.Decode(b.Span);
                return new DecodedImage(img.Pixels, img.Width, img.Height);
            }
            catch
            {
                return null;   // undecodable image => map absent (degrade gracefully, no throw)
            }
        }
    }
}
