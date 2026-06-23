using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D.Rendering;
using SharpGLTF.Schema2;
// KhaozEngine.Render3D now defines its own Material struct; alias the glTF one to disambiguate.
using GltfMaterial = SharpGLTF.Schema2.Material;

namespace KhaozEngine.Render3D
{
    /// <summary>Loads a glTF/GLB at runtime via SharpGLTF into a flat-shaded <see cref="GltfMesh"/>.
    /// Reads POSITION/NORMAL/TEXCOORD_0/TANGENT; a missing TANGENT is computed from UV+position by
    /// MeshAssembler. Material textures (normal/roughness) are bound separately via Scene3D.SurfaceMaps,
    /// not auto-read.</summary>
    public static class GltfLoader
    {
        public static GltfMesh Load(string path)
        {
            ModelRoot root = ModelRoot.Load(path);
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
        /// gradient keeps a zero tangent (lit by the geometric normal). Embedded images are ignored (bind a PNG
        /// albedo separately, as with <see cref="Load"/>). Throws if the mesh has no skin/joint data (use
        /// <see cref="Load"/> for rigid meshes). Indexed directly (no re-weld) so joints/weights stay aligned to
        /// their vertices; emits 32-bit indices so rigs past the 65,536-vertex ceiling load
        /// (<see cref="SkinnedGltfMesh"/> picks the GPU index width).</summary>
        public static SkinnedGltfMesh LoadSkinned(string path)
        {
            ModelRoot root = ModelRoot.Load(path);

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
    }
}
