using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using TileGroundMaterialHandle = KhaozEngine.Render3D.Scene3D.TileGroundMaterialHandle;

namespace KhaozEngine.TileWorld;

/// <summary>What <see cref="TileWorldView"/> does with roofs, the player-facing roofs setting an OSRS-style
/// game exposes.</summary>
public enum RoofVisibility
{
    /// <summary>The default. A roof is hidden while the observer stands INSIDE the building it covers: the
    /// roof's footprint has to touch the observer's own interior, and sit on a plane above the observer's. Every
    /// other roof in view keeps drawing, so walking into a house does not strip the skyline.</summary>
    Interior,
    /// <summary>Nothing is ever hidden. Roofs draw indoors as well, which is the debug and map-authoring view.</summary>
    AlwaysVisible,
    /// <summary>Every roof on every plane is hidden, indoors or not. This is the "roofs off" setting, and it is
    /// also the pre-18.10.0 indoor behaviour applied unconditionally.</summary>
    AlwaysHidden,
}

/// <summary>Knobs for <see cref="TileWorldView"/>.</summary>
public sealed class TileWorldViewOptions
{
    /// <summary>Horizontal radius in metres around the draw focus inside which a prop is drawn.</summary>
    public float PropDrawRadius { get; set; } = 96f;

    /// <summary>The settings every region-plane ground mesh is built with. The view SETS
    /// <see cref="TileGroundMesherOptions.Slots"/> on this object when it is constructed, to the material set it
    /// is about to upload, because the slots a vertex names only mean anything against that set.</summary>
    public TileGroundMesherOptions Mesher { get; set; } = new();

    /// <summary>The ground materials every region-plane mesh is drawn with. Null builds one from the catalogs
    /// with no texture loader, which gives every material a flat layer of its catalog colour: a colour-only world
    /// renders through the textured pipeline, without texture detail. Pass a set to texture the ground, or to
    /// share one across several views of the same catalog.</summary>
    public TileGroundMaterialSet? GroundMaterials { get; set; }

    /// <summary>The look every water plane this view queues carries. Defaults to
    /// <see cref="TileWaterLooks.River"/>. Null draws the planes with the scene's own water settings instead,
    /// which is what a world whose scene look is already a river wants. Read on every frame, so a change applies
    /// to the next <see cref="TileWorldView.Draw"/>. On the options rather than the view so a caller that never
    /// sees the view (<see cref="TileWorldSnapshot"/>, the ke-tileedit renders) can still set it.</summary>
    public WaterLook? WaterLook { get; set; } = TileWaterLooks.River;

    /// <summary>Whether <see cref="TileWorldView.Draw"/> queues the water planes after the ground and props (the
    /// default). A caller that submits its own water pass for the world sets it false and may still call
    /// <see cref="TileWorldView.DrawWaterPlanes"/> itself.</summary>
    public bool DrawWater { get; set; } = true;

    /// <summary>How many queued region-planes one <see cref="TileWorldView.Flush()"/> may remesh, oldest first.
    /// The rest stay queued for the next flush, so a burst spreads over frames instead of landing on one.
    /// <para>The burst is real rather than theoretical: streaming one region in marks its eight neighbours dirty
    /// on every plane, because the mesher reads heights, normals and corner colours ACROSS region borders, so a
    /// neighbour meshed while this region was absent is stale the moment it arrives. Two loads an update against
    /// a four-plane world is 64 marks, of which the loaded ones are real remeshes. 16 keeps that inside a frame
    /// at a cost of a few frames of latency on a border that is already only a corner blend out of date.</para>
    /// <para><see cref="int.MaxValue"/> restores the drain-everything behaviour, for a loading screen or a test.
    /// A value below 1 is treated as 1, because a budget of 0 would silently freeze every queued rebuild and
    /// leave stale meshes on screen forever.</para></summary>
    public int MaxRebuildsPerFlush { get; set; } = DefaultMaxRebuildsPerFlush;

