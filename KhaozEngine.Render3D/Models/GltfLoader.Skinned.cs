using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using SharpGLTF.Schema2;
using GltfMaterial = SharpGLTF.Schema2.Material;

namespace KhaozEngine.Render3D
{
    public static partial class GltfLoader
    {
        /// <summary>Load a rigged glb/glTF as a <see cref="SkinnedGltfMesh"/>: reads POSITION/NORMAL/TEXCOORD_0/TANGENT
        /// plus JOINTS_0/WEIGHTS_0 and the skin's inverse-bind matrices + rest-pose joint world transforms. A missing
        /// TANGENT is computed from UV+position (Lengyel, accumulated over the index list then Gram-Schmidt
        /// orthogonalized against the normal) so a normal-mapped skinned mesh perturbs correctly. A mesh with no UV
        /// gradient keeps a zero tangent (lit by the geometric normal). Material textures are NOT auto-read (bind a
        /// PNG albedo separately, as with <see cref="Load"/>, or opt into auto-read with
        /// <see cref="LoadSkinnedWithMaterial(string)"/>). Throws if the mesh has no skin/joint data (use
        /// <see cref="Load"/> for rigid meshes). Indexed directly (no re-weld) so joints/weights stay aligned to
        /// their vertices. Emits 32-bit indices so rigs past the 65,536-vertex ceiling load
        /// (<see cref="SkinnedGltfMesh"/> picks the GPU index width).</summary>
        public static SkinnedGltfMesh LoadSkinned(string path) => LoadSkinned(path, SkinningProfile.LinearBlend);

        /// <summary>Load with an explicit deformation profile. DQ import validates raw influences and the
        /// composed rest pose. Audit authored samples with <see cref="SkinningValidation.ValidatePose"/>.
        /// Skins and arbitrary glTF extras never select DQ.</summary>
        public static SkinnedGltfMesh LoadSkinned(string path, SkinningProfile profile) =>
            ValidateSkinningImport(path, profile, () => BuildSkinned(LoadModel(path), path, profile));

        /// <summary>Load a flattened skinned mesh from binary glTF bytes without temporary files or source
        /// mutation. Uses the same decoder and skin joint order as the path overload. The caller's
        /// <paramref name="identity"/> names the asset in validation diagnostics.</summary>
        /// <remarks>Attachment consumers must apply their embedded skinned asset policy before import.</remarks>
        /// <exception cref="InvalidDataException">The glTF is malformed. The message names
        /// <paramref name="identity"/> and the importer's diagnostic is the inner exception.</exception>
        /// <exception cref="InvalidOperationException">The glTF has no skin, exceeds the bone cap or fails index
        /// validation.</exception>
        public static SkinnedGltfMesh LoadSkinned(ReadOnlyMemory<byte> glb, string identity) =>
            LoadSkinned(glb, identity, SkinningProfile.LinearBlend);

        /// <summary>Load binary glTF with the same explicit profile and validation as the path overload.</summary>
        public static SkinnedGltfMesh LoadSkinned(ReadOnlyMemory<byte> glb, string identity, SkinningProfile profile) =>
            TranslateModelErrors(identity, () => ValidateSkinningImport(identity, profile,
                () => BuildSkinned(LoadModel(glb, identity), identity, profile)));

        /// <summary>Opt-in convenience: load a rigged glb/glTF AND auto-read its first textured material's
        /// baseColor/normal/metallicRoughness textures, decoded to raw RGBA8 (no GPU). The
        /// <see cref="SkinnedGltfMesh"/> is identical to <see cref="LoadSkinned(string)"/>. Only the extra
        /// <see cref="GltfMaterialMaps"/> are new. Upload them with <see cref="Scene3D.LoadSurfaceMaps"/> and pass
        /// the result to <see cref="Scene3D.LoadSkinnedMesh(SkinnedGltfMesh,Scene3D.SurfaceMaps)"/>. A material with
        /// no (or missing/undecodable) textures yields an all-absent <see cref="GltfMaterialMaps"/>, never a
        /// throw.</summary>
        public static (SkinnedGltfMesh Mesh, GltfMaterialMaps Maps) LoadSkinnedWithMaterial(string path) =>
            LoadSkinnedWithMaterial(path, SkinningProfile.LinearBlend);

        /// <summary>Load skinned geometry and its first textured material with an explicit profile.</summary>
        public static (SkinnedGltfMesh Mesh, GltfMaterialMaps Maps) LoadSkinnedWithMaterial(string path, SkinningProfile profile) =>
            ValidateSkinningImport(path, profile, () =>
            {
                ModelRoot root = LoadModel(path);
                SkinnedGltfMesh mesh = BuildSkinned(root, path, profile);
                return (mesh, ReadMaterialMaps(FirstTexturedMaterial(root)));
            });

        static SkinnedGltfMesh BuildSkinned(ModelRoot root, string path, SkinningProfile profile) =>
            DecodeSkinned(root, path, splitByMaterial: false, profile)[0].Mesh;

