using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using TileGroundMaterialHandle = KhaozEngine.Render3D.Scene3D.TileGroundMaterialHandle;

namespace KhaozEngine.TileWorld;

/// <summary>The slice of a 3D scene a tile world draws through: upload and free a ground mesh, draw one at a
/// world transform, upload and free an archetype's prop parts, and queue a placement list through the prop path.
/// Shaped exactly on what <see cref="Scene3D"/> and the prop renderer already offer, because its job is to let
/// the view's bookkeeping run without a device, not to add an abstraction of its own. The shipped implementation
/// is <see cref="Scene3DTileWorldScene"/>, and the tests drive a recording fake.</summary>
public interface ITileWorldScene
{
    /// <summary>Uploads one region-plane's ground mesh and returns its handle.</summary>
    MeshHandle LoadMesh(GltfMesh mesh);

    /// <summary>Frees a ground-mesh handle. A default handle is a no-op.</summary>
    void UnloadMesh(MeshHandle handle);

    /// <summary>Queues one ground mesh at its world transform for this frame.</summary>
    void DrawMesh(MeshHandle handle, Matrix4x4 world);

    /// <summary>Queues one rigid mesh with a dissolve threshold, edge width and edge colour. Defaults to the
    /// solid draw so an implementation written before rigid dissolve support keeps compiling and keeps the body
    /// visible.</summary>
    void DrawMeshDissolved(MeshHandle handle, Matrix4x4 world, float dissolve, float edgeWidth, Color edgeColor) =>
        DrawMesh(handle, world);

    /// <summary>Uploads the ground material set every region-plane mesh of this world is drawn with, once per
    /// view, and returns its handle. Defaults to an invalid handle so an implementation written before textured
    /// ground existed keeps compiling and keeps drawing through the untextured upload below.</summary>
    TileGroundMaterialHandle LoadTileGroundMaterial(TileGroundMaterialSet set) => TileGroundMaterialHandle.Invalid;

    /// <summary>Frees a ground material set. Defaults to a no-op, which is right for an implementation whose
    /// <see cref="LoadTileGroundMaterial"/> never uploaded one.</summary>
    void UnloadTileGroundMaterial(TileGroundMaterialHandle handle) { }

    /// <summary>Uploads one region-plane's ground mesh bound to a ground material, so it draws through the
    /// tile-ground pipeline. Defaults to the material-free upload, which renders the same geometry through the
    /// model path.</summary>
    MeshHandle LoadMesh(GltfMesh mesh, TileGroundMaterialHandle material) => LoadMesh(mesh);

    /// <summary>Uploads one archetype's mesh parts and returns the per-part handles, one textured sub-mesh per
    /// source material.</summary>
    IReadOnlyList<MeshHandle> LoadPropMeshes(IReadOnlyList<GltfMeshPart> parts);

    /// <summary>Frees every part handle of one archetype.</summary>
    void UnloadPropMeshes(IReadOnlyList<MeshHandle> handles);

    /// <summary>Queues every placement within <paramref name="drawRadius"/> of <paramref name="focus"/> whose
    /// archetype id has parts in <paramref name="parts"/>, and returns how many were drawn.
    /// <para><paramref name="placements"/> is READ during the call and never retained, so a caller may hand over
    /// a scratch list it refills for the next call of the same frame. The view relies on that for the roofs it
    /// filters per region-plane.</para></summary>
    int DrawProps(IReadOnlyList<PropPlacement> placements,
                  IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts,
                  Vector3 focus, float drawRadius);

    /// <summary>Queues one water surface for this frame, defaulting to a no-op so an implementation written
    /// before water existed keeps compiling and simply draws none.</summary>
    void DrawWater(in WaterPlane plane) { }

    /// <summary>Queues one per-entity SILHOUETTE (the inverted-hull highlight for a clicked monster or a
    /// selected prop) of an already-loaded mesh at a world transform, in a flat colour, with the hull pushed
    /// out by <paramref name="widthMetres"/>. Defaults to a no-op so an implementation written before
    /// silhouettes existed keeps compiling and simply draws none.</summary>
    void DrawMeshSilhouette(MeshHandle handle, Matrix4x4 world, Color color, float widthMetres) { }
}

/// <summary>The shipped <see cref="ITileWorldScene"/>: every member forwards straight to a <see cref="Scene3D"/>
/// and its prop-renderer extension, so the seam costs one virtual call and adds no behaviour of its own.</summary>
public sealed class Scene3DTileWorldScene : ITileWorldScene
{
    readonly Scene3D _scene;

    /// <summary>Wraps a scene. The scene stays owned by its creator, so disposing a view never disposes it.</summary>
    public Scene3DTileWorldScene(Scene3D scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        _scene = scene;
    }

    /// <summary>The wrapped scene, for a caller that needs the full surface alongside the seam.</summary>
    public Scene3D Scene => _scene;

    /// <inheritdoc />
    public MeshHandle LoadMesh(GltfMesh mesh) => _scene.LoadMesh(mesh);

    /// <inheritdoc />
    public void UnloadMesh(MeshHandle handle) => _scene.UnloadMesh(handle);

    /// <inheritdoc />
    public void DrawMesh(MeshHandle handle, Matrix4x4 world) => _scene.Draw(handle, world);

    /// <inheritdoc />
    public void DrawMeshDissolved(MeshHandle handle, Matrix4x4 world, float dissolve, float edgeWidth, Color edgeColor) =>
        _scene.Draw(handle, world, Color.White, Material.None, dissolve, edgeWidth, edgeColor);

    /// <inheritdoc />
    public void DrawMeshSilhouette(MeshHandle handle, Matrix4x4 world, Color color, float widthMetres) =>
        _scene.DrawMeshSilhouette(handle, world, color, widthMetres);

    /// <inheritdoc />
    public TileGroundMaterialHandle LoadTileGroundMaterial(TileGroundMaterialSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return _scene.LoadTileGroundMaterial(set.Width, set.Height, set.Layers);
    }

    /// <inheritdoc />
    public void UnloadTileGroundMaterial(TileGroundMaterialHandle handle) => _scene.UnloadTileGroundMaterial(handle);

    /// <inheritdoc />
    public MeshHandle LoadMesh(GltfMesh mesh, TileGroundMaterialHandle material) => _scene.LoadMesh(mesh, material);

    /// <inheritdoc />
    public IReadOnlyList<MeshHandle> LoadPropMeshes(IReadOnlyList<GltfMeshPart> parts) => _scene.LoadPropMeshes(parts);

    /// <inheritdoc />
    public void UnloadPropMeshes(IReadOnlyList<MeshHandle> handles)
    {
        ArgumentNullException.ThrowIfNull(handles);
        foreach (MeshHandle handle in handles) _scene.UnloadMesh(handle);
    }

    /// <inheritdoc />
    public int DrawProps(IReadOnlyList<PropPlacement> placements,
                         IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts,
                         Vector3 focus, float drawRadius) =>
        _scene.DrawProps(placements, parts, focus, drawRadius);

    /// <inheritdoc />
    public void DrawWater(in WaterPlane plane) => _scene.DrawWater(plane);
}
