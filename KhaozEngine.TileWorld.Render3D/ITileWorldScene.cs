using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using TileGroundMaterialHandle = KhaozEngine.Render3D.Scene3D.TileGroundMaterialHandle;

namespace KhaozEngine.TileWorld;

/// <summary>One TileWorld view's adapter around the shared prop-cluster renderer.</summary>
public interface ITileWorldPropClusterOwner : IDisposable
{
    /// <summary>Build one detached cluster generation without GPU access.</summary>
    PropClusterCpuBuild BuildCpu(PropClusterBuildRequest request);

    /// <summary>Apply one completed generation on the scene thread.</summary>
    void Apply(PropClusterKey key, PropClusterCpuBuild build);

    /// <summary>Force the next accepted build to replace the current generation.</summary>
    void Invalidate(PropClusterKey key);

    /// <summary>Release one region-plane layer.</summary>
    void Unload(PropClusterKey key);

    /// <summary>Draw all retained layers once for this frame.</summary>
    void Draw(Vector3 focus);

    /// <summary>Reports the exact individual and merged choices used by the retained cluster's draw.</summary>
    bool TryGetDrawState(PropClusterKey key, Vector3 focus, out PropClusterDrawState state)
    {
        state = default;
        return false;
    }
}

/// <summary>The slice of a 3D scene a tile world draws through: upload and free a ground mesh, draw one at a
/// world transform, upload and free an archetype's prop parts, and queue a placement list through the prop path.
/// Shaped exactly on what <see cref="Scene3D"/> and the prop renderer already offer, because its job is to let
/// the view's bookkeeping run without a device, not to add an abstraction of its own. The shipped implementation
/// is <see cref="Scene3DTileWorldScene"/>, and the tests drive a recording fake.</summary>
public interface ITileWorldScene
{
    /// <summary>Create one view-owned adapter around the shared prop-cluster renderer.</summary>
    ITileWorldPropClusterOwner CreatePropClusterOwner() =>
        throw new NotSupportedException("This tile-world scene does not support prop clusters.");

    /// <summary>Uploads one region-plane's ground mesh and returns its handle.</summary>
    MeshHandle LoadMesh(GltfMesh mesh);

    /// <summary>Frees a ground-mesh handle. A default handle is a no-op.</summary>
    void UnloadMesh(MeshHandle handle);

    /// <summary>Queues one ground mesh at its world transform for this frame.</summary>
    void DrawMesh(MeshHandle handle, Matrix4x4 world);

    /// <summary>Queues one translucent, unlit, depth-tested mesh in the overlay pass. An implementation that
    /// cannot reach that pass refuses the call rather than silently drawing an opaque mesh.</summary>
    /// <exception cref="NotSupportedException">This scene implementation has no overlay-mesh pass.</exception>
    void DrawOverlayMesh(MeshHandle handle, Matrix4x4 world) =>
        throw new NotSupportedException("This tile-world scene does not support translucent overlay meshes.");

    /// <summary>Queues one rigid mesh with a dissolve threshold, edge width and edge colour. Defaults to the
    /// solid draw so an implementation written before rigid dissolve support keeps compiling and keeps the body
    /// visible.</summary>
    void DrawMeshDissolved(MeshHandle handle, Matrix4x4 world, float dissolve, float edgeWidth, Color edgeColor) =>
        DrawMesh(handle, world);

    /// <summary>Queues one rigid mesh from a full draw descriptor, its motion key included. The
    /// <see cref="MotionKey"/> lets the engine find the draw's previous transform so temporal rendering reprojects it,
    /// so it must stay the same across frames and be unique per draw: each part of a multi-part body takes its own
    /// key, derived with <see cref="MotionKey.Combine"/>. Defaults to the solid or dissolved mesh draw, which keeps an
    /// implementation written before descriptors compiling and keeps the body visible. That fallback keeps the mesh,
    /// the transform and the dissolve with its edge. It drops the key, the tint, the material, <c>CastsShadows</c> and
    /// <c>InvertShadowDissolve</c>, and does not forward <c>DissolveComplement</c>. It draws nothing for a shadow-only
    /// descriptor, which an older scene never cast. It also draws nothing for a complement phase above one half with
    /// no dissolve. <see cref="Scene3D"/> shows no body for that draw, at most a shadow, so the fallback treats it like
    /// a shadow-only draw.</summary>
    /// <exception cref="ArgumentException">The descriptor is shadow-only and casts no shadow, the pair the scene's
    /// instance queue refuses with the same exception.</exception>
    void DrawMesh(in RigidInstanceDraw draw)
    {
        if (draw.ShadowOnly && !draw.CastsShadows)
            throw new ArgumentException(
                "A shadow-only instance must cast shadows: shadowOnly with castsShadows false draws in neither pass.",
                "shadowOnly");
        // The colour pass and the plain shadow passes read a phase above one half as the complement, which keeps
        // nothing at a threshold of zero. An inverted shadow ignores the phase, so such a draw is at most a shadow.
        if (draw.ShadowOnly || (draw.DissolveComplement > 0.5f && draw.Dissolve <= 0f)) return;
        if (draw.Dissolve > 0f)
            DrawMeshDissolved(draw.Mesh, draw.World, draw.Dissolve, draw.DissolveEdgeWidth, draw.DissolveEdgeColor);
        else DrawMesh(draw.Mesh, draw.World);
    }

