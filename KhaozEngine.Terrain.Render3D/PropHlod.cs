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
        /// coarsening - call <see cref="Weld"/> (or <see cref="BuildMergedMesh"/>) to reduce the triangle count.
        /// <para>Both output arrays are sized EXACTLY, from a counting pass over the source meshes, before anything is
        /// written. A merge is a concatenation, so the totals are known up front and there is no reason to pay for a
        /// growing list: on a real cluster (the measured one is 41 props and 139,608 triangles) a doubling
        /// <see cref="List{T}"/> plus its closing <c>ToArray</c> spent roughly three times the final size in
        /// large-object allocations, all of it transient and none of it compacted (issue #393). The output is
        /// byte-identical either way, since the fill order is unchanged.</para>
        /// <para><b>The merged mesh always addresses its own vertices.</b> A source mesh with no vertices is skipped
        /// like an absent id, and a source corner pointing past its own mesh's vertex array is collapsed onto that
        /// mesh's first vertex instead of being rebased into nowhere. So no caller of the merged mesh (the weld, a GPU
        /// upload, a bake) can be handed an index past the end, which is what issue #402 crashed on. Both rules are
        /// the identity for a well-formed kit, so real output is unchanged.</para></summary>
        public static GltfMesh Merge(IReadOnlyList<PropPlacement> placements,
                                     IReadOnlyDictionary<string, GltfMesh> sourceMeshes)
        {
            if (placements == null) throw new ArgumentNullException(nameof(placements));
            if (sourceMeshes == null) throw new ArgumentNullException(nameof(sourceMeshes));

            int vertCount = 0, indexCount = 0;
            for (int p = 0; p < placements.Count; p++)
                if (Usable(sourceMeshes, placements[p].Id, out GltfMesh? m))
                {
                    vertCount += m.Vertices.Length;
                    indexCount += m.Indices32.Length;
                }

            var verts = new ModelVertex[vertCount];
            var idx = new uint[indexCount];
            int vi = 0, ii = 0;
            for (int p = 0; p < placements.Count; p++)
            {
                PropPlacement pl = placements[p];
                if (!Usable(sourceMeshes, pl.Id, out GltfMesh? mesh)) continue;

                Matrix4x4 rot = Matrix4x4.CreateRotationY(pl.Yaw);
                Matrix4x4 world = Matrix4x4.CreateScale(pl.Scale) * rot * Matrix4x4.CreateTranslation(pl.X, pl.Y, pl.Z);
                uint baseIndex = (uint)vi;
                ModelVertex[] mv = mesh.Vertices;
                for (int i = 0; i < mv.Length; i++)
                {
                    ModelVertex v = mv[i];
                    v.Position = Vector3.Transform(mv[i].Position, world);
                    v.Normal = Vector3.Normalize(Vector3.TransformNormal(mv[i].Normal, rot));
                    v.Tangent = Vector4.Zero;
                    verts[vi++] = v;
                }
                uint[] mi = mesh.Indices32;
                uint srcVerts = (uint)mv.Length;
                for (int i = 0; i < mi.Length; i++)
                {
                    uint s = mi[i];
                    // A source corner naming a vertex its own mesh does not have is collapsed onto that mesh's first
                    // vertex rather than rebased blind (issue #402). Rebasing it would put an index past the merged
                    // vertex array, which is what turned one malformed kit mesh into an IndexOutOfRangeException on a
                    // worker thread. The ternary is the identity for every well-formed mesh, so the merged output is
                    // byte-identical to before. A collapsed corner makes the triangle degenerate and Weld drops it.
                    idx[ii++] = baseIndex + (s < srcVerts ? s : 0u);
                }
            }
            return new GltfMesh(verts, idx);
        }

        /// <summary>A placement's source mesh, when the kit has one that can actually contribute geometry. A mesh with
        /// no vertices is skipped exactly like an absent id: it has nothing to merge, and admitting it would size the
        /// index array for corners that can never address a real vertex. Used by BOTH of <see cref="Merge"/>'s passes,
        /// so the counting pass and the fill pass cannot disagree about which placements are in.</summary>
        static bool Usable(IReadOnlyDictionary<string, GltfMesh> sourceMeshes, string id,
                           [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GltfMesh? mesh)
        {
            mesh = sourceMeshes.TryGetValue(id, out GltfMesh? m) && m != null && m.Vertices.Length > 0 ? m : null;
            return mesh != null;
        }

        /// <summary>Vertex-cluster weld decimation: collapse every vertex whose position quantizes to the same cubic
        /// cell of side <paramref name="cellSize"/> (metres) to one averaged vertex (position, normal, colour), rebuild
        /// the triangles against the collapsed vertices, and drop the triangles that degenerate (two or three corners
        /// welded together). A coarse, silhouette-preserving reduction that is adequate at HLOD range - trunks go
        /// blobby but canopy shape and colour hold. Deterministic: cells are assigned ids in first-seen order over the
        /// input vertices, so identical input yields a byte-identical result. Throws on a non-positive cell.
        /// <para>Three passes, none of which grows a list: assign cells, accumulate into exactly-sized arrays, then
        /// count the surviving triangles before filling an exactly-sized index array. The accumulate pass repeats the
        /// same additions in the same per-cell order as the single-pass form it replaced, so the float sums (and
        /// therefore the output) are bit-for-bit what they were.</para>
        /// <para><b>A malformed index buffer degrades, it does not throw.</b> A triangle with a corner past the end of
        /// the vertex array is dropped like a degenerate one (issue #402), so this never throws
        /// <see cref="IndexOutOfRangeException"/> whatever it is handed. Note that the weld CELL never indexes
        /// anything: cell keys go into a <see cref="Dictionary{TKey,TValue}"/> and cell ids are dense, so however the
        /// <c>(int)</c> cast of a quotient rounds on a given CPU, it cannot address out of range.</para></summary>
        public static GltfMesh Weld(GltfMesh mesh, float cellSize)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (cellSize <= 0f)
                throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Weld cell size must be positive.");

            ModelVertex[] src = mesh.Vertices;
            var cellOf = new Dictionary<(int, int, int), int>();
            var remap = new int[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                Vector3 p = src[i].Position;
                var key = ((int)MathF.Floor(p.X / cellSize),
                           (int)MathF.Floor(p.Y / cellSize),
                           (int)MathF.Floor(p.Z / cellSize));
                if (!cellOf.TryGetValue(key, out int id))
                {
                    id = cellOf.Count;   // first-seen order, the determinism contract
                    cellOf[key] = id;
                }
                remap[i] = id;
            }

            int cells = cellOf.Count;
            var accPos = new Vector3[cells];
            var accNrm = new Vector3[cells];
            var accCol = new Vector4[cells];
            var accCnt = new int[cells];
            for (int i = 0; i < src.Length; i++)
            {
                int id = remap[i];
                accPos[id] += src[i].Position; accNrm[id] += src[i].Normal; accCol[id] += src[i].Color; accCnt[id]++;
            }

            var outV = new ModelVertex[cells];
            for (int i = 0; i < outV.Length; i++)
            {
                float inv = 1f / accCnt[i];
                Vector3 n = accNrm[i] * inv;
                outV[i] = new ModelVertex(accPos[i] * inv,
                    n.LengthSquared() > 1e-8f ? Vector3.Normalize(n) : Vector3.UnitY,
                    accCol[i] * inv);
            }

            uint[] si = mesh.Indices32;
            uint vertexCount = (uint)src.Length;
            int kept = 0;
            for (int t = 0; t + 2 < si.Length; t += 3)
                if (Survives(si, t, vertexCount, remap, out _, out _, out _)) kept++;

            var outI = new uint[kept * 3];
            int o = 0;
            for (int t = 0; t + 2 < si.Length; t += 3)
            {
                if (!Survives(si, t, vertexCount, remap, out int a, out int b, out int c)) continue;
                outI[o++] = (uint)a; outI[o++] = (uint)b; outI[o++] = (uint)c;
            }
            return new GltfMesh(outV, outI);
        }

        /// <summary>Whether the triangle at <paramref name="t"/> makes it into the welded output, and its remapped
        /// corners when it does. A triangle is dropped when it collapsed (two or three corners welded into one cell)
        /// or when a corner names a vertex the mesh does not have.
        /// <para>That second case is issue #402: <c>remap</c> is sized by the VERTEX count and was indexed by the
        /// index buffer with no check, so a single out-of-range corner threw <see cref="IndexOutOfRangeException"/>
        /// out of a background chunk build. An unrepresentable corner cannot be welded to anything, so the triangle is
        /// dropped like a degenerate one instead: the mesh degrades by that triangle rather than the build dying. The
        /// comparison is unsigned, so <c>uint.MaxValue</c> reads as out of range and never as a negative offset.</para>
        /// <para>Both of <see cref="Weld"/>'s passes (count the survivors, then fill an exactly-sized array) run
        /// through here, so they cannot drift apart and overrun the output.</para></summary>
        static bool Survives(uint[] si, int t, uint vertexCount, int[] remap, out int a, out int b, out int c)
        {
            a = b = c = 0;
            uint ia = si[t], ib = si[t + 1], ic = si[t + 2];
            if (ia >= vertexCount || ib >= vertexCount || ic >= vertexCount) return false;
            a = remap[ia]; b = remap[ib]; c = remap[ic];
            return a != b && b != c && a != c;
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

        // ---- Crossfade draw gates (issue #405). Near either end of the band one of the two halves is dithered
        // almost fully away, so drawing it still pays full vertex/triangle cost for a side that contributes close to
        // nothing on screen. Both the individual-props dither (ShaderSources.Model's ModelFrag / the shadow depth
        // pass's ShadowDepthDissolveFrag) and the merged-mesh dither (the same frag, plus the issue #391 INVERTED
        // shadow variant ShadowDepthDissolveInvertedFrag) discard `mask < threshold` (or its complement) against a
        // world-space value-noise mask spread over the full 0..1 range, so a threshold's kept-fragment fraction is
        // ~ (1 - threshold): that is the documented reading of "threshold: 0 = solid .. 1 = gone" on IDissolve.x.
        //
        // The props half's threshold IS t (dissolveFloor below), so its kept fraction is ~ (1 - t): past t = 0.97
        // that is under 3 percent. The merged half's threshold is (1 - t) (hlodDissolve below, color and shadow
        // alike per the derivation in ShadowDepthDissolveInvertedFrag), so ITS kept fraction is ~ t: below t = 0.03
        // that is under 3 percent too. Both figures hold for the shadow pass as well as color, since the shadow
        // dither reads the very same threshold (plain for props, inverted for merged) - so gating the one Draw /
        // DrawProps call that feeds both the color instance and its shadow-caster registration keeps color and
        // shadow skipped together, with no separate shadow-side gate needed.

        /// <summary>The crossfade band's near-edge gate for the merged HLOD half (issue #405): below this its dither
        /// keeps only a ~t fraction of fragments (see the derivation above), under 3 percent below this threshold.</summary>
        public const float HlodMergedSkipAt = 0.03f;

        /// <summary>The crossfade band's far-edge gate for the individual-props half (issue #405): at or above this
        /// its dither keeps only a ~(1 - t) fraction of fragments (see the derivation above), under 3 percent from
        /// this threshold on.</summary>
        public const float HlodPropsSkipAt = 0.97f;

        /// <summary>True while the individual props half of an HLOD crossfade is still worth drawing at fade
        /// parameter <paramref name="t"/> (issue #405): below <see cref="HlodPropsSkipAt"/>. Also true for every t
        /// outside the band's far edge (t stays below 1 there), so a hard swap (<see cref="CrossfadeAt"/> with a
        /// non-positive width) is unaffected: it only ever produces t = 0 or t = 1 exactly, matching the pre-#405
        /// `t &lt; 1f` gate at both ends.</summary>
        public static bool DrawsHlodProps(float t) => t < HlodPropsSkipAt;

        /// <summary>True while the merged HLOD mesh half of a crossfade is still worth drawing at fade parameter
        /// <paramref name="t"/> (issue #405): above <see cref="HlodMergedSkipAt"/>. Mirrors <see cref="DrawsHlodProps"/>:
        /// a hard swap's t = 0 or t = 1 matches the pre-#405 `t &gt; 0f` gate at both ends.</summary>
        public static bool DrawsHlodMerged(float t) => t > HlodMergedSkipAt;
    }
}
