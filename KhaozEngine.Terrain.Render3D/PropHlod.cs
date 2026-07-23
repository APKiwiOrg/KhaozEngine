using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>Author-agnostic HLOD (hierarchical LOD) merge+weld for a cluster of scattered props. Given a
    /// cluster's <see cref="PropPlacement"/>s, a per-kit map of flat source <see cref="GltfMesh"/>es, and a weld
    /// cell size, it produces ONE coarse world-space mesh for the whole cluster: every placement's mesh is
    /// transformed to world space and concatenated (<see cref="Merge"/>), then a vertex-cluster weld collapses
    /// vertices that fall in the same cubic cell to cut the triangle count (<see cref="Weld"/>). The result renders
    /// as a single instanced draw through the existing untextured <see cref="Scene3D.Draw(MeshHandle, Matrix4x4, KhaozEngine.Primitives.Color)"/>
    /// vertex-colour path, so a far forest costs one draw and a few thousand triangles with no new shader, no texture
    /// atlas, and no impostor card (the L3 spike measured a 41-prop cluster from 139,608 tris / 12 draws down to
    /// 16,178 tris / 1 draw at a 1.5 m weld, fidelity holding at range).
    /// <para><b>Vertex-colour texturing.</b> The source meshes are the flat <see cref="PropLoader.LoadProp"/> form,
    /// whose per-vertex colour already carries each material's average albedo (texture-weighted), so the merged mesh
    /// keeps a sensible flat colour per region with zero extra texture memory. Feed a coarser source (an authored
    /// low-poly proxy or the L2 <c>LodFile</c> mesh) to skip or lighten the weld.</para>
    /// <para><b>Determinism.</b> Both steps are pure functions of their inputs and iterate the placement list in
    /// order (the source-mesh dictionary is only ever looked up by id, never iterated), so identical inputs yield a
    /// byte-identical mesh - the merge can run at bake time or at chunk load, cached per cluster, and reproduce.</para></summary>
    public static class PropHlod
    {
        /// <summary>Merge a cluster's props into one world-space mesh: for each placement whose <see cref="PropPlacement.Id"/>
        /// has a mesh in <paramref name="sourceMeshes"/>, transform that mesh by the placement's scale/yaw/translation
        /// (the same world matrix <see cref="PropRenderer"/> uses) and concatenate. A placement whose id is absent is
        /// skipped (per-kit opt-in). Normals are rotated by the yaw and tangents zeroed (the merged mesh lights by its
        /// geometric normal, like any untangented mesh); per-vertex colour and UV are carried through unchanged. No
        /// coarsening - call <see cref="Weld"/> (or <see cref="BuildMergedMesh"/>) to reduce the triangle count.</summary>
        public static GltfMesh Merge(IReadOnlyList<PropPlacement> placements,
                                     IReadOnlyDictionary<string, GltfMesh> sourceMeshes)
        {
            if (placements == null) throw new ArgumentNullException(nameof(placements));
            if (sourceMeshes == null) throw new ArgumentNullException(nameof(sourceMeshes));

            var verts = new List<ModelVertex>();
            var idx = new List<uint>();
            for (int p = 0; p < placements.Count; p++)
            {
                PropPlacement pl = placements[p];
                if (!sourceMeshes.TryGetValue(pl.Id, out GltfMesh? mesh) || mesh == null) continue;

                Matrix4x4 rot = Matrix4x4.CreateRotationY(pl.Yaw);
                Matrix4x4 world = Matrix4x4.CreateScale(pl.Scale) * rot * Matrix4x4.CreateTranslation(pl.X, pl.Y, pl.Z);
                uint baseIndex = (uint)verts.Count;
                ModelVertex[] mv = mesh.Vertices;
                for (int i = 0; i < mv.Length; i++)
                {
                    ModelVertex v = mv[i];
                    v.Position = Vector3.Transform(mv[i].Position, world);
                    v.Normal = Vector3.Normalize(Vector3.TransformNormal(mv[i].Normal, rot));
                    v.Tangent = Vector4.Zero;
                    verts.Add(v);
                }
                uint[] mi = mesh.Indices32;
                for (int i = 0; i < mi.Length; i++) idx.Add(baseIndex + mi[i]);
            }
            return new GltfMesh(verts.ToArray(), idx.ToArray());
        }

        /// <summary>Vertex-cluster weld decimation: collapse every vertex whose position quantizes to the same cubic
        /// cell of side <paramref name="cellSize"/> (metres) to one averaged vertex (position, normal, colour), rebuild
        /// the triangles against the collapsed vertices, and drop the triangles that degenerate (two or three corners
        /// welded together). A coarse, silhouette-preserving reduction that is adequate at HLOD range - trunks go
        /// blobby but canopy shape and colour hold. Deterministic: cells are assigned ids in first-seen order over the
        /// input vertices, so identical input yields a byte-identical result. Throws on a non-positive cell.</summary>
        public static GltfMesh Weld(GltfMesh mesh, float cellSize)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (cellSize <= 0f)
                throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Weld cell size must be positive.");

            var cellOf = new Dictionary<(int, int, int), int>();
            var accPos = new List<Vector3>();
            var accNrm = new List<Vector3>();
            var accCol = new List<Vector4>();
            var accCnt = new List<int>();
            var remap = new int[mesh.Vertices.Length];
            for (int i = 0; i < mesh.Vertices.Length; i++)
            {
                ModelVertex v = mesh.Vertices[i];
                var key = ((int)MathF.Floor(v.Position.X / cellSize),
                           (int)MathF.Floor(v.Position.Y / cellSize),
                           (int)MathF.Floor(v.Position.Z / cellSize));
                if (!cellOf.TryGetValue(key, out int id))
                {
                    id = accPos.Count;
                    cellOf[key] = id;
                    accPos.Add(Vector3.Zero); accNrm.Add(Vector3.Zero); accCol.Add(Vector4.Zero); accCnt.Add(0);
                }
                accPos[id] += v.Position; accNrm[id] += v.Normal; accCol[id] += v.Color; accCnt[id]++;
                remap[i] = id;
            }

            var outV = new ModelVertex[accPos.Count];
            for (int i = 0; i < outV.Length; i++)
            {
                float inv = 1f / accCnt[i];
                Vector3 n = accNrm[i] * inv;
                outV[i] = new ModelVertex(accPos[i] * inv,
                    n.LengthSquared() > 1e-8f ? Vector3.Normalize(n) : Vector3.UnitY,
                    accCol[i] * inv);
            }

            uint[] si = mesh.Indices32;
            var outI = new List<uint>();
            for (int t = 0; t + 2 < si.Length; t += 3)
            {
                int a = remap[si[t]], b = remap[si[t + 1]], c = remap[si[t + 2]];
                if (a == b || b == c || a == c) continue;   // collapsed / degenerate triangle
                outI.Add((uint)a); outI.Add((uint)b); outI.Add((uint)c);
            }
            return new GltfMesh(outV, outI.ToArray());
        }

        /// <summary>Build a cluster's coarse HLOD mesh in one call: <see cref="Merge"/> the placements, then
        /// <see cref="Weld"/> at <paramref name="weldCellSize"/> when it is positive (a non-positive cell keeps the
        /// full-detail merge, no decimation). This is the shape both consumption paths want - a runtime bake at chunk
        /// load (cached per cluster) or an offline artifact bake - since it is deterministic in
        /// (placements, meshes, cell). An empty or all-unknown-id cluster returns a mesh with zero triangles; the
        /// caller decides whether to upload it.</summary>
        public static GltfMesh BuildMergedMesh(IReadOnlyList<PropPlacement> placements,
                                               IReadOnlyDictionary<string, GltfMesh> sourceMeshes,
                                               float weldCellSize)
        {
            GltfMesh merged = Merge(placements, sourceMeshes);
            return weldCellSize > 0f ? Weld(merged, weldCellSize) : merged;
        }

        /// <summary>The HLOD crossfade curve: given a cluster's horizontal <paramref name="distance"/> to the focus,
        /// the layer's <paramref name="hlodDistance"/>, and a <paramref name="crossfadeWidth"/>, return the fade
        /// parameter t in 0..1. t is 0 up to the near edge (draw the full props, HLOD hidden), ramps linearly across
        /// the band centred on <paramref name="hlodDistance"/>, and is 1 past the far edge (props hidden, HLOD solid).
        /// The individual props dissolve toward 1 as t rises and the merged mesh dissolves toward 0 (fades in) as
        /// (1 - t) falls, so the two swap with the 14.5.0 rigid-dissolve primitive - deterministic by distance, no
        /// per-frame randomness. A non-positive width is a hard swap at <paramref name="hlodDistance"/>.</summary>
        public static float CrossfadeAt(float distance, float hlodDistance, float crossfadeWidth)
        {
            if (crossfadeWidth <= 0f) return distance < hlodDistance ? 0f : 1f;
            float half = crossfadeWidth * 0.5f;
            float lo = hlodDistance - half;
            return Math.Clamp((distance - lo) / crossfadeWidth, 0f, 1f);
        }
    }
}
