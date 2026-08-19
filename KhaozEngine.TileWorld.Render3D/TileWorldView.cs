using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using TileGroundMaterialHandle = KhaozEngine.Render3D.Scene3D.TileGroundMaterialHandle;

namespace KhaozEngine.TileWorld;

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
/// hides the planes above an indoor observer. Edits are announced with <see cref="MarkDirty(RegionCoord, int)"/>
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
    TileCoord _observer;
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

    /// <summary>The tile the roof rule is judged from. Setting it recomputes <see cref="ObserverIndoors"/>.</summary>
    public TileCoord Observer
    {
        get => _observer;
        set
        {
            _observer = value;
            ObserverIndoors = IndoorsAt(value);
        }
    }

    /// <summary>Whether the observer's tile is flagged indoors, which hides the roofs on the planes above it.</summary>
    public bool ObserverIndoors { get; private set; }

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
                props[plane] = TileObjectProps.Build(_doc, _catalogs, region, plane);
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
        // observer is edited: the setter alone only sees the observer moving.
        ObserverIndoors = IndoorsAt(_observer);
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
                handles.Props[plane] = TileObjectProps.Build(_doc, _catalogs, region, plane);
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
    /// region's world transform, that plane's ground props, and its roofs unless the observer stands indoors on a
    /// lower plane. <paramref name="focus"/> is the point the prop draw radius is measured from, which is the
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
                if (props.Roofs.Count > 0 && !RoofsHiddenOn(plane))
                    drawn += _scene.DrawProps(props.Roofs, _propMeshes, focus, _options.PropDrawRadius);
            }
        }
        LastDrawnProps = drawn;
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

        if (_material.IsValid) _scene.UnloadTileGroundMaterial(_material);
    }

    // The roof rule: standing indoors hides the roofs of every plane ABOVE the observer's own, so the storey the
    // observer is on keeps its own props and the ceiling between them and the camera goes.
    bool RoofsHiddenOn(int plane) => ObserverIndoors && plane > _observer.Plane;

    bool IndoorsAt(TileCoord tile) => (_doc.GetSettings(tile.X, tile.Z, tile.Plane) & TileSettings.Indoors) != 0;

    // The one place the dedup set and the order list are appended to, so they cannot drift apart.
    void Queue(RegionCoord region, int plane)
    {
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
