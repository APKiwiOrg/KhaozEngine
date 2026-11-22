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
        public static SkinnedGltfMesh LoadSkinned(string path) => BuildSkinned(LoadModel(path), path);

        /// <summary>Load a flattened skinned mesh from binary glTF bytes without temporary files or source
        /// mutation. Uses the same decoder and skin joint order as the path overload. The caller's
        /// <paramref name="identity"/> names the asset in validation diagnostics.</summary>
        /// <remarks>Attachment consumers must apply their embedded skinned asset policy before import.</remarks>
        /// <exception cref="GltfImportDataException">The glTF is malformed. Reason is
        /// <see cref="GltfImportRefusal.MalformedModel"/>, Identity is <paramref name="identity"/> and the
        /// importer's diagnostic is the inner exception. This type derives from Exception because
        /// InvalidDataException is sealed.</exception>
        /// <exception cref="GltfImportStateException">The glTF fails strict validation, has no skin, exceeds
        /// the joint cap or has an influence outside the skin joint count. Reason identifies the rule.</exception>
        /// <exception cref="MeshIndexStateException">A source index or rebased index is outside its range.</exception>
        public static SkinnedGltfMesh LoadSkinned(ReadOnlyMemory<byte> glb, string identity)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(identity);
            return TranslateModelErrors(identity, () => BuildSkinned(LoadModel(glb, identity), identity));
        }

        /// <summary>Opt-in convenience: load a rigged glb/glTF AND auto-read its first textured material's
        /// baseColor/normal/metallicRoughness textures, decoded to raw RGBA8 (no GPU). The
        /// <see cref="SkinnedGltfMesh"/> is identical to <see cref="LoadSkinned(string)"/>. Only the extra
        /// <see cref="GltfMaterialMaps"/> are new. Upload them with <see cref="Scene3D.LoadSurfaceMaps"/> and pass
        /// the result to <see cref="Scene3D.LoadSkinnedMesh(SkinnedGltfMesh,Scene3D.SurfaceMaps)"/>. A material with
        /// no (or missing/undecodable) textures yields an all-absent <see cref="GltfMaterialMaps"/>, never a
        /// throw.</summary>
        public static (SkinnedGltfMesh Mesh, GltfMaterialMaps Maps) LoadSkinnedWithMaterial(string path) =>
            BuildSkinnedWithMaterial(LoadModel(path), path, fromFile: true);

        /// <summary>Load the flattened skinned mesh and decode the first textured material's maps from binary
        /// glTF bytes. Uses the same primitive order, skin and material selection as the path overload.
        /// Images can live in the GLB buffer or in data URIs. <paramref name="identity"/> names the asset
        /// in diagnostics.</summary>
        /// <exception cref="GltfImportDataException">The GLB is malformed or any image URI names an external
        /// resource, including an unused image. An external image refusal carries the asset identity and the
        /// zero-based image index.</exception>
        public static (SkinnedGltfMesh Mesh, GltfMaterialMaps Maps) LoadSkinnedWithMaterial(
            ReadOnlyMemory<byte> glb, string identity)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(identity);
            return TranslateModelErrors(identity,
                () => BuildSkinnedWithMaterial(LoadModel(glb, identity, requireEmbeddedImages: true), identity, fromFile: false));
        }

        static (SkinnedGltfMesh Mesh, GltfMaterialMaps Maps) BuildSkinnedWithMaterial(
            ModelRoot root, string identity, bool fromFile)
        {
            SkinnedGltfMesh mesh = BuildSkinned(root, identity);
            return (mesh, ReadMaterialMaps(FirstTexturedMaterial(root), fromFile ? AssetDirectory(identity) : null));
        }

        static SkinnedGltfMesh BuildSkinned(ModelRoot root, string path) =>
            DecodeSkinned(root, path, splitByMaterial: false)[0].Mesh;

        // Share material grouping and triangle rebasing between upload and index-only reads.
        static (List<SkinnedGeometry> Groups, Skin? Skin) ReadSkinnedGeometry(
            ModelRoot root, string path, bool splitByMaterial, bool readVertices, CoverageImportCollector? coverage = null)
        {
            var groups = new List<SkinnedGeometry>();
            Skin? skin = null;
            string identity = $"glTF asset '{path}'";

            foreach (var mesh in root.LogicalMeshes)
                foreach (var prim in mesh.Primitives)
                {
                    var position = prim.GetVertexAccessor("POSITION");
                    var joints = prim.GetVertexAccessor("JOINTS_0");
                    var weights = prim.GetVertexAccessor("WEIGHTS_0");
                    if (position == null || joints == null || weights == null) continue;

                    int partIndex = splitByMaterial
                        ? groups.FindIndex(g => ReferenceEquals(g.Material, prim.Material))
                        : groups.Count == 0 ? -1 : 0;
                    if (partIndex < 0)
                    {
                        partIndex = groups.Count;
                        groups.Add(new SkinnedGeometry(prim.Material));
                    }
                    SkinnedGeometry group = groups[partIndex];
                    int baseIndex = group.VertexCount;
                    int vertexCount = position.Count;
                    int sourceStart = 0;
                    if (readVertices)
                    {
                        var positions = position.AsVector3Array();
                        var jointIndices = joints.AsVector4Array();
                        var jointWeights = weights.AsVector4Array();
                        coverage?.VerifySkin(root, mesh, path);
                        // The first skin supplies the inverse binds and rest pose for every mesh.
                        skin ??= root.LogicalNodes.FirstOrDefault(n => n.Mesh == mesh && n.Skin != null)?.Skin
                                 ?? root.LogicalSkins.FirstOrDefault();
                        sourceStart = coverage?.AddSkinned(mesh, prim, partIndex, baseIndex, positions, jointIndices, jointWeights) ?? 0;
                        AppendSkinnedVertices(group, prim, positions, jointIndices, jointWeights);
                    }
                    group.VertexCount = checked(baseIndex + vertexCount);

                    int sourceTriangle = 0;
                    foreach (var (a, b, c) in prim.GetTriangleIndices())
                    {
                        group.Indices.Add(MeshIndexValidation.Rebase(a, vertexCount, baseIndex, path, partIndex: partIndex, diagnosticIdentity: identity));
                        group.Indices.Add(MeshIndexValidation.Rebase(b, vertexCount, baseIndex, path, partIndex: partIndex, diagnosticIdentity: identity));
                        group.Indices.Add(MeshIndexValidation.Rebase(c, vertexCount, baseIndex, path, partIndex: partIndex, diagnosticIdentity: identity));
                        coverage?.AddTriangle(mesh, prim, sourceTriangle++, partIndex, sourceStart, a, b, c);
                    }
                }

            return (groups, skin);
        }

        static void AppendSkinnedVertices(SkinnedGeometry group, MeshPrimitive prim, IList<Vector3> positions,
            IList<Vector4> joints, IList<Vector4> weights)
        {
            var normals = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
            var texcoords = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
            // A missing tangent is computed from the UVs and positions after the triangles are known.
            var tangents = prim.GetVertexAccessor("TANGENT")?.AsVector4Array();
            // Per-vertex COLOR_0 multiplies the material colour, as it does on the rigid path.
            var colors = prim.GetVertexAccessor("COLOR_0")?.AsColorArray();
            Vector4 baseColor = ReadBaseColor(prim.Material);

            for (int i = 0; i < positions.Count; i++)
            {
                group.Vertices.Add(new SkinnedVertex
                {
                    Position = positions[i],
                    Normal = normals != null && i < normals.Count ? normals[i] : Vector3.UnitY,
                    Color = colors != null && i < colors.Count ? baseColor * colors[i] : baseColor,
                    Uv = texcoords != null && i < texcoords.Count ? texcoords[i] : Vector2.Zero,
                    BoneIndices = joints[i],
                    BoneWeights = SkinningMath.NormalizeWeights(weights[i]),
                });
                group.SourceTangents.Add(tangents != null && i < tangents.Count ? tangents[i] : (Vector4?)null);
            }
        }

        static IReadOnlyList<(SkinnedGltfMesh Mesh, GltfMaterial? Material)> DecodeSkinned(
            ModelRoot root, string path, bool splitByMaterial, CoverageImportCollector? coverage = null)
        {
            var (groups, skin) = ReadSkinnedGeometry(root, path, splitByMaterial, readVertices: true, coverage);
            if (skin == null || groups.All(group => group.Vertices.Count == 0))
                throw new GltfImportStateException(GltfImportRefusal.NoSkinnedMesh,
                    "glTF has no skinned mesh (JOINTS_0/WEIGHTS_0 + skin): " + path)
                { Identity = path };

            foreach (SkinnedGeometry group in groups)
                ComputeSkinnedTangents(group.Vertices, group.Indices, group.SourceTangents);

            int boneCount = skin.JointsCount;

            // Reject a malformed/malicious rig at load. The CPU skinning path slices the bone palette to
            // exactly MaxBonesPerDraw and reads it unconditionally per vertex (SkinningMath.BlendSkinMatrix),
            // so an oversized joint count or an out-of-range JOINTS_0 index would otherwise throw mid-frame.
            if (boneCount > SkinningMath.MaxBonesPerDraw)
                throw new GltfImportStateException(GltfImportRefusal.SkinJointLimitExceeded,
                    $"glTF skin has {boneCount} joints, over the {SkinningMath.MaxBonesPerDraw}-bone per-draw cap: {path}")
                { Identity = path, JointCount = boneCount, MaximumJointCount = SkinningMath.MaxBonesPerDraw };
            for (int partIndex = 0; partIndex < groups.Count; partIndex++)
                for (int vertexIndex = 0; vertexIndex < groups[partIndex].Vertices.Count; vertexIndex++)
                {
                    SkinnedVertex v = groups[partIndex].Vertices[vertexIndex];
                    ValidateSkinJointIndices(v.BoneIndices, boneCount, path, vertexIndex, partIndex);
                }

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
                meshes.Add((new SkinnedGltfMesh(group.Vertices.ToArray(), group.Indices.ToArray(),
                    inverseBind, restPose, skeleton), group.Material));
            return meshes;
        }

        sealed class SkinnedGeometry
        {
            internal GltfMaterial? Material { get; }
            internal int VertexCount { get; set; }
            internal List<SkinnedVertex> Vertices { get; } = new();
            internal List<uint> Indices { get; } = new();
            internal List<Vector4?> SourceTangents { get; } = new();

            internal SkinnedGeometry(GltfMaterial? material) => Material = material;
        }
    }
}
