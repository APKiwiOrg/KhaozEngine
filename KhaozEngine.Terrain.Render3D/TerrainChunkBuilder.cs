using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>Meshes one finite chunk off the analytic field at a chosen LOD: a (res+1)^2 grid of
    /// field-sampled vertices (position/normal/ramp-colour/splat), CCW-from-above indices, plus ~0.3 m edge
    /// skirts that hide cracks where a dense chunk meets a coarse neighbour. CPU only - no GPU device. Output is
    /// standard Render3D mesh data plus a parallel splat array and an AABB.</summary>
    public static class TerrainChunkBuilder
    {
        public static TerrainChunkMesh Build(TerrainField field, TerrainChunkRegion region, int lod, float skirtDepth = 0.3f, float snowLine = 60f)
        {
            int res = TerrainLod.ResolutionFor(lod);
            int cols = res + 1;
            var verts = new List<ModelVertex>(cols * cols + cols * 4);
            var splat = new List<TerrainSplatWeights>(cols * cols + cols * 4);
            var inds = new List<uint>(res * res * 6 + res * 4 * 6);

            // --- surface grid -------------------------------------------------
            for (int iz = 0; iz <= res; iz++)
            for (int ix = 0; ix <= res; ix++)
            {
                float x = region.OriginX + (float)ix / res * region.Size;
                float z = region.OriginZ + (float)iz / res * region.Size;
                float h = field.SampleHeight(x, z);
                var n = field.SampleNormal(x, z);
                float slope01 = 1f - n.Y;
                var w = TerrainSplatWeights.From(h, slope01, field.SampleBiome(x, z), field.WaterLevel, snowLine);
                verts.Add(new ModelVertex(new Vector3(x, h, z), n, TerrainRamp.Of(w), new Vector2((float)ix / res, (float)iz / res)));
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
