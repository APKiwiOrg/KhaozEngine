using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

/// <summary>Value metadata needed to publish a mesh after its detached inputs have been released.</summary>
internal readonly record struct SceneUploadPublication(MeshBounds Bounds, int IndexCount, GpuIndexFormat IndexFormat, CoverageUploadEvidence Coverage,
    float AlphaCutoff = 0, bool Ground = false, TerrainSamplerConfig? Sampler = null, bool OpaqueFoliage = false)
{
    internal static SceneUploadPublication From(GltfMesh mesh)
    {
        var coverage = new CoverageUploadEvidence(mesh);
        return new(coverage.Bounds, mesh.Indices32.Length, mesh.IndexFormat, coverage,
            OpaqueFoliage: FoliageMaterial.IsOpaque(false, 0, mesh.Vertices));
    }

    /// <summary>The sets publication creates. <paramref name="material"/> is whether the mesh has a material set:
    /// any map, or a glow, which binds its own parameter block.</summary>
    internal int BindingCount(bool albedo, bool material) =>
        Ground ? 1 + (Sampler.HasValue ? 1 : 0) : MaterialBindings(albedo, material, AlphaCutoff);

    /// <summary>The model material set, the outline set an albedo map needs, and the shadow cutout set an albedo map
    /// with an alpha cutoff needs.</summary>
    internal static int MaterialBindings(bool albedo, bool material, float alphaCutoff) =>
        (material ? 1 : 0) + (albedo ? 1 : 0) + (albedo && alphaCutoff > 0 ? 1 : 0);
}
