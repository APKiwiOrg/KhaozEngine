using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>Packs the baked per-vertex <see cref="TerrainSplatWeights"/> into a mesh the splat pipeline reads:
    /// the four leading weights (grass/dirt/rock/sand) ride in <see cref="ModelVertex.Color"/> (full float), and the
    /// shader reconstructs snow as 1 - sum. Pure; headless-testable. The untextured path is unchanged (it keeps the
    /// ramp Color the builder bakes).</summary>
    public static class TerrainSplatPacking
    {
        /// <summary>Pack the four leading weights into an RGBA colour (snow is derived in the shader).</summary>
        public static Vector4 Pack(in TerrainSplatWeights w) => new(w.Grass, w.Dirt, w.Rock, w.Sand);

        /// <summary>Build a render mesh whose vertex <c>Color</c> carries the packed splat weights (Position/Normal/
        /// Uv/Tangent copied from the chunk's mesh; indices shared). Hand the result to
        /// <c>Scene3D.LoadMesh(mesh, SplatMaterialHandle)</c>.</summary>
        public static GltfMesh PackedMesh(TerrainChunkMesh chunk)
        {
            var src = chunk.Mesh.Vertices;
            var splat = chunk.Splat;
            var verts = new ModelVertex[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                var v = src[i];
                verts[i] = new ModelVertex(v.Position, v.Normal, Pack(splat[i]), v.Uv, v.Tangent);
            }
            return new GltfMesh(verts, chunk.Mesh.Indices32);
        }
    }
}
