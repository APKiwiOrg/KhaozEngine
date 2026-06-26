using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>The CPU output of meshing one chunk: a Render3D GltfMesh (vertex colours = the height/slope ramp),
    /// a parallel per-vertex splat-weight array (plumbed for the later PBR upgrade), an AABB for culling, and the
    /// LOD/region it was built at. Hand Mesh to Scene3D.LoadMesh (or the TerrainScene3D extensions).</summary>
    public sealed class TerrainChunkMesh
    {
        public GltfMesh Mesh { get; }
        public TerrainSplatWeights[] Splat { get; }
        public TerrainChunkBounds Bounds { get; }
        public int Lod { get; }
        public TerrainChunkRegion Region { get; }
        /// <summary>Number of leading grid (surface) vertices before the appended skirt vertices.</summary>
        public int SurfaceVertexCount { get; }

        public TerrainChunkMesh(GltfMesh mesh, TerrainSplatWeights[] splat, TerrainChunkBounds bounds, int lod, TerrainChunkRegion region, int surfaceVertexCount)
        {
            Mesh = mesh; Splat = splat; Bounds = bounds; Lod = lod; Region = region; SurfaceVertexCount = surfaceVertexCount;
        }
    }
}