    /// <summary>The default <see cref="MaxRebuildsPerFlush"/>.</summary>
    public const int DefaultMaxRebuildsPerFlush = 16;

    /// <summary>Where the view reports an archetype with no mesh, once per archetype. Null discards the line.</summary>
    public Action<string>? Log { get; set; }
}

/// <summary>Owns one tile world's meshes and props inside a scene: one ground mesh handle per drawable
/// region-plane, one prop mesh set per catalog archetype, per-region prop placements, and the roof rule that
/// hides the roofs over the building an indoor observer is standing in. Edits are announced with <see cref="MarkDirty(RegionCoord, int)"/>
/// and coalesced into one rebuild per region-plane at the start of the next <see cref="Draw"/>, so a stroke that
/// touches the same tiles a hundred times still remeshes each region-plane once. Everything goes through
/// <see cref="ITileWorldScene"/>, so the whole class runs headless.</summary>
public sealed partial class TileWorldView : IDisposable
{
    /// <summary>Side in metres of the box an archetype with no mesh is drawn as.</summary>
    public const float PlaceholderSize = 1f;

    /// <summary>How many tiles a dirty world rect is grown by before it is turned into region marks.</summary>
    public const int DirtyRegionMargin = 2;

    // A flat mid grey: visibly not content, and visible against both the greybox palette and lit ground.
    static readonly Vector4 PlaceholderColor = new(0.5f, 0.5f, 0.5f, 1f);

    // One shared unit box, uploaded once per view per unresolved archetype. Built here rather than asked of the
    // greybox resolver, because the placeholder must not depend on the archetype's footprint: a missing 2x2 mesh
    // should read as "nothing resolved this", not as a plausible 2x2 object.
    static readonly IReadOnlyList<GltfMeshPart> PlaceholderParts = Array.AsReadOnly(new[]
    {
        new GltfMeshPart(
            GreyboxMeshResolver.Box(
                new Vector3(-PlaceholderSize * 0.5f, 0f, -PlaceholderSize * 0.5f),
                new Vector3(PlaceholderSize * 0.5f, PlaceholderSize, PlaceholderSize * 0.5f),
                PlaceholderColor),
            default),
    });

    readonly ITileWorldScene _scene;
    readonly TileWorldDocument _doc;
    readonly TileWorldCatalogs _catalogs;
    readonly TileWorldViewOptions _options;
    readonly Dictionary<string, IReadOnlyList<MeshHandle>> _propMeshes = new(StringComparer.Ordinal);
    readonly Dictionary<RegionCoord, RegionHandles> _loaded = new();
    // The rebuild queue is a pair on purpose: the set is the dedup (a stroke marks the same region-plane a
    // hundred times) and the list is the ORDER, which matters once a flush is budgeted, because the oldest mark
    // is the one whose mesh has been wrong the longest. The two are always mutated together.
    readonly HashSet<(RegionCoord Region, int Plane)> _dirty = new();
    readonly List<(RegionCoord Region, int Plane)> _dirtyOrder = new();
    readonly int _planes;
    readonly TileGroundMaterialSet _materials;
    readonly TileGroundMaterialHandle _material;
    // The observer's own interior, and the roofs of one region-plane that survive it. The scratch list is
    // refilled per region-plane rather than allocated, and is only ever handed to ITileWorldScene.DrawProps,
    // which reads it during the call and does not retain it.
    readonly TileInteriorFill _interior = new();
    readonly List<PropPlacement> _visibleRoofs = new();
    TileCoord _observer;
    bool _interiorStale = true;
    bool _interiorTruncationLogged;
    bool _disposed;

