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
    /// <summary>Loads a glTF/GLB at runtime via SharpGLTF into a flat-shaded <see cref="GltfMesh"/>.</summary>
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
                // NORMAL / TEXCOORD_0 if present (SharpGLTF exposes the standard glTF attributes by name, same
                // accessor pattern as POSITION). Source normals are honoured so the artist's hard edges survive;
                // when absent, MeshAssembler computes a smooth normal from winding.
                var srcNormals = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
                var texcoords = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
                Vector4 baseColor = ReadBaseColor(prim.Material);

                Vector3? Norm(int i) => srcNormals != null && i < srcNormals.Count ? srcNormals[i] : (Vector3?)null;
                Vector2 Uv(int i) => texcoords != null && i < texcoords.Count ? texcoords[i] : Vector2.Zero;

                foreach (var (a, b, c) in prim.GetTriangleIndices())
                {
                    corners.Add(new MeshCorner(pos[a], Norm(a), baseColor, Uv(a)));
                    corners.Add(new MeshCorner(pos[b], Norm(b), baseColor, Uv(b)));
                    corners.Add(new MeshCorner(pos[c], Norm(c), baseColor, Uv(c)));
                }
            }
            if (corners.Count == 0) throw new InvalidOperationException("glTF has no triangles: " + path);

            return MeshAssembler.Build(corners);
        }

        /// <summary>Load a rigged glb/glTF as a <see cref="SkinnedGltfMesh"/>: reads POSITION/NORMAL/TEXCOORD_0
        /// plus JOINTS_0/WEIGHTS_0 and the skin's inverse-bind matrices + rest-pose joint world transforms.
        /// Embedded images are ignored (bind a PNG albedo separately, as with <see cref="Load"/>). Throws if the
        /// mesh has no skin/joint data (use <see cref="Load"/> for rigid meshes). Indexed directly (no re-weld) so
        /// joints/weights stay aligned to their vertices; emits 32-bit indices so rigs past the 65,536-vertex
        /// ceiling load (<see cref="SkinnedGltfMesh"/> picks the GPU index width).</summary>
        public static SkinnedGltfMesh LoadSkinned(string path)
        {
            ModelRoot root = ModelRoot.Load(path);

            var verts = new List<SkinnedVertex>();
            var indices = new List<uint>();
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