    /// <summary>Uploads one skinned mesh. An implementation that cannot preserve its skin refuses the call.</summary>
    /// <exception cref="NotSupportedException">This scene implementation has no skinned-mesh pass.</exception>
    SkinnedMeshHandle LoadSkinnedMesh(SkinnedGltfMesh mesh) =>
        throw new NotSupportedException("This tile-world scene does not support skinned meshes.");

    /// <summary>Uploads one skinned mesh with its decoded glTF material maps. An implementation that cannot
    /// preserve its skin refuses the call.</summary>
    /// <exception cref="NotSupportedException">This scene implementation has no skinned-mesh pass.</exception>
    SkinnedMeshHandle LoadSkinnedMesh(SkinnedGltfMesh mesh, GltfMaterialMaps maps) =>
        throw new NotSupportedException("This tile-world scene does not support skinned meshes.");

    /// <summary>Frees a skinned-mesh handle. An implementation without skinned meshes refuses the call.</summary>
    /// <exception cref="NotSupportedException">This scene implementation has no skinned-mesh pass.</exception>
    void UnloadSkinnedMesh(SkinnedMeshHandle handle) =>
        throw new NotSupportedException("This tile-world scene does not support skinned meshes.");

    /// <summary>Queues one skinned mesh with this frame's bone palette, world transform and tint. An implementation
    /// that cannot preserve its skin refuses the call.</summary>
    /// <exception cref="NotSupportedException">This scene implementation has no skinned-mesh pass.</exception>
    void DrawSkinned(SkinnedMeshHandle handle, ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 world, Color tint) =>
        throw new NotSupportedException("This tile-world scene does not support skinned meshes.");

    /// <summary>Queues one skinned mesh with the scene's dissolve treatment. An implementation that cannot
    /// preserve its skin refuses the call.</summary>
    /// <exception cref="NotSupportedException">This scene implementation has no skinned-mesh pass.</exception>
    void DrawSkinnedDissolved(SkinnedMeshHandle handle, ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 world,
        Color tint, float dissolve, float edgeWidth, Color edgeColor) =>
        throw new NotSupportedException("This tile-world scene does not support skinned meshes.");

    /// <summary>Queues one skinned mesh from a full draw descriptor, its motion key included. The
    /// <see cref="MotionKey"/> lets the engine find the draw's previous transform and bone palette so temporal
    /// rendering reprojects it, so it must stay the same across frames and be unique per draw: each part of a
    /// multi-part body takes its own key, derived with <see cref="MotionKey.Combine"/>. Defaults to the plain skinned
    /// draw, or to the dissolved one when the descriptor dissolves. That fallback keeps the mesh, the palette,
    /// the model transform, the tint and the dissolve with its edge, and drops the key, the material and
    /// <c>CastsShadows</c>. Each route refuses where the older draw it reaches refuses: a scene with no skinned pass
    /// throws on every descriptor, and a scene that implements only the plain <c>DrawSkinned</c> throws from the
    /// dissolved default when the descriptor dissolves.</summary>
    /// <exception cref="NotSupportedException">This scene implementation does not implement the older skinned draw the
    /// descriptor routes to.</exception>
    void DrawSkinned(in SkinnedInstanceDraw draw, ReadOnlySpan<Matrix4x4> boneMatrices)
    {
        if (draw.Dissolve > 0f)
            DrawSkinnedDissolved(draw.Mesh, boneMatrices, draw.Model, draw.Tint, draw.Dissolve, draw.DissolveEdgeWidth,
                draw.DissolveEdgeColor);
        else DrawSkinned(draw.Mesh, boneMatrices, draw.Model, draw.Tint);
    }

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

    /// <summary>Queues every placement within <paramref name="drawRadius"/> of <paramref name="focus"/> whose
    /// archetype id has parts in <paramref name="parts"/> into the SHADOW depth pass ALONE (issue #974): they
    /// record depth for the key light and draw nothing in the colour pass. Returns how many were queued.
    /// <para>This is how a view keeps the shadow of geometry it hides from the eye, the hidden roof being the
    /// first consumer. <paramref name="placements"/> is READ during the call and never retained, the same
    /// contract <see cref="DrawProps"/> carries, so a caller may hand over a scratch list it refills.</para>
    /// <para>Defaults to no draws, so a scene seam written before shadow-only casters existed keeps compiling
    /// and simply casts nothing, which is the behaviour it already had.</para></summary>
    int DrawShadowOnlyProps(IReadOnlyList<PropPlacement> placements,
                            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts,
                            Vector3 focus, float drawRadius) => 0;