    /// <summary>Binds a world to a scene and uploads the ground material set plus one mesh set per catalog
    /// archetype up front, so a region load is placements alone. An archetype the resolver has no mesh for gets
    /// the placeholder box and one log line, because a half-authored catalog has to keep rendering.
    /// <para>The ground material set is uploaded ONCE here and shared by every region-plane mesh, and the mesher
    /// is pointed at it, so the slots the vertices name are slots of the set the mesh is drawn with. Neither is a
    /// mid-frame call: the upload builds a mip chain on a command list of its own (#424).</para></summary>
    public TileWorldView(ITileWorldScene scene, TileWorldDocument doc, TileWorldCatalogs catalogs,
                         ITileMeshResolver resolver, TileWorldViewOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(resolver);

        _scene = scene;
        _doc = doc;
        _catalogs = catalogs;
        _options = options ?? new TileWorldViewOptions();
        _planes = Math.Max(0, doc.PlaneCount);

        // A throw part way through frees the sets already uploaded, because a constructor that throws never
        // produces the object whose Dispose would have freed them. The resolver is caller code and the upload is a
        // device call, so either can fail on the ninth archetype of twelve, and without this the first eight are
        // stranded on the device with nothing left holding their handles.
        try
        {
            _materials = _options.GroundMaterials ?? TileGroundMaterials.Build(catalogs);
            // Written into the caller's mesher settings rather than a private copy, so a caller who reads them
            // back sees the slot map the meshes are actually built against, and so a field added to those
            // settings later cannot be silently dropped by a copy nobody remembered to widen.
            _options.Mesher.Slots = _materials;
            _material = scene.LoadTileGroundMaterial(_materials);

            foreach (KeyValuePair<string, TileObjectArchetype> entry in catalogs.Archetypes)
            {
                IReadOnlyList<GltfMeshPart>? parts = resolver.Resolve(entry.Value);
                if (parts is null || parts.Count == 0)
                {
                    _options.Log?.Invoke($"tile world: archetype '{entry.Key}' has no mesh, drawing a placeholder box.");
                    parts = PlaceholderParts;
                }
                _propMeshes[entry.Key] = scene.LoadPropMeshes(parts);
            }
        }
        catch
        {
            foreach (IReadOnlyList<MeshHandle> uploaded in _propMeshes.Values) _scene.UnloadPropMeshes(uploaded);
            _propMeshes.Clear();
            // The material is uploaded before the archetypes, so it is the one thing already on the device when
            // the ninth archetype of twelve throws.
            if (_material.IsValid) _scene.UnloadTileGroundMaterial(_material);
            throw;
        }

        Observer = default;
    }

    /// <summary>The tile the roof rule is judged from. Setting it recomputes <see cref="ObserverIndoors"/> at
    /// once, and marks the interior for a refill on the next <see cref="Flush()"/> or roof query when the tile
    /// actually moved, so standing still costs one settings lookup a frame and no flood fill.</summary>
    public TileCoord Observer
    {
        get => _observer;
        set
        {
            if (value != _observer) _interiorStale = true;
            _observer = value;
            ObserverIndoors = IndoorsAt(value);
        }
    }

    /// <summary>Whether the observer's tile is flagged indoors. Unchanged in meaning: it is the trigger for the
    /// roof rule, and <see cref="RoofMode"/> plus the observer's own interior decide which roofs it reaches.</summary>
    public bool ObserverIndoors { get; private set; }

    /// <summary>What this view does with roofs. <see cref="RoofVisibility.Interior"/> by default, which hides
    /// only the roofs over the building the observer is standing in. A game wires this straight to its roofs
    /// setting.</summary>
    public RoofVisibility RoofMode { get; set; } = RoofVisibility.Interior;

    /// <summary>Most tiles one interior may hold before the fill stops walking. Past it the roofs simply stay
    /// visible: see <see cref="InteriorTruncated"/>.</summary>
    public const int MaxInteriorTiles = TileInteriorFill.MaxTiles;

    /// <summary>How many tiles the observer's interior holds, 0 outdoors. Refills the interior if an edit or a
    /// move left it stale, so it never answers from a fill that is out of date.</summary>
    public int InteriorTileCount
    {
        get { EnsureInterior(); return _interior.Count; }
    }

