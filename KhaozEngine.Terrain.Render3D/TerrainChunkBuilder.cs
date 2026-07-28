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
            // Exact sizes, not capacities: the surface is a cols x cols grid and each of the four skirts adds one
            // dropped copy of its edge (cols vertices, res quads), so every total is known before a byte is written.
            // Filling arrays directly is what removes the closing ToArray on each of the three buffers, and with it a
            // second full ModelVertex[] copy of every streamed chunk (issue #393).
            int vertCount = cols * cols + cols * 4;
            int indexCount = res * res * 6 + res * 4 * 6;
            var verts = new ModelVertex[vertCount];
            var splat = new TerrainSplatWeights[vertCount];
            var inds = new uint[indexCount];
            int vi = 0, ii = 0;

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
                verts[vi] = new ModelVertex(new Vector3(lx, h, lz), n, TerrainRamp.Of(w), new Vector2((float)ix / res, (float)iz / res));
                splat[vi] = w;
                vi++;
            }
            for (int iz = 0; iz < res; iz++)
            for (int ix = 0; ix < res; ix++)
            {
                uint i0 = (uint)(iz * cols + ix);
                uint i1 = (uint)(iz * cols + ix + 1);
                uint i2 = (uint)((iz + 1) * cols + ix);
                uint i3 = (uint)((iz + 1) * cols + ix + 1);
                inds[ii++] = i0; inds[ii++] = i2; inds[ii++] = i3;
                inds[ii++] = i0; inds[ii++] = i3; inds[ii++] = i1;
            }

            int surfaceVertexCount = vi;

            // --- skirts: drop a copy of each edge vertex by skirtDepth and stitch a vertical strip ------------
            uint Grid(int ix, int iz) => (uint)(iz * cols + ix);
            // One scratch buffer for the four skirts: every edge is the same length, and the array is fully rewritten
            // before it is read, so reusing it saves three allocations per chunk and changes nothing.
            var lower = new uint[cols];
            void Skirt(int[] edgeIx, int[] edgeIz, bool flip)
            {
                int count = edgeIx.Length;
                for (int k = 0; k < count; k++)
                {
                    uint top = Grid(edgeIx[k], edgeIz[k]);
                    ModelVertex tv = verts[(int)top];
                    Vector3 p = tv.Position; p.Y -= skirtDepth;
                    lower[k] = (uint)vi;
                    verts[vi] = new ModelVertex(p, tv.Normal, tv.Color, tv.Uv);
                    splat[vi] = splat[(int)top];
                    vi++;
                }
                for (int k = 0; k < count - 1; k++)
                {
                    uint t0 = Grid(edgeIx[k], edgeIz[k]), t1 = Grid(edgeIx[k + 1], edgeIz[k + 1]);
                    uint b0 = lower[k], b1 = lower[k + 1];
                    if (!flip) { inds[ii++] = t0; inds[ii++] = b0; inds[ii++] = b1; inds[ii++] = t0; inds[ii++] = b1; inds[ii++] = t1; }
                    else { inds[ii++] = t0; inds[ii++] = b1; inds[ii++] = b0; inds[ii++] = t0; inds[ii++] = t1; inds[ii++] = b1; }
                }
            }

            var rng = new int[cols];
            var zeros = new int[cols];
            var maxs = new int[cols];
            for (int i = 0; i <= res; i++) { rng[i] = i; zeros[i] = 0; maxs[i] = res; }
            Skirt(rng, zeros, flip: false);   // -Z edge (iz = 0)
            Skirt(rng, maxs, flip: true);     // +Z edge (iz = res)
            Skirt(zeros, rng, flip: true);    // -X edge (ix = 0)
            Skirt(maxs, rng, flip: false);    // +X edge (ix = res)

            var mesh = new GltfMesh(verts, inds);
            var bounds = TerrainChunkBounds.FromPositions(verts);
            return new TerrainChunkMesh(mesh, splat, bounds, lod, region, surfaceVertexCount);
        }
    }
}