    /// <summary>Queues cached ground cover. Defaults to no draws for an older
    /// headless scene seam implementation.</summary>
    int DrawGroundCover(IReadOnlyList<GroundCoverInstance> cover,
                        IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts,
                        Vector3 focus, GroundCoverRenderOptions options) => 0;

    /// <summary>Releases retained draw resources for one ground-cover batch. Defaults to a no-op for
    /// scenes that use immediate draws or implement the older scene seam.</summary>
    void ReleaseGroundCover(IReadOnlyList<GroundCoverInstance> cover) { }

    /// <summary>Queues one water surface for this frame, defaulting to a no-op so an implementation written
    /// before water existed keeps compiling and simply draws none.</summary>
    void DrawWater(in WaterPlane plane) { }

    /// <summary>Queues one per-entity SILHOUETTE (the inverted-hull highlight for a clicked monster or a
    /// selected prop) of an already-loaded mesh at a world transform, in a flat colour, with the hull pushed
    /// out by <paramref name="widthMetres"/>. Defaults to a no-op so an implementation written before
    /// silhouettes existed keeps compiling and simply draws none.</summary>
    void DrawMeshSilhouette(MeshHandle handle, Matrix4x4 world, Color color, float widthMetres) { }

    /// <summary>Starts one frame-local pixel-width outline group. Defaults to an inert handle for an older scene.</summary>
    MeshOutlineGroup BeginMeshOutline(Color color, float widthPixels) => default;

    /// <summary>Starts one frame-local pixel-width outline group whose border <paramref name="occlusion"/> may hide.
    /// Defaults to the scene-depth group for a scene written before the choice existed.</summary>
    MeshOutlineGroup BeginMeshOutline(Color color, float widthPixels, MeshOutlineOcclusion occlusion) =>
        BeginMeshOutline(color, widthPixels);

    /// <summary>Adds one mesh part to a pixel-width outline group. Defaults to a no-op for an older scene.</summary>
    void DrawMeshOutline(MeshOutlineGroup group, MeshHandle handle, Matrix4x4 world) { }

    /// <summary>Adds a part whose visible mask follows the rigid dissolve used by the ordinary model draw.</summary>
    void DrawMeshOutlineDissolved(MeshOutlineGroup group, MeshHandle handle, Matrix4x4 world,
        float dissolve, bool dissolveComplement) => DrawMeshOutline(group, handle, world);

    /// <summary>Adds one posed skinned part to a pixel-width outline group. Defaults to a no-op for an older scene.</summary>
    void DrawSkinnedOutline(
        MeshOutlineGroup group,
        SkinnedMeshHandle mesh,
        ReadOnlySpan<Matrix4x4> boneMatrices,
        Matrix4x4 world) { }

    /// <summary>Adds a posed skinned part whose visible mask follows the ordinary skinned dissolve.</summary>
    void DrawSkinnedOutlineDissolved(
        MeshOutlineGroup group,
        SkinnedMeshHandle mesh,
        ReadOnlySpan<Matrix4x4> boneMatrices,
        Matrix4x4 world,
        float dissolve,
        bool dissolveComplement) =>
        DrawSkinnedOutline(group, mesh, boneMatrices, world);
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
    public ITileWorldPropClusterOwner CreatePropClusterOwner() => new ScenePropClusterOwner(_scene);

    /// <inheritdoc />
    public MeshHandle LoadMesh(GltfMesh mesh) => _scene.LoadMesh(mesh);

    /// <inheritdoc />
    public void UnloadMesh(MeshHandle handle) => _scene.UnloadMesh(handle);

    /// <inheritdoc />
    public void DrawMesh(MeshHandle handle, Matrix4x4 world) => _scene.Draw(handle, world);

    /// <inheritdoc />
    public void DrawOverlayMesh(MeshHandle handle, Matrix4x4 world) => _scene.DrawOverlayMesh(handle, world);

    /// <inheritdoc />
    public void DrawMeshDissolved(MeshHandle handle, Matrix4x4 world, float dissolve, float edgeWidth, Color edgeColor) =>
        _scene.Draw(handle, world, Color.White, Material.None, dissolve, edgeWidth, edgeColor);

    /// <inheritdoc />
    public void DrawMesh(in RigidInstanceDraw draw) => _scene.Draw(in draw);

    /// <inheritdoc />
    public SkinnedMeshHandle LoadSkinnedMesh(SkinnedGltfMesh mesh) => _scene.LoadSkinnedMesh(mesh);