        // Both flattened and material-part imports use this decoder. Only the destination buffers differ.
        static IReadOnlyList<(SkinnedGltfMesh Mesh, GltfMaterial? Material)> DecodeSkinned(
            ModelRoot root, string path, bool splitByMaterial, SkinningProfile profile)
        {
            var groups = new List<SkinnedGeometry>();
            Skin? skin = null;

            foreach (var mesh in root.LogicalMeshes)
                foreach (var prim in mesh.Primitives)
                {
                    var pos = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
                    var joints = prim.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
                    var weights = prim.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();
                    if (pos == null || joints == null || weights == null) continue;

                    SkinnedGeometry? group = splitByMaterial
                        ? groups.Find(g => ReferenceEquals(g.Material, prim.Material))
                        : groups.FirstOrDefault();
                    if (group == null)
                    {
                        group = new SkinnedGeometry(prim.Material);
                        groups.Add(group);
                    }
                    var verts = group.Vertices;
                    var indices = group.Indices;
                    var srcTangent = group.SourceTangents;

                    // Single-skin assumption: the first skin found supplies InverseBind/RestPose for all meshes.
                    // A glb where different meshes use different skins is not supported. Later meshes would mis-bind against this skin.
                    skin ??= root.LogicalNodes.FirstOrDefault(n => n.Mesh == mesh && n.Skin != null)?.Skin
                             ?? root.LogicalSkins.FirstOrDefault();

                    if (profile.Method == SkinningMethod.DualQuaternionRigid && skin != null)
                        ValidateImportInfluences(prim, pos.Count, skin.JointsCount, path);

                    var normals = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
                    var texcoords = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
                    // TANGENT is a vec4 (xyz = tangent direction, w = bitangent sign per glTF spec). When absent,
                    // it is computed below from UV+position.
                    var tangents = prim.GetVertexAccessor("TANGENT")?.AsVector4Array();
                    // Per-vertex COLOR_0 multiplies the material colour exactly as the rigid path does, so a rigged
                    // asset painted with vertex colours reads the same as the prop kit it was authored beside.
                    var colors = prim.GetVertexAccessor("COLOR_0")?.AsColorArray();
                    Vector4 baseColor = ReadBaseColor(prim.Material);
                    string identity = $"glTF asset '{path}'";

                    int baseIndex = verts.Count;
                    for (int i = 0; i < pos.Count; i++)
                    {
                        Vector4 w = SkinningMath.NormalizeWeights(weights[i]);
                        verts.Add(new SkinnedVertex
                        {
                            Position = pos[i],
                            Normal = normals != null && i < normals.Count ? normals[i] : Vector3.UnitY,
                            Color = colors != null && i < colors.Count ? baseColor * colors[i] : baseColor,
                            Uv = texcoords != null && i < texcoords.Count ? texcoords[i] : Vector2.Zero,
                            BoneIndices = joints[i],
                            BoneWeights = w,
                            Tangent = tangents != null && i < tangents.Count ? tangents[i] : Vector4.Zero,
                        });
                        srcTangent.Add(tangents != null && i < tangents.Count ? tangents[i] : (Vector4?)null);
                    }
                    foreach (var (a, b, c) in prim.GetTriangleIndices())
                    {
                        indices.Add(MeshIndexValidation.Rebase(a, pos.Count, baseIndex, identity));
                        indices.Add(MeshIndexValidation.Rebase(b, pos.Count, baseIndex, identity));
                        indices.Add(MeshIndexValidation.Rebase(c, pos.Count, baseIndex, identity));
                    }
                }

            if (skin == null || groups.All(group => group.Vertices.Count == 0))
                throw new InvalidOperationException("glTF has no skinned mesh (JOINTS_0/WEIGHTS_0 + skin): " + path);

            foreach (SkinnedGeometry group in groups)
            {
                if (profile.Method == SkinningMethod.DualQuaternionRigid)
                    SkinningValidation.ValidateSource(group.Vertices.ToArray(), skin.JointsCount, profile,
                        new SkinningValidationContext(path, null, -1));
                ComputeSkinnedTangents(group.Vertices, group.Indices, group.SourceTangents);
            }

            int boneCount = skin.JointsCount;

            // Reject a malformed/malicious rig at load. The CPU skinning path slices the bone palette to
            // exactly MaxBonesPerDraw and reads it unconditionally per vertex (SkinningMath.BlendSkinMatrix),
            // so an oversized joint count or an out-of-range JOINTS_0 index would otherwise throw mid-frame.
            if (boneCount > SkinningMath.MaxBonesPerDraw)
                throw new InvalidOperationException(
                    $"glTF skin has {boneCount} joints, over the {SkinningMath.MaxBonesPerDraw}-bone per-draw cap: {path}");
            foreach (SkinnedGeometry group in groups)
                foreach (SkinnedVertex v in group.Vertices)
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

            Skeleton skeleton = BuildSkeleton(skin);
            var meshes = new List<(SkinnedGltfMesh Mesh, GltfMaterial? Material)>(groups.Count);
            foreach (SkinnedGeometry group in groups)
            {
                SkinnedVertex[] vertices = group.Vertices.ToArray();
                SkinningValidation.ValidateSource(vertices, boneCount, profile, new SkinningValidationContext(path, null, -1));
                var mesh = new SkinnedGltfMesh(vertices, group.Indices.ToArray(), inverseBind, restPose, skeleton, profile);
                SkinningValidation.ValidatePose(mesh, restPose, new SkinningValidationContext(path, null, -1));
                meshes.Add((mesh, group.Material));
            }
            return meshes;
        }

        sealed class SkinnedGeometry
        {
            internal GltfMaterial? Material { get; }
            internal List<SkinnedVertex> Vertices { get; } = new();
            internal List<uint> Indices { get; } = new();
            internal List<Vector4?> SourceTangents { get; } = new();

            internal SkinnedGeometry(GltfMaterial? material) => Material = material;
        }
    }
}
