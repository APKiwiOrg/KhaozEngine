using System.Collections.Generic;
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

        /// <summary>Realize a <see cref="TerrainLayeredMaterial"/> into a shared splat material handle (uploads the
        /// two texture arrays + mip chains + params once). Pass the handle to <see cref="LoadTerrainChunk(Scene3D,
        /// TerrainChunkMesh, Scene3D.SplatMaterialHandle)"/> or a <see cref="Scene3DChunkSink"/>.</summary>
        public static Scene3D.SplatMaterialHandle LoadTerrainMaterial(this Scene3D scene, TerrainLayeredMaterial material)
        {
            material.Validate();
            var layers = new List<SplatLayerImage>(material.Layers.Count);
            foreach (var l in material.Layers)
                layers.Add(new SplatLayerImage
                {
                    AlbedoRgba = l.AlbedoRgba, NormalRgba = l.NormalRgba,
                    Tint = l.Tint, TilesPerMetre = l.TilesPerMetre, Roughness = l.Roughness,
                });
            return scene.LoadSplatMaterial(material.Width, material.Height, layers,
                material.TriplanarSharpness, material.Projection, material.BaseSpecStrength);
        }

        /// <summary>Upload a chunk and draw it through the splat-terrain pipeline with <paramref name="material"/>
        /// (the baked weights are packed into the mesh's vertex colour). The textured counterpart to
        /// <see cref="LoadTerrainChunk(Scene3D, TerrainChunkMesh)"/>.</summary>
        public static MeshHandle LoadTerrainChunk(this Scene3D scene, TerrainChunkMesh chunk, Scene3D.SplatMaterialHandle material) =>
            scene.LoadMesh(TerrainSplatPacking.PackedMesh(chunk), material);
    }
}