    /// <inheritdoc />
    public SkinnedMeshHandle LoadSkinnedMesh(SkinnedGltfMesh mesh, GltfMaterialMaps maps) =>
        _scene.LoadSkinnedMesh(mesh, maps);

    /// <inheritdoc />
    public void UnloadSkinnedMesh(SkinnedMeshHandle handle) => _scene.UnloadSkinnedMesh(handle);

    /// <inheritdoc />
    public void DrawSkinned(SkinnedMeshHandle handle, ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 world,
        Color tint) => _scene.DrawSkinned(handle, boneMatrices, world, tint);

    /// <inheritdoc />
    public void DrawSkinnedDissolved(SkinnedMeshHandle handle, ReadOnlySpan<Matrix4x4> boneMatrices,
        Matrix4x4 world, Color tint, float dissolve, float edgeWidth, Color edgeColor) =>
        _scene.DrawSkinned(handle, boneMatrices, world, tint, Material.None, dissolve, edgeWidth, edgeColor);

    /// <inheritdoc />
    public void DrawSkinned(in SkinnedInstanceDraw draw, ReadOnlySpan<Matrix4x4> boneMatrices) =>
        _scene.DrawSkinned(in draw, boneMatrices);

    /// <inheritdoc />
    public void DrawMeshSilhouette(MeshHandle handle, Matrix4x4 world, Color color, float widthMetres) =>
        _scene.DrawMeshSilhouette(handle, world, color, widthMetres);

    /// <inheritdoc />
    public MeshOutlineGroup BeginMeshOutline(Color color, float widthPixels) =>
        _scene.BeginMeshOutline(color, widthPixels);

    /// <inheritdoc />
    public MeshOutlineGroup BeginMeshOutline(Color color, float widthPixels, MeshOutlineOcclusion occlusion) =>
        _scene.BeginMeshOutline(color, widthPixels, occlusion);

    /// <inheritdoc />
    public void DrawMeshOutline(MeshOutlineGroup group, MeshHandle handle, Matrix4x4 world) =>
        _scene.DrawMeshOutline(group, handle, world);

    /// <inheritdoc />
    public void DrawMeshOutlineDissolved(MeshOutlineGroup group, MeshHandle handle, Matrix4x4 world,
        float dissolve, bool dissolveComplement) =>
        _scene.DrawMeshOutlineDissolved(group, handle, world, dissolve, dissolveComplement);

    /// <inheritdoc />
    public void DrawSkinnedOutline(
        MeshOutlineGroup group, SkinnedMeshHandle mesh,
        ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 world) =>
        _scene.DrawSkinnedOutline(group, mesh, boneMatrices, world);

    /// <inheritdoc />
    public void DrawSkinnedOutlineDissolved(
        MeshOutlineGroup group, SkinnedMeshHandle mesh,
        ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 world,
        float dissolve, bool dissolveComplement) =>
        _scene.DrawSkinnedOutlineDissolved(
            group, mesh, boneMatrices, world, dissolve, dissolveComplement);

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
    public int DrawShadowOnlyProps(IReadOnlyList<PropPlacement> placements,
                                   IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts,
                                   Vector3 focus, float drawRadius) =>
        _scene.DrawShadowOnlyProps(placements, parts, focus, drawRadius);

    /// <inheritdoc />
    public int DrawGroundCover(IReadOnlyList<GroundCoverInstance> cover,
                               IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts,
                               Vector3 focus, GroundCoverRenderOptions options) =>
        _scene.DrawGroundCover(cover, parts, focus, options);

    /// <inheritdoc />
    public void ReleaseGroundCover(IReadOnlyList<GroundCoverInstance> cover) => _scene.ReleaseGroundCover(cover);

    /// <inheritdoc />
    public void DrawWater(in WaterPlane plane) => _scene.DrawWater(plane);

    sealed class ScenePropClusterOwner : ITileWorldPropClusterOwner
    {
        readonly PropClusterRenderer _renderer;

        public ScenePropClusterOwner(Scene3D scene) => _renderer = new PropClusterRenderer(scene);
        public PropClusterCpuBuild BuildCpu(PropClusterBuildRequest request) => _renderer.BuildCpu(request);
        public void Apply(PropClusterKey key, PropClusterCpuBuild build) => _renderer.Apply(key, build);
        public void Invalidate(PropClusterKey key) => _renderer.Invalidate(key);
        public void Unload(PropClusterKey key) => _renderer.Unload(key);
        public void Draw(Vector3 focus) => _renderer.Draw(focus);
        public bool TryGetDrawState(PropClusterKey key, Vector3 focus, out PropClusterDrawState state) =>
            _renderer.TryGetDrawState(key, focus, out state);
        public void Dispose() => _renderer.Dispose();
    }
}
