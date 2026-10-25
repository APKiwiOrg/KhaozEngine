using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using TileGroundMaterialHandle = KhaozEngine.Render3D.Scene3D.TileGroundMaterialHandle;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Forwards every call a TileWorld view makes to the shipped scene adapter, and records the ground
/// meshes it draws, the water planes it submits and the triangle fields it uploads. The recorded meshes are the
/// exact instances handed to the GPU, so an oracle over them reads the drawn geometry.</summary>
internal sealed class TileWorldWaterTopologyRecorder : ITileWorldScene, ITileWorldWaterTriangleOpticsScene
{
    readonly ITileWorldScene _inner;
    readonly ITileWorldWaterTriangleOpticsScene _optics;
    readonly Dictionary<(int Index, int Generation), GltfMesh> _meshes = new();

    internal TileWorldWaterTopologyRecorder(ITileWorldScene inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _optics = inner as ITileWorldWaterTriangleOpticsScene
            ?? throw new ArgumentException("The inner scene must publish triangle optics.", nameof(inner));
    }

    /// <summary>Ground draws since the last <see cref="ClearFrame"/>, in submission order.</summary>
    internal List<(GltfMesh Mesh, Matrix4x4 World)> DrawnMeshes { get; } = new();

    /// <summary>Water submissions since the last <see cref="ClearFrame"/>, in submission order.</summary>
    internal List<WaterPlane> DrawnWater { get; } = new();

    /// <summary>Every triangle field uploaded through this seam that is still live.</summary>
    internal Dictionary<WaterOpticsHandle, WaterOpticsTriangleField> Fields { get; } = new();

    internal int TriangleUploads { get; private set; }

    internal void ClearFrame()
    {
        DrawnMeshes.Clear();
        DrawnWater.Clear();
    }

    public InteriorCutFrame InteriorCutFrame => _inner.InteriorCutFrame;
    public void SetInteriorCut(InteriorHeightField? field) => _inner.SetInteriorCut(field);
    public ITileWorldPropClusterOwner CreatePropClusterOwner() => _inner.CreatePropClusterOwner();

    public MeshHandle LoadMesh(GltfMesh mesh) => Remember(_inner.LoadMesh(mesh), mesh);
    public MeshHandle LoadMesh(GltfMesh mesh, TileGroundMaterialHandle material) =>
        Remember(_inner.LoadMesh(mesh, material), mesh);

    public void UnloadMesh(MeshHandle handle)
    {
        _meshes.Remove((handle.Index, handle.Generation));
        _inner.UnloadMesh(handle);
    }

    public void DrawMesh(MeshHandle handle, Matrix4x4 world)
    {
        if (_meshes.TryGetValue((handle.Index, handle.Generation), out GltfMesh? mesh)) DrawnMeshes.Add((mesh, world));
        _inner.DrawMesh(handle, world);
    }

    public void DrawMesh(in RigidInstanceDraw draw) => _inner.DrawMesh(in draw);
    public void DrawOverlayMesh(MeshHandle handle, Matrix4x4 world) => _inner.DrawOverlayMesh(handle, world);
    public void DrawMeshDissolved(MeshHandle handle, Matrix4x4 world, float dissolve, float edgeWidth, Color edgeColor) =>
        _inner.DrawMeshDissolved(handle, world, dissolve, edgeWidth, edgeColor);

    public TileGroundMaterialHandle LoadTileGroundMaterial(TileGroundMaterialSet set) => _inner.LoadTileGroundMaterial(set);
    public void UnloadTileGroundMaterial(TileGroundMaterialHandle handle) => _inner.UnloadTileGroundMaterial(handle);

    public IReadOnlyList<MeshHandle> LoadPropMeshes(IReadOnlyList<GltfMeshPart> parts) => _inner.LoadPropMeshes(parts);
    public void UnloadPropMeshes(IReadOnlyList<MeshHandle> handles) => _inner.UnloadPropMeshes(handles);
    public int DrawProps(IReadOnlyList<PropPlacement> placements, IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts,
        Vector3 focus, float drawRadius) => _inner.DrawProps(placements, parts, focus, drawRadius);
    public int DrawShadowOnlyProps(IReadOnlyList<PropPlacement> placements,
        IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts, Vector3 focus, float drawRadius) =>
        _inner.DrawShadowOnlyProps(placements, parts, focus, drawRadius);
    public int DrawGroundCover(IReadOnlyList<GroundCoverInstance> cover,
        IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts, Vector3 focus, GroundCoverRenderOptions options) =>
        _inner.DrawGroundCover(cover, parts, focus, options);
    public void ReleaseGroundCover(IReadOnlyList<GroundCoverInstance> cover) => _inner.ReleaseGroundCover(cover);

    public void DrawWater(in WaterPlane plane)
    {
        DrawnWater.Add(plane);
        _inner.DrawWater(plane);
    }

    public void DrawMeshSilhouette(MeshHandle handle, Matrix4x4 world, Color color, float widthMetres) =>
        _inner.DrawMeshSilhouette(handle, world, color, widthMetres);
    public void DrawMeshSilhouette(MeshHandle handle, Matrix4x4 world, Color color, float widthMetres,
        bool cutInsideInteriors) => _inner.DrawMeshSilhouette(handle, world, color, widthMetres, cutInsideInteriors);
    public MeshOutlineGroup BeginMeshOutline(Color color, float widthPixels) => _inner.BeginMeshOutline(color, widthPixels);
    public MeshOutlineGroup BeginMeshOutline(Color color, float widthPixels, MeshOutlineOcclusion occlusion) =>
        _inner.BeginMeshOutline(color, widthPixels, occlusion);
    public void DrawMeshOutline(MeshOutlineGroup group, MeshHandle handle, Matrix4x4 world) =>
        _inner.DrawMeshOutline(group, handle, world);
    public void DrawMeshOutlineDissolved(MeshOutlineGroup group, MeshHandle handle, Matrix4x4 world,
        float dissolve, bool dissolveComplement) =>
        _inner.DrawMeshOutlineDissolved(group, handle, world, dissolve, dissolveComplement);
    public void DrawMeshOutlineDissolved(MeshOutlineGroup group, MeshHandle handle, Matrix4x4 world,
        float dissolve, bool dissolveComplement, bool cutInsideInteriors) =>
        _inner.DrawMeshOutlineDissolved(group, handle, world, dissolve, dissolveComplement, cutInsideInteriors);

    public WaterOpticsHandle LoadWaterOpticsField(WaterOpticsField field) => _optics.LoadWaterOpticsField(field);

    public WaterOpticsHandle LoadWaterOpticsTriangles(WaterOpticsTriangleField field)
    {
        WaterOpticsHandle handle = _optics.LoadWaterOpticsTriangles(field);
        Fields[handle] = field;
        TriangleUploads++;
        return handle;
    }

    public void UnloadWaterOpticsField(WaterOpticsHandle handle)
    {
        Fields.Remove(handle);
        _optics.UnloadWaterOpticsField(handle);
    }

    public float GetWaterOpticsPadding(WaterLook? look = null) => _optics.GetWaterOpticsPadding(look);

    public bool TryGetWaterOpticsPadding(WaterLook? look, out float padding, out WaterOpticsSurfaceFailure reason) =>
        _optics.TryGetWaterOpticsPadding(look, out padding, out reason);

    MeshHandle Remember(MeshHandle handle, GltfMesh mesh)
    {
        _meshes[(handle.Index, handle.Generation)] = mesh;
        return handle;
    }
}