    /// <summary>Whether the observer's interior hit <see cref="MaxInteriorTiles"/> with tiles left to walk,
    /// which means indoor tiles past the cap are not part of it and the roofs over them stay drawn. Content is
    /// saying "indoors" over ground that is not one room.</summary>
    public bool InteriorTruncated
    {
        get { EnsureInterior(); return _interior.Truncated; }
    }

    /// <summary>How many prop placements the last <see cref="Draw"/> queued, roofs included when shown.</summary>
    public int LastDrawnProps { get; private set; }

    /// <summary>How many regions are loaded right now.</summary>
    public int LoadedRegionCount => _loaded.Count;

    /// <summary>How many region-planes are queued for a rebuild, including marks on regions that are not loaded
    /// and will simply be dropped. Non-zero after a flush means the budget deferred work to the next one.</summary>
    public int PendingRebuilds => _dirtyOrder.Count;

    /// <summary>A snapshot of the loaded regions, in no particular order. A copy rather than a live view, so a
    /// caller may load or unload regions while walking it, which is exactly what a residency ring does.</summary>
    public IReadOnlyCollection<RegionCoord> LoadedRegions
    {
        get
        {
            var snapshot = new RegionCoord[_loaded.Count];
            _loaded.Keys.CopyTo(snapshot, 0);
            return snapshot;
        }
    }

    /// <summary>Builds and uploads every plane of one region. Loading a region that is already loaded rebuilds it
    /// from the document rather than doubling up, so a caller may use this as a whole-region refresh.
    /// <para>A throw part way through the region leaves nothing loaded and nothing uploaded: the planes already
    /// on the device are freed before the exception propagates, so a caller that retries the load or gives up on
    /// the region either way does not strand handles.</para></summary>
    public void LoadRegion(RegionCoord region)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UnloadRegion(region);

