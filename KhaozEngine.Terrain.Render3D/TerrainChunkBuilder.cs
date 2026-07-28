using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>Meshes one finite chunk off the analytic field at a chosen LOD: a (res+1)^2 grid of
    /// field-sampled vertices (position/normal/ramp-colour/splat), CCW-from-above indices, plus ~0.3 m edge
    /// skirts that hide cracks where a dense chunk meets a coarse neighbour. CPU only - no GPU device. Output is
    /// standard Render3D mesh data plus a parallel splat array and an AABB.
    /// <para>Vertices are CHUNK-LOCAL in X and Z: the field is sampled at the absolute world coordinate (it is
    /// authored in world space and stays that way), but what is STORED is <c>x - region.OriginX</c>, so a vertex
    /// magnitude is at most <see cref="TerrainChunkRegion.Size"/> however far out the chunk sits. Y is absolute
    /// world height, always. The region origin travels in the draw transform
    /// (<c>DrawTerrainChunk(scene, handle, region)</c>) and in the collision static's pose
    /// (<see cref="ChunkTerrainCollision"/>), so a chunk 100 km out is bit-for-bit as precise as one at the origin
    /// instead of quantized to that magnitude's 7.8 mm float32 lattice at BAKE time. Before this, the placement
    /// was baked into the vertex and no camera-relative render or physics rebase could recover it: the error was
    /// already in the buffer. <see cref="TerrainChunkMesh.Bounds"/> follows the vertices and is therefore
    /// chunk-local too.</para>
    /// <para>The per-vertex material mix comes from <see cref="TerrainSplatWeights.From"/> unless the caller supplies
    /// a <c>splatRule</c>, which sees each vertex's inputs plus the engine's own result and returns the weights to
    /// bake (<see cref="TerrainSplatContext"/> carries the full contract). That is how a world with a SECOND body of
    /// water gets a shoreline at all: <c>From</c> derives its sand band from the field's single water level.</para></summary>
    public static class TerrainChunkBuilder
    {
        /// <summary>Mesh a chunk at <paramref name="lod"/> using the default LOD tier table
        /// (<see cref="TerrainLodConfig.Default"/>). Byte-identical to the pre-data-driven behaviour for tiers 0/1/2.
        /// <para><paramref name="splatRule"/> is the optional consumer rule for the per-vertex material mix; null (the
        /// default) bakes exactly what <see cref="TerrainSplatWeights.From"/> produces. Contract:
        /// <see cref="TerrainSplatContext"/>.</para></summary>
        public static TerrainChunkMesh Build(TerrainField field, TerrainChunkRegion region, int lod, float skirtDepth = 0.3f, float snowLine = 60f,
                                             Func<TerrainSplatContext, TerrainSplatWeights>? splatRule = null)
            => Build(field, region, lod, TerrainLodConfig.Default, skirtDepth, snowLine, splatRule);

        /// <summary>Mesh a chunk at <paramref name="lod"/>, resolving the tier's grid resolution through
        /// <paramref name="lodConfig"/> (so a game's custom tier table meshes at its own resolutions). The
        /// <paramref name="lodConfig"/> must be the same one the streamer picks tiers with, or a tier index means a
        /// different resolution on each side.
        /// <para><paramref name="splatRule"/> is the optional consumer rule for the per-vertex material mix. Null (the
        /// default) bakes exactly what the engine's own <see cref="TerrainSplatWeights.From"/> produces, so a caller
        /// that supplies nothing is byte-identical to the pre-rule builder. The full contract is on
        /// <see cref="TerrainSplatContext"/>, and all three parts of it are load-bearing: the rule must be PURE
        /// (each chunk is meshed independently, per region and LOD, off the frame thread, so an impure rule bakes
        /// neighbours that disagree at their shared edge), it runs on a HOT PATH (once per vertex of every streamed
        /// chunk), and it is PRESENTATION ONLY (no field, collision, document, or world-identity impact).</para></summary>
        public static TerrainChunkMesh Build(TerrainField field, TerrainChunkRegion region, int lod, TerrainLodConfig lodConfig, float skirtDepth = 0.3f, float snowLine = 60f,
                                             Func<TerrainSplatContext, TerrainSplatWeights>? splatRule = null)
        {
            int res = lodConfig.ResolutionFor(lod);
            int cols = res + 1;
            var verts = new List<ModelVertex>(cols * cols + cols * 4);
            var splat = new List<TerrainSplatWeights>(cols * cols + cols * 4);
            var inds = new List<uint>(res * res * 6 + res * 4 * 6);

            // --- surface grid -------------------------------------------------
            for (int iz = 0; iz <= res; iz++)
            for (int ix = 0; ix <= res; ix++)
            {
                // Sample ABSOLUTE (the field is authored in world space), store CHUNK-LOCAL (see the class doc).
                float lx = (float)ix / res * region.Size;
                float lz = (float)iz / res * region.Size;
                float x = region.OriginX + lx;
                float z = region.OriginZ + lz;
                float h = field.SampleHeight(x, z);
                var n = field.SampleNormal(x, z);
                float slope01 = 1f - n.Y;
                BiomeId biome = field.SampleBiome(x, z);
                var w = TerrainSplatWeights.From(h, slope01, biome, field.WaterLevel, snowLine);
                // Consumer splat rule (issue #373), presentation only. Null is the whole pre-rule path: the engine's
                // weights go straight into the vertex, so a consumer that supplies nothing bakes byte-identical
                // meshes. The rule sees the engine's own result as Default so "the engine's mix plus a sand band"
                // does not have to reimplement (and then drift from) TerrainSplatWeights.From.
                if (splatRule is not null) w = splatRule(new TerrainSplatContext(h, slope01, biome, x, z, w));
                verts.Add(new ModelVertex(new Vector3(lx, h, lz), n, TerrainRamp.Of(w), new Vector2((float)ix / res, (float)iz / res)));
                splat.Add(w);
            }
            for (int iz = 0; iz < res; iz++)
            for (int ix = 0; ix < res; ix++)
            {
                uint i0 = (uint)(iz * cols + ix);
                uint i1 = (uint)(iz * cols + ix + 1);
                uint i2 = (uint)((iz + 1) * cols + ix);
                uint i3 = (uint)((iz + 1) * cols + ix + 1);
                inds.Add(i0); inds.Add(i2); inds.Add(i3);
                inds.Add(i0); inds.Add(i3); inds.Add(i1);
            }

            int surfaceVertexCount = verts.Count;

            // --- skirts: drop a copy of each edge vertex by skirtDepth and stitch a vertical strip ------------
            uint Grid(int ix, int iz) => (uint)(iz * cols + ix);
            void Skirt(IReadOnlyList<int> edgeIx, IReadOnlyList<int> edgeIz, bool flip)
            {
                int count = edgeIx.Count;
                var lower = new uint[count];
                for (int k = 0; k < count; k++)
                {
                    uint top = Grid(edgeIx[k], edgeIz[k]);
                    var tv = verts[(int)top];
                    var p = tv.Position; p.Y -= skirtDepth;
                    lower[k] = (uint)verts.Count;
                    verts.Add(new ModelVertex(p, tv.Normal, tv.Color, tv.Uv));
                    splat.Add(splat[(int)top]);
                }
                for (int k = 0; k < count - 1; k++)
                {
                    uint t0 = Grid(edgeIx[k], edgeIz[k]), t1 = Grid(edgeIx[k + 1], edgeIz[k + 1]);
                    uint b0 = lower[k], b1 = lower[k + 1];
                    if (!flip) { inds.Add(t0); inds.Add(b0); inds.Add(b1); inds.Add(t0); inds.Add(b1); inds.Add(t1); }
                    else { inds.Add(t0); inds.Add(b1); inds.Add(b0); inds.Add(t0); inds.Add(t1); inds.Add(b1); }
                }
            }

            var rng = new List<int>();
            for (int i = 0; i <= res; i++) rng.Add(i);
            var zeros = new List<int>(); for (int i = 0; i <= res; i++) zeros.Add(0);
            var maxs = new List<int>(); for (int i = 0; i <= res; i++) maxs.Add(res);
            Skirt(rng, zeros, flip: false);   // -Z edge (iz = 0)
            Skirt(rng, maxs, flip: true);     // +Z edge (iz = res)
            Skirt(zeros, rng, flip: true);    // -X edge (ix = 0)
            Skirt(maxs, rng, flip: false);    // +X edge (ix = res)

            var vertArr = verts.ToArray();
            var mesh = new GltfMesh(vertArr, inds.ToArray());
            var bounds = TerrainChunkBounds.FromPositions(vertArr);
            return new TerrainChunkMesh(mesh, splat.ToArray(), bounds, lod, region, surfaceVertexCount);
        }
    }
}
