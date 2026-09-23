using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

/// <summary>Skinned mesh upload transaction and retained per-mesh material state.</summary>
public sealed partial class Scene3D
{
    /// <summary>Upload a skinned mesh to the GPU once. Returns a handle to draw it with
    /// <see cref="DrawSkinned(KhaozEngine.Render3D.SkinnedMeshHandle, System.ReadOnlySpan{System.Numerics.Matrix4x4}, System.Numerics.Matrix4x4, KhaozEngine.Primitives.Color)"/>.
    /// Untextured meshes sample the 1x1 white default, so their colour is the baked vertex colour times any
    /// per-instance tint.</summary>
    public SkinnedMeshHandle LoadSkinnedMesh(SkinnedGltfMesh mesh) =>
        LoadSkinnedInternal(mesh, null, null, null, 0f);

    /// <summary>Upload a skinned mesh and bind <paramref name="texture"/> as its albedo
    /// (<c>texRgb * vColor * vTint</c>). An invalid handle falls back to untextured.</summary>
    public SkinnedMeshHandle LoadSkinnedMesh(SkinnedGltfMesh mesh, TextureHandle texture)
    {
        IGpuTexture? albedo = texture.IsValid ? _textures[texture.ListIndex] : null;
        return LoadSkinnedInternal(mesh, albedo, null, null, 0f);
    }

    /// <summary>Upload a skinned mesh and bind a full PBR-lite material (<paramref name="maps"/>): albedo +
    /// optional normal + optional roughness, mirroring <see cref="LoadMesh(GltfMesh,SurfaceMaps)"/>. Invalid
    /// handles fall back to the renderer defaults (white albedo / flat normal / zero roughness). Normal
    /// perturbation requires the mesh to carry tangents. Skinned glTF via <see cref="GltfLoader.LoadSkinned"/> or
    /// <see cref="SkinnedMeshBuilder"/> output both compute them. A tangent-less skinned vertex is lit by its
    /// geometric normal. The tangent rides the skin deform on either skinning path, so the TBN tracks the pose.</summary>
    public SkinnedMeshHandle LoadSkinnedMesh(SkinnedGltfMesh mesh, SurfaceMaps maps)
    {
        IGpuTexture? albedo = maps.Albedo.IsValid ? _textures[maps.Albedo.ListIndex] : null;
        IGpuTexture? normal = maps.Normal.IsValid ? _textures[maps.Normal.ListIndex] : null;
        IGpuTexture? roughness = maps.Roughness.IsValid ? _textures[maps.Roughness.ListIndex] : null;
        return LoadSkinnedInternal(mesh, albedo, normal, roughness, maps.AlphaCutoff);
    }

    SkinnedMeshHandle LoadSkinnedInternal(
        SkinnedGltfMesh mesh,
        IGpuTexture? albedo,
        IGpuTexture? normal,
        IGpuTexture? roughness,
        float alphaCutoff)
    {
        IGpuBuffer? vertexBuffer = null;
        IGpuBuffer? indexBuffer = null;
        IGpuResourceSet? material = null;
        IGpuResourceSet? skinnedMaterial = null;
        IGpuResourceSet? outlineMaterial = null;
        try
        {
            vertexBuffer = _gd.Factory.CreateBuffer(new GpuBufferDescription(
                (uint)(mesh.Vertices.Length * SkinnedVertex.SizeInBytes), GpuBufferUsage.VertexBuffer));
            _gd.UpdateBuffer(vertexBuffer, 0, mesh.Vertices);
            indexBuffer = CreateIndexBuffer(mesh.Indices32, mesh.IndexFormat);

            bool hasOrdinaryMap = albedo is not null || normal is not null || roughness is not null;
            material = hasOrdinaryMap ? _model.CreateMaterialSet(albedo, normal, roughness) : null;
            skinnedMaterial = hasOrdinaryMap ? _model.CreateSkinnedMaterialSet(albedo, normal, roughness) : null;
            outlineMaterial = albedo is null ? null : _targetOutlines.CreateMaterialSet(albedo);

            MeshBounds bounds = MeshBounds.FromVertices(mesh.Vertices);
            var entry = new SkinnedMeshEntry(
                vertexBuffer, indexBuffer, mesh.Indices32.Length, mesh.IndexFormat,
                material, skinnedMaterial, outlineMaterial, alphaCutoff, mesh.InverseBind, in bounds);
            int index = _skinnedSlots.Alloc(out int generation);
            if (index < _skinnedMeshes.Count)
            {
                _skinnedMeshes[index] = entry;
                _skinnedCpuVerts[index] = mesh.Vertices;
            }
            else
            {
                _skinnedMeshes.Add(entry);
                _skinnedCpuVerts.Add(mesh.Vertices);
            }
            return new SkinnedMeshHandle(index, generation);
        }
        catch
        {
            vertexBuffer?.Dispose();
            indexBuffer?.Dispose();
            material?.Dispose();
            skinnedMaterial?.Dispose();
            outlineMaterial?.Dispose();
            throw;
        }
    }

    /// <summary>Number of skinned mesh slots still holding a live GPU mesh. For lifetime diagnostics.</summary>
    internal int LiveSkinnedMeshCount
    {
        get { int count = 0; foreach (SkinnedMeshEntry? mesh in _skinnedMeshes) if (mesh is not null) count++; return count; }
    }

    internal float SkinnedAlphaCutoffAt(SkinnedMeshHandle handle) =>
        _skinnedMeshes[handle.Index]!.AlphaCutoff;
    internal IGpuResourceSet? SkinnedCpuMaterialSetAt(SkinnedMeshHandle handle) =>
        _skinnedMeshes[handle.Index]?.MaterialSet;
    internal IGpuResourceSet? SkinnedGpuMaterialSetAt(SkinnedMeshHandle handle) =>
        _skinnedMeshes[handle.Index]?.SkinnedMaterialSet;
    internal IGpuResourceSet? SkinnedOutlineMaterialSetAt(SkinnedMeshHandle handle) =>
        _skinnedMeshes[handle.Index]?.OutlineMaterialSet;

    sealed class SkinnedMeshEntry
    {
        public readonly IGpuBuffer Vb, Ib;
        public readonly int IndexCount;
        public readonly GpuIndexFormat IndexFormat;
        public readonly IGpuResourceSet? MaterialSet;
        public readonly IGpuResourceSet? SkinnedMaterialSet;
        public readonly IGpuResourceSet? OutlineMaterialSet;
        public readonly float AlphaCutoff;
        public readonly Matrix4x4[] InverseBind;
        public readonly MeshBounds Bounds;

        public SkinnedMeshEntry(
            IGpuBuffer vb,
            IGpuBuffer ib,
            int indexCount,
            GpuIndexFormat indexFormat,
            IGpuResourceSet? materialSet,
            IGpuResourceSet? skinnedMaterialSet,
            IGpuResourceSet? outlineMaterialSet,
            float alphaCutoff,
            Matrix4x4[] inverseBind,
            in MeshBounds bounds)
        {
            Vb = vb;
            Ib = ib;
            IndexCount = indexCount;
            IndexFormat = indexFormat;
            MaterialSet = materialSet;
            SkinnedMaterialSet = skinnedMaterialSet;
            OutlineMaterialSet = outlineMaterialSet;
            AlphaCutoff = alphaCutoff;
            InverseBind = inverseBind;
            Bounds = bounds;
        }
    }
}