        var meshes = new MeshHandle?[_planes];
        var props = new TileRegionProps[_planes];
        try
        {
            for (int plane = 0; plane < _planes; plane++)
            {
                meshes[plane] = BuildMesh(region, plane);
                props[plane] = TileObjectProps.Build(_doc, _catalogs, region, plane, OverrideLookup());
            }
        }
        catch
        {
            // The array only reaches _loaded once every plane is built, so a mesher or an upload that throws on
            // plane 3 of 4 would otherwise orphan planes 0 to 2: nothing references them and UnloadRegion has
            // nothing to find. Free what THIS call uploaded, then let the exception out unchanged.
            FreeMeshes(meshes);
            throw;
        }
        _loaded[region] = new RegionHandles(meshes, props);
    }

    /// <summary>Frees every mesh handle of one region and forgets it, along with any rebuild it had queued.
    /// Unloading a region that is not loaded does nothing.</summary>
    public void UnloadRegion(RegionCoord region)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (int plane = 0; plane < _planes; plane++)
            if (_dirty.Remove((region, plane))) _dirtyOrder.Remove((region, plane));
        if (!_loaded.Remove(region, out RegionHandles? handles)) return;
        FreeMeshes(handles);
    }

    /// <summary>Queues one region-plane for a rebuild at the next <see cref="Flush()"/>. A plane outside the
    /// document is ignored, and a region that is not loaded is dropped when the flush runs.</summary>
    public void MarkDirty(RegionCoord region, int plane)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (plane < 0 || plane >= _planes) return;
        Queue(region, plane);
    }

    /// <summary>Queues every region a world-space tile rect can affect on one plane: every region the rect
    /// touches after growing it by <see cref="DirtyRegionMargin"/> tiles. That margin is correctness, not
    /// padding. A corner height is shared by the four tiles around it and so by up to four regions, the smooth
    /// normal at a corner is a central difference that reads one corner further still, and a corner colour
    /// averages the tiles meeting there, so an edit one tile inside a region border genuinely changes the
    /// NEIGHBOUR's mesh. The margin is applied unconditionally, because the rect does not say which of those
    /// three inputs the edit touched, and marking wide is free: the flush drops every region that is not
    /// loaded. This is the edit-facing overload, where an editor hands over the tiles it wrote and the view
    /// works out which regions that is.</summary>
    public void MarkDirty(TileRect worldRect, int plane)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (worldRect.IsEmpty || plane < 0 || plane >= _planes) return;
        TileRect reach = worldRect.Expand(DirtyRegionMargin);
        RegionCoord min = RegionCoord.Of(reach.X, reach.Z);
        RegionCoord max = RegionCoord.Of(reach.X1 - 1, reach.Z1 - 1);
        for (int rz = min.Rz; rz <= max.Rz; rz++)
            for (int rx = min.Rx; rx <= max.Rx; rx++)
                Queue(new RegionCoord(rx, rz), plane);
    }

    /// <summary>Rebuilds queued region-planes oldest first, up to
    /// <see cref="TileWorldViewOptions.MaxRebuildsPerFlush"/> of them, and refreshes the roof rule's indoor flag.
    /// Called at the start of <see cref="Draw"/>, so an explicit call is only needed to pay the cost off the draw
    /// path. Whatever the budget did not reach stays queued and is counted by
    /// <see cref="PendingRebuilds"/>.</summary>
    public void Flush() => Flush(_options.MaxRebuildsPerFlush);

    /// <summary>The same drain against an explicit budget, which overrides
    /// <see cref="TileWorldViewOptions.MaxRebuildsPerFlush"/> for this one call. <see cref="int.MaxValue"/> is the
    /// settle-now form a loading moment wants, and is what a residency prime finishes with, so a teleport does not
    /// spend frames drawing borders meshed against a neighbour that had not arrived yet.
    /// <para>A rebuild that produces NO mesh (the region-plane has no drawable tile) does not spend budget. It
    /// uploads nothing and frees nothing, and a four-plane document where one plane is authored would otherwise
    /// burn three quarters of every flush on region-planes that mesh to null.</para>
    /// <para>A rebuild that THROWS is dropped along with everything already completed ahead of it: the exception
    /// propagates, the queue keeps only what this call had not reached, and that region-plane keeps its previous
    /// mesh rather than being retried on every frame from here on.</para></summary>
    /// <param name="maxRebuilds">Mesh-producing rebuilds this call may perform, treated as 1 when below 1.</param>
    public void Flush(int maxRebuilds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // One settings lookup a frame, which is what it costs to stay right when the tile UNDER a stationary
        // observer is edited: the setter alone only sees the observer moving. A flip refills the interior, and
        // so does any MarkDirty, which is the only edit channel there is (the document raises no events).
        bool indoors = IndoorsAt(_observer);
        if (indoors != ObserverIndoors) _interiorStale = true;
        ObserverIndoors = indoors;
        EnsureInterior();
        if (_dirtyOrder.Count == 0) return;

        int budget = Math.Max(1, maxRebuilds);
        int taken = 0;
        int scanned = 0;
        try
        {
            while (scanned < _dirtyOrder.Count && taken < budget)
            {
                (RegionCoord region, int plane) = _dirtyOrder[scanned];
                scanned++;
                _dirty.Remove((region, plane));
                // A mark on a region that is not loaded costs nothing to drop, so it must not spend the budget:
                // the residency marks eight neighbours per streamed region and most of them are never loaded.
                if (!_loaded.TryGetValue(region, out RegionHandles? handles)) continue;
                // Build BEFORE freeing. A mesher that throws must leave the old handle live and drawable rather
                // than a freed one in the slot, which would be a use-after-free on the next frame.
                MeshHandle? rebuilt = BuildMesh(region, plane);
                if (handles.Meshes[plane] is { } old) _scene.UnloadMesh(old);
                handles.Meshes[plane] = rebuilt;
                handles.Props[plane] = TileObjectProps.Build(_doc, _catalogs, region, plane, OverrideLookup());
                // Only a mesh that was actually built counts. The budget exists to bound uploads and handle
                // churn, and an empty region-plane produces neither.
                if (rebuilt is not null) taken++;
            }
        }
        finally
        {
            // In a finally so a throwing mesher cannot leave the set and the list disagreeing, which would let a
            // later MarkDirty push a duplicate of an entry the list still holds.
            _dirtyOrder.RemoveRange(0, scanned);
        }
    }

    /// <summary>Flushes pending rebuilds, then queues every loaded region: each plane's ground mesh at the
    /// region's world transform, that plane's ground props, and every roof <see cref="IsRoofHidden"/> leaves
    /// visible. <paramref name="focus"/> is the point the prop draw radius is measured from, which is the
    /// camera subject rather than the observer tile, so a camera pulled back from an indoor observer still draws
    /// the props around it.</summary>
    public void Draw(Vector3 focus)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Flush();

        int drawn = 0;
        foreach (KeyValuePair<RegionCoord, RegionHandles> entry in _loaded)
        {
            Matrix4x4 world = TileGroundMesher.WorldMatrix(_doc, entry.Key);
            RegionHandles handles = entry.Value;
            for (int plane = 0; plane < _planes; plane++)
            {
                if (handles.Meshes[plane] is { } mesh) _scene.DrawMesh(mesh, world);

                TileRegionProps props = handles.Props[plane];
                if (props.Ground.Count > 0)
                    drawn += _scene.DrawProps(props.Ground, _propMeshes, focus, _options.PropDrawRadius);
                if (props.Roofs.Count > 0)
                {
                    IReadOnlyList<PropPlacement> roofs = VisibleRoofs(props, plane);
                    if (roofs.Count > 0)
                        drawn += _scene.DrawProps(roofs, _propMeshes, focus, _options.PropDrawRadius);
                }
            }
        }
        LastDrawnProps = drawn;
        // Water rides the same frame: the planes are cached per region-plane and re-collected only when that
        // region-plane's mesh or the look changed, so this is a walk over the loaded regions and one submit per
        // plane. A caller that runs its own water pass turns it off with TileWorldViewOptions.DrawWater.
        if (_options.DrawWater) DrawWaterPlanes();
        if (_silhouettedObject != 0L) DrawSilhouettedObject(focus);
    }

    // The silhouetted object, 0 for none. The id resolves through the document's own O(1) object index
    // (FindObject) every frame, never through the region accessors: GetOrCreateRegion THROWS for an unloaded
    // hash and INSERTS a dirty region for an absent coord, and a frame loop must do neither.
    long _silhouettedObject;
    Color _silhouetteColor;
    float _silhouetteWidth;

    /// <summary>The default hull width for an object silhouette, in metres.</summary>
    public const float DefaultSilhouetteWidthMetres = 0.05f;

    /// <summary>Silhouettes one authored object (the per-entity highlight for a clicked or selected prop): its
    /// parts re-draw as an inverted hull in <paramref name="color"/> every frame until
    /// <see cref="ClearSilhouettedObject"/>. An id the loaded regions do not hold draws nothing that frame and
    /// self-corrects when its region streams in, so a caller can set it optimistically.</summary>
    /// <param name="objectId">The object's document id, from a pick.</param>
    /// <param name="color">The hull colour. Alpha blends.</param>
    /// <param name="widthMetres">The hull width. Defaults to <see cref="DefaultSilhouetteWidthMetres"/>.</param>
    public void SetSilhouettedObject(long objectId, Color color, float widthMetres = DefaultSilhouetteWidthMetres)
    {
        _silhouettedObject = objectId;
        _silhouetteColor = color;
        _silhouetteWidth = widthMetres;
    }

    /// <summary>Stops silhouetting. Safe when nothing is silhouetted.</summary>
    public void ClearSilhouettedObject() => _silhouettedObject = 0L;

    // Finds the silhouetted object through the document's object index and queues its parts as hulls, the
    // placement derived exactly the way TileObjectProps builds a prop draw (anchor position, yaw, scale 1), so
    // the hull sits on the same transform the prop was drawn at. The gates the prop draw applies apply here
    // too: an object outside the prop draw radius, or a roof the same IsRoofHidden predicate hides, draws no
    // hull, because a hull whose model is not drawn has nothing to eat its middle and reads as a solid blob.
    void DrawSilhouettedObject(Vector3 focus)
    {
        if (_doc.FindObject(_silhouettedObject) is not { } o) return;
        // Through the override, never off the document: a hull built on the authored archetype while the prop
        // draws an overridden one would sit on a different mesh and a different anchor, which reads as a hull
        // floating beside the thing it is meant to outline.
        string archetypeId = ArchetypeFor(o);
        if (_catalogs.Archetype(archetypeId) is not { } archetype) return;
        if (archetype.IsRoof && IsRoofHidden(TileFootprint.Of(archetype, o.X, o.Z, o.Rotation), o.Plane)) return;
        if (!_propMeshes.TryGetValue(archetypeId, out IReadOnlyList<MeshHandle>? parts)) return;
        Vector3 at = TileObjectProps.AnchorPosition(_doc, archetype, o);
        float dx = at.X - focus.X;
        float dz = at.Z - focus.Z;
        if (dx * dx + dz * dz > _options.PropDrawRadius * _options.PropDrawRadius) return;
        Matrix4x4 world = Matrix4x4.CreateRotationY(TileObjectProps.YawRadians(archetype, o.Rotation))
            * Matrix4x4.CreateTranslation(at);
        for (int i = 0; i < parts.Count; i++)
            _scene.DrawMeshSilhouette(parts[i], world, _silhouetteColor, _silhouetteWidth);
    }

    /// <summary>The ground materials every region-plane mesh of this view is drawn with, which is also the slot
    /// map its meshes were built against.</summary>
    public TileGroundMaterialSet GroundMaterials => _materials;

    /// <summary>Frees every region, the ground material set and every archetype mesh set this view uploaded. The
    /// scene itself is not owned, so it outlives the view. Safe to call twice.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (RegionHandles handles in _loaded.Values) FreeMeshes(handles);
        _loaded.Clear();
        _dirty.Clear();
        _dirtyOrder.Clear();

        foreach (IReadOnlyList<MeshHandle> parts in _propMeshes.Values) _scene.UnloadPropMeshes(parts);
        _propMeshes.Clear();
        _archetypeOverrides.Clear();

        if (_material.IsValid) _scene.UnloadTileGroundMaterial(_material);
    }

    /// <summary>The roof rule, and the whole of it. Under <see cref="RoofVisibility.Interior"/> a roof is hidden
    /// when the observer is indoors, the roof sits on a plane ABOVE the observer's own (so the storey they stand
    /// on keeps its own ceiling and only what is between them and the camera goes), and the roof's footprint
    /// touches the observer's interior, which is what keeps the rule to one building. The other two modes answer
    /// without looking at the world at all.</summary>
    /// <param name="footprint">The world tile rect the roof covers, from <see cref="TileFootprint.Of"/>. An
    /// empty rect is never hidden by the interior rule.</param>
    /// <param name="plane">The plane the roof stands on.</param>
    public bool IsRoofHidden(TileRect footprint, int plane)
    {
        EnsureInterior();
        return RoofMode switch
        {
            RoofVisibility.AlwaysVisible => false,
            RoofVisibility.AlwaysHidden => true,
            _ => ObserverIndoors && plane > _observer.Plane && _interior.Intersects(footprint),
        };
    }

    // The roofs of one region-plane this frame may draw. The region's OWN list comes back untouched whenever
    // nothing on the plane can be hidden, which is every outdoor frame and every plane at or below the observer,
    // so the common case copies nothing at all. Otherwise the one scratch list is refilled, which is safe to
    // reuse across the region-planes of a frame because DrawProps reads it during the call (ITileWorldScene).
    IReadOnlyList<PropPlacement> VisibleRoofs(TileRegionProps props, int plane)
    {
        if (!AnyRoofHiddenOn(plane)) return props.Roofs;
        if (RoofMode != RoofVisibility.Interior) return Array.Empty<PropPlacement>();

        _visibleRoofs.Clear();
        IReadOnlyList<TileRect> footprints = props.RoofFootprints;
        for (int i = 0; i < props.Roofs.Count; i++)
        {
            // A roof the footprint list does not reach is one nothing placed, so it is not part of any interior
            // and stays visible. TileObjectProps.Build always fills the list, so this is the hand-built case.
            TileRect footprint = i < footprints.Count ? footprints[i] : default;
            if (!_interior.Intersects(footprint)) _visibleRoofs.Add(props.Roofs[i]);
        }
        return _visibleRoofs;
    }

    // Whether the mode and the observer can hide ANY roof on this plane, which is the per-region-plane gate that
    // keeps the per-roof test off the outdoor path entirely.
    bool AnyRoofHiddenOn(int plane) => RoofMode switch
    {
        RoofVisibility.AlwaysVisible => false,
        RoofVisibility.AlwaysHidden => true,
        _ => ObserverIndoors && plane > _observer.Plane && _interior.Count > 0,
    };

    // Refills the observer's interior when a move or an edit left it stale. Lazy rather than eager so a game
    // that sets Observer every frame pays one flood fill per tile it actually walks onto.
    void EnsureInterior()
    {
        if (!_interiorStale) return;
        _interiorStale = false;
        _interior.Rebuild(_doc, _observer, ObserverIndoors);
        if (!_interior.Truncated || _interiorTruncationLogged) return;
        // Once per view: the observer standing in an over-flagged interior would otherwise log every frame they
        // walk through it. Developer diagnostics, never shown to a player, so it is deliberately not localized.
        _interiorTruncationLogged = true;
        _options.Log?.Invoke($"tile world: the interior around tile ({_observer.X}, {_observer.Z}) on plane " +
                             $"{_observer.Plane} is larger than {MaxInteriorTiles} tiles, so the roofs past that " +
                             "stay visible. Check the Indoors flags there.");
    }

    bool IndoorsAt(TileCoord tile) => (_doc.GetSettings(tile.X, tile.Z, tile.Plane) & TileSettings.Indoors) != 0;

    // The one place the dedup set and the order list are appended to, so they cannot drift apart.
    void Queue(RegionCoord region, int plane)
    {
        // An edit reaches the view through MarkDirty and nowhere else, so this is also where the interior hears
        // that the tiles it was filled over may have moved under it.
        _interiorStale = true;
        if (_dirty.Add((region, plane))) _dirtyOrder.Add((region, plane));
    }

    MeshHandle? BuildMesh(RegionCoord region, int plane)
    {
        GltfMesh? mesh = TileGroundMesher.Build(_doc, _catalogs, region, plane, _options.Mesher);
        return mesh is null ? null : _scene.LoadMesh(mesh, _material);
    }

    void FreeMeshes(RegionHandles handles) => FreeMeshes(handles.Meshes);

    // Takes the array rather than the record, so the rollback in LoadRegion can free a half-filled plane array
    // that never became a RegionHandles. Nulling as it goes keeps it safe to call twice.
    void FreeMeshes(MeshHandle?[] meshes)
    {
        for (int plane = 0; plane < meshes.Length; plane++)
        {
            if (meshes[plane] is { } mesh) _scene.UnloadMesh(mesh);
            meshes[plane] = null;
        }
    }

    // One loaded region: the ground mesh handle of each plane (null where the plane has no drawable tile) and
    // that plane's placements. Dropped whole on unload, so a region never leaves half its handles behind.
    sealed record RegionHandles(MeshHandle?[] Meshes, TileRegionProps[] Props);
}
