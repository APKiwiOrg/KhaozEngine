using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>Scene3D glue for terrain chunks. A consumer with `using KhaozEngine.Terrain;` gets these in scope
    /// (same pattern as the Ground* telegraph extensions). Chunk vertices are already world-space, so the draw
    /// transform is identity; tint white lets the baked vertex-colour ramp through.</summary>
    public static class TerrainScene3D
    {
        /// <summary>Uploads a built chunk's mesh and returns its handle. Cache the handle; rebuild/unload cadence
        /// is the World streaming sub-project's concern.</summary>
        public static MeshHandle LoadTerrainChunk(this Scene3D scene, TerrainChunkMesh chunk) => scene.LoadMesh(chunk.Mesh);

        /// <summary>Queues a loaded terrain chunk for this frame at world origin (identity), tint white.</summary>
        public static void DrawTerrainChunk(this Scene3D scene, MeshHandle handle) =>
            scene.Draw(handle, Matrix4x4.Identity, Color.White);
    }
}
