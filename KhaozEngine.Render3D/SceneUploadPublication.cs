using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

/// <summary>Value metadata needed to publish a mesh after its detached inputs have been released.</summary>
internal readonly record struct SceneUploadPublication(MeshBounds Bounds, int IndexCount, GpuIndexFormat IndexFormat,
    float AlphaCutoff = 0, bool Ground = false, TerrainSamplerConfig? Sampler = null)
{
    internal static SceneUploadPublication From(GltfMesh mesh) => new(MeshBounds.FromVertices(mesh.Vertices),
        mesh.Indices32.Length, mesh.IndexFormat);

    internal int BindingCount(bool albedo, bool normal, bool roughness) => Ground ? 1 + (Sampler.HasValue ? 1 : 0) :
        (albedo || normal || roughness ? 1 : 0) + (albedo ? 1 : 0) + (albedo && AlphaCutoff > 0 ? 1 : 0);
}
