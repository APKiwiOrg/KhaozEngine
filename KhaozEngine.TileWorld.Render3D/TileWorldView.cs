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
    /// <summary>Optical depth policy, captured when the view is constructed.</summary>
    public TileWaterDepthMode WaterDepthMode { get; init; } = TileWaterDepthMode.SceneDepth;

    /// <summary>Horizontal radius in metres around the draw focus inside which a prop is drawn.</summary>
    public float PropDrawRadius { get; set; } = 96f;

    /// <summary>Opt-in disjoint archetype groups represented by shared LOD and HLOD clusters. Empty preserves
    /// ordinary prop drawing.</summary>
    public IReadOnlyList<TilePropLayerDefinition> PropLayers { get; set; } = Array.Empty<TilePropLayerDefinition>();

    /// <summary>Dedicated geometry reader used only by this view's serialized interior builds. Null disables
    /// the field. Seeds <see cref="TileWorldView.InteriorMeshes"/> at construction, including snapshot captures.</summary>
    public ITileMeshGeometry? InteriorMeshes { get; set; }

    /// <summary>Initial roof rule, including snapshot captures. Runtime changes use
    /// <see cref="TileWorldView.RoofMode"/>.</summary>
    public RoofVisibility RoofMode { get; set; } = RoofVisibility.Interior;

    /// <summary>Live distance, quality and shadow policy for authored ground cover.</summary>
    public GroundCoverRenderOptions GroundCover { get; set; } = new();

    /// <summary>Authored non-roof archetypes drawn as animated foliage. Empty preserves ordinary prop draws.
    /// Selected props keep their full density and PropDrawRadius, independent of short-grass quality.</summary>
    public IReadOnlySet<string> AnimatedFoliageArchetypes { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Live motion and casting for selected animated archetypes. An omitted style uses Blade motion
    /// without casting. Entries outside AnimatedFoliageArchetypes and conflicting styles on shared meshes are refused.</summary>
    public IReadOnlyDictionary<string, AnimatedFoliageStyle> AnimatedFoliageStyles
    {
        get => _animatedFoliageStyles;
        set => _animatedFoliageStyles = value ?? throw new ArgumentNullException(nameof(AnimatedFoliageStyles));
    }

    IReadOnlyDictionary<string, AnimatedFoliageStyle> _animatedFoliageStyles =
        new Dictionary<string, AnimatedFoliageStyle>(StringComparer.Ordinal);

    /// <summary>The settings every region-plane ground mesh is built with. The view SETS
    /// <see cref="TileGroundMesherOptions.Slots"/> on this object when it is constructed, to the material set it
    /// is about to upload, because the slots a vertex names only mean anything against that set.
    /// <para>The view then COPIES these settings and builds every mesh from its copy, on the view thread and on a
    /// worker alike, so a later change to this object, or a replacement of it, reaches no mesh of that view. A
    /// live read would apply a change only to the region-planes rebuilt after it, which is a seam at every border
    /// with an older mesh, and a replaced object would carry the identity slot map rather than the uploaded set.
    /// Build a new view to change them, the same as <see cref="GroundMaterials"/>.</para></summary>
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

    // One shared unit box, uploaded at most once per view and shared by every unresolved archetype. Built here
    // rather than asked of the greybox resolver, because the placeholder must not depend on the archetype's
    // footprint: a missing 2x2 mesh should read as "nothing resolved this", not as a plausible 2x2 object.
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
    readonly Func<int, int, int, bool> _terrainPickFilter;
    readonly Func<TileObject, bool> _walkSurfacePickFilter;
    readonly Dictionary<string, IReadOnlyList<MeshHandle>> _propMeshes = new(StringComparer.Ordinal);
    // The one upload of PlaceholderParts every unresolved archetype's _propMeshes entry points at, null until an
    // archetype needs it. Freed once by UnloadPropMeshes, never per archetype.
    IReadOnlyList<MeshHandle>? _placeholderMeshes;
    // The mesher settings as they stood at construction, which every ground build reads. See
    // TileWorldViewOptions.Mesher for why a copy rather than the caller's object.
    readonly TileGroundMesherOptions _mesher;
    TileWorldPropClusters _propClusters;
    readonly Dictionary<RegionCoord, RegionHandles> _loaded = new();
    // The rebuild queue is a pair on purpose: the set is the dedup (a stroke marks the same region-plane a
    // hundred times) and the list is the ORDER, which matters once a flush is budgeted, because the oldest mark
    // is the one whose mesh has been wrong the longest. The two are always mutated together.
    readonly HashSet<(RegionCoord Region, int Plane)> _dirty = new();
    readonly List<(RegionCoord Region, int Plane)> _dirtyOrder = new();
    readonly int _planes;
    readonly TileGroundMaterialSet _materials;
    readonly TileGroundMaterialHandle _material;
    // The observer's own interior. The roof rule that reads it, and the scratch lists it splits a region-plane's
    // roofs into, live in TileWorldView.Roofs.cs.
    readonly TileInteriorFill _interior = new();
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
        : this(scene, doc, catalogs, resolver, options, buildQueueOptions: null, dispatcher: null)
    {
    }

    internal TileWorldView(ITileWorldScene scene, TileWorldDocument doc, TileWorldCatalogs catalogs,
                           ITileMeshResolver resolver, TileWorldViewOptions? options,
                           TileWorldBuildQueueOptions? buildQueueOptions, IChunkBuildDispatcher? dispatcher,
                           Action<TileWorldBuildKey>? onCpuBuild = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(resolver);

        _scene = scene;
        _doc = doc;
        _catalogs = catalogs;
        _options = options ?? new TileWorldViewOptions();
        _waterDepthMode = _options.WaterDepthMode;
        if (!Enum.IsDefined(_waterDepthMode)) throw new ArgumentOutOfRangeException(nameof(options));
        _opticsScene = _waterDepthMode == TileWaterDepthMode.AuthoredBed ? scene as ITileWorldWaterTriangleOpticsScene : null;
        if (_waterDepthMode == TileWaterDepthMode.AuthoredBed && _opticsScene is null)
            throw new NotSupportedException("Authored bed water requires triangle optical field preparation.");
        _publication = new TileWaterPublication(_opticsScene);
        ResolveWaterDisplacement();
        _acceptedSurface = AcceptedGroundSurface;
        _waterTileSize = doc.TileSize;
        _waterPlaneHeight = doc.PlaneHeight;
        _interiorMeshes = _options.InteriorMeshes;
        RoofMode = _options.RoofMode;
        _animatedFoliageStyles = new AnimatedFoliageStyleSnapshot(_options.AnimatedFoliageArchetypes,
            _options.AnimatedFoliageStyles, catalogs, _propMeshes);
        ArgumentNullException.ThrowIfNull(_options.PropLayers);
        TileWorldPropClusters.ValidatedLayers validatedPropLayers =
            TileWorldPropClusters.Validate(catalogs, _options.PropLayers);
        _planes = Math.Max(0, doc.PlaneCount);
        _terrainPickFilter = IsRenderedTerrain;
        _walkSurfacePickFilter = IsGameplayObject;
        _onCpuBuild = onCpuBuild;
        _propLayerResolver = resolver;
        _propLayerQueueOptions = buildQueueOptions;
        _propLayerDispatcher = dispatcher;
        RememberInteriorLayers(validatedPropLayers);
        _interiorBuilds = new TileWorldBuildQueue<TileInteriorBuildInput, TileInteriorBuildOutput>(
            BuildInteriorCpu, ApplyInteriorBuild,
            (buildQueueOptions ?? new TileWorldBuildQueueOptions()) with { MaxConcurrentBuilds = 1 }, dispatcher);
        _profileBuilds = new TileWorldBuildQueue<TileProfileBuildInput, TileProfileBuildOutput>(
            BuildProfileCpu, ApplyProfileBuild, buildQueueOptions, dispatcher);

        // A throw part way through frees the sets already uploaded, because a constructor that throws never
        // produces the object whose Dispose would have freed them. The resolver is caller code and the upload is a
        // device call, so either can fail on the ninth archetype of twelve, and without this the first eight are
        // stranded on the device with nothing left holding their handles.
        try
        {
            _materials = _options.GroundMaterials ?? TileGroundMaterials.Build(catalogs);
            // Written into the caller's mesher settings first, so a caller who reads them back sees the slot map
            // the meshes are actually built against, then copied. The copy is what every build reads, and it is
            // TileGroundMesherOptions.Copy, which sits beside the settings so a new one is not silently dropped.
            _options.Mesher.Slots = _materials;
            _mesher = _options.Mesher.Copy();
            _material = scene.LoadTileGroundMaterial(_materials);

            foreach (KeyValuePair<string, TileObjectArchetype> entry in catalogs.Archetypes)
            {
                IReadOnlyList<GltfMeshPart>? parts = resolver.Resolve(entry.Value);
                if (parts is null || parts.Count == 0)
                {
                    _options.Log?.Invoke($"tile world: archetype '{entry.Key}' has no mesh, drawing a placeholder box.");
                    _propMeshes[entry.Key] = _placeholderMeshes ??= scene.LoadPropMeshes(PlaceholderParts);
                    continue;
                }
                _propMeshes[entry.Key] = scene.LoadPropMeshes(parts);
            }
            _animatedFoliageStyles = new AnimatedFoliageStyleSnapshot(_options.AnimatedFoliageArchetypes,
                _options.AnimatedFoliageStyles, catalogs, _propMeshes);
            _propClusters = new TileWorldPropClusters(scene, catalogs, resolver, _propMeshes, validatedPropLayers,
                doc.TileSize, buildQueueOptions, dispatcher);
        }
        catch
        {
            _profileBuilds.Dispose();
            DisposeInteriorCut();
            UnloadPropMeshes();
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

    /// <summary>Whether <paramref name="tile"/> is part of the observer's current 4-connected indoor area.
    /// This is the interior query itself, independent of <see cref="RoofMode"/> and of whether a roof occupies
    /// the tile. A tile on another plane is never in the observer's interior.</summary>
    /// <param name="tile">The world tile to test, including its plane.</param>
    public bool IsObserverInterior(TileCoord tile)
    {
        EnsureInterior();
        return tile.Plane == _interior.Plane && _interior.Contains(tile.X, tile.Z);
    }

    /// <summary>How many prop placements the last <see cref="Draw"/> queued, ordinary and layered roofs included when shown.
    /// Merged layer handles count their represented placements once.
    /// Animated foliage contributes conservative submitted candidates, including model parts.</summary>
    public int LastDrawnProps { get; private set; }

    /// <summary>How many roof placements the last <see cref="Draw"/> queued as SHADOW-ONLY casters: hidden from
    /// the eye by <see cref="RoofMode"/> and still recording depth for the key light, so the room under them
    /// stays shaded. Includes individual and merged roof layers. Counted apart from <see cref="LastDrawnProps"/>,
    /// which stays the visible total.</summary>
    public int LastShadowOnlyProps { get; private set; }

    /// <summary>How many regions are loaded right now.</summary>
    public int LoadedRegionCount => _loaded.Count;

    /// <summary>The representation currently retained for <paramref name="region"/>, or
    /// <see cref="TileRegionResidencyState.Unloaded"/> when the view does not hold it.</summary>
    /// <param name="region">Region to inspect.</param>
    public TileRegionResidencyState ResidencyOf(RegionCoord region) =>
        _loaded.TryGetValue(region, out RegionHandles? handles)
            ? handles.Residency
            : TileRegionResidencyState.Unloaded;

    /// <summary>How many region-planes are queued for a rebuild, including marks on regions that are not loaded
    /// and will simply be dropped. Non-zero after a flush means the budget deferred work to the next one.</summary>
    public int PendingRebuilds => _dirtyOrder.Count;

    /// <summary>A snapshot of the loaded regions, in no particular order. A copy rather than a live view, so a
    /// caller may load or unload regions while walking it, which is exactly what a residency ring does. Allocates a
    /// fresh array on every read, so a per-frame caller uses <see cref="CollectLoadedRegions"/> instead.</summary>
    public IReadOnlyCollection<RegionCoord> LoadedRegions
    {
        get
        {
            var snapshot = new RegionCoord[_loaded.Count];
            _loaded.Keys.CopyTo(snapshot, 0);
            return snapshot;
        }
    }

    /// <summary>Fills a caller's collection with the loaded regions, the same set <see cref="LoadedRegions"/>
    /// snapshots, in no particular order. Cleared first. The filled collection is the caller's own, so it is as
    /// safe to walk while loading or unloading as the snapshot is, and a warm call allocates nothing: the regions
    /// are walked through the dictionary's struct enumerator and a reused buffer never has to grow.</summary>
    /// <param name="into">The buffer to fill, a <see cref="List{T}"/> or a <see cref="HashSet{T}"/> reused across
    /// calls.</param>
    public void CollectLoadedRegions(ICollection<RegionCoord> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        foreach (RegionCoord region in _loaded.Keys) into.Add(region);
    }

    /// <summary>Gets the immutable prop snapshot retained for one loaded region-plane.</summary>
    public bool TryGetRegionProps(RegionCoord region, int plane, out TileRegionProps snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)plane < (uint)_planes && _loaded.TryGetValue(region, out RegionHandles? handles))
        {
            snapshot = handles.Props[plane];
            return true;
        }
        snapshot = null!;
        return false;
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

        try { LoadGameplayRegion(region, props: null); }
        finally { SweepWaterMetadata(); }
    }

    internal void LoadRegion(RegionCoord region, TileRegionResidencyState residency)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _interiorInputsDirty = true;
        if (residency == TileRegionResidencyState.Unloaded)
        {
            UnloadRegion(region);
            return;
        }
        if (_loaded.TryGetValue(region, out RegionHandles? existing))
        {
            if (existing.Residency == residency) return;
            if (residency == TileRegionResidencyState.Decor)
            {
                DemoteToDecor(region, existing);
                return;
            }
            PromoteToGameplay(region, existing);
            return;
        }
        try { LoadProfileRegion(region, residency); }
        finally { SweepWaterMetadata(); }
    }

    /// <summary>Frees every mesh handle of one region and forgets it, along with any rebuild it had queued.
    /// Unloading a region that is not loaded does nothing.</summary>
    public void UnloadRegion(RegionCoord region)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WaterAvailabilityChanged(region);
        _interiorInputsDirty = true;
        for (int plane = 0; plane < _planes; plane++)
        {
            if (_dirty.Remove((region, plane))) _dirtyOrder.Remove((region, plane));
            ReleaseWater(region, plane);
            CancelGround(region, plane);
            _propClusters.Unload(region, plane);
        }
        CancelCover(region);
        ReleaseWaterSourceRefusals(region);
        if (_loaded.Remove(region, out RegionHandles? handles))
        {
            GeneratedCoverCount -= handles.Cover.Count;
            FreeMeshes(handles);
            // Fields elsewhere that consumed these surfaces cannot outlive the drawn bed.
            for (int plane = 0; plane < _planes; plane++) _publication.SourceRemoved((region, plane));
        }
        SweepWaterMetadata();
    }

    /// <summary>Queues one region-plane for a rebuild at the next <see cref="Flush()"/>. A plane outside the
    /// document is ignored, and a region that is not loaded is dropped when the flush runs.</summary>
    public void MarkDirty(RegionCoord region, int plane)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (plane < 0 || plane >= _planes) return;
        WaterHeightChanged(region, plane);
        MarkInteriorDirty(region.Rect, plane);
        MarkCoverObjectsStale(region);
        Queue(region, plane);
    }

    /// <summary>Queues every loaded region a world-space tile rect can affect on one plane. Ground meshes use
    /// the fixed <see cref="DirtyRegionMargin"/> for shared corners, normals and colours. Ground cover also uses
    /// the largest active same-plane door clearance, so a distant cached region cannot retain foliage inside a
    /// newly edited exclusion. This is the edit-facing overload, where an editor hands over the tiles it wrote
    /// and the view works out which regions that is.</summary>
    public void MarkDirty(TileRect worldRect, int plane)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (worldRect.IsEmpty || plane < 0 || plane >= _planes) return;
        RegionCoord waterMin = RegionCoord.Of(worldRect.X, worldRect.Z);
        RegionCoord waterMax = RegionCoord.Of(worldRect.X1 - 1, worldRect.Z1 - 1);
        for (int z = waterMin.Rz; z <= waterMax.Rz; z++)
            for (int x = waterMin.Rx; x <= waterMax.Rx; x++) WaterHeightChanged(new RegionCoord(x, z), plane);
        MarkInteriorDirty(worldRect, plane);
        MarkCoverObjectsStale(worldRect);
        QueueBaseDependencies(worldRect, plane, excluded: null);
        QueueFoliageDependencies(worldRect, plane, excluded: null);
    }

    void QueueBaseDependencies(TileRect worldRect, int plane, RegionCoord? excluded)
    {
        TileRect reach = worldRect.Expand(DirtyRegionMargin);
        RegionCoord min = RegionCoord.Of(reach.X, reach.Z);
        RegionCoord max = RegionCoord.Of(reach.X1 - 1, reach.Z1 - 1);
        for (int rz = min.Rz; rz <= max.Rz; rz++)
            for (int rx = min.Rx; rx <= max.Rx; rx++)
            {
                var region = new RegionCoord(rx, rz);
                if (region != excluded) Queue(region, plane);
            }
    }

    /// <summary>Flushes pending rebuilds, then queues every loaded region: each plane's ground mesh at the
    /// region's world transform, that plane's ground props, and every roof <see cref="IsRoofHidden"/> leaves
    /// visible. A roof it HIDES is queued as a shadow-only caster instead of dropped, so the building it covers
    /// keeps its shade (see TileWorldView.Roofs.cs). <paramref name="focus"/> is the point the prop draw radius is
    /// measured from, which is the camera subject rather than the observer tile, so a camera pulled back from an
    /// indoor observer still draws the props around it.</summary>
    public void Draw(Vector3 focus)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateAnimatedFoliageStyles();
        RefreshInteriorLayers();
        Flush();
        PumpInteriorCut(focus);
        PumpProfileBuilds(focus);
        _propClusters.Pump(focus);

        int drawn = 0, shadowOnly = 0;
        foreach (KeyValuePair<RegionCoord, RegionHandles> entry in _loaded)
        {
            Matrix4x4 world = TileGroundMesher.WorldMatrix(_doc, entry.Key);
            RegionHandles handles = entry.Value;
            for (int plane = 0; plane < _planes; plane++)
            {
                if (handles.Meshes[plane] is { } mesh) _scene.DrawMesh(mesh, world);

                if (handles.Residency != TileRegionResidencyState.Gameplay) continue;

                TileRegionProps props = handles.Props[plane];
                drawn += DrawGroundProps(handles, plane, focus);
                if (props.Roofs.Count > 0) drawn += DrawRoofs(props, plane, focus, ref shadowOnly);
            }
        }
        PropClusterDrawCounts layerCounts = _propClusters.Draw(focus, RoofRule);
        drawn += layerCounts.Visible;
        shadowOnly += layerCounts.ShadowOnly;
        LastDrawnProps = drawn;
        LastShadowOnlyProps = shadowOnly;
        DrawCover(focus);
        // Water rides the same frame: the planes are cached per region-plane and re-collected only when that
        // region-plane's mesh or the look changed, so this is a walk over the loaded regions and one submit per
        // plane. A caller that runs its own water pass turns it off with TileWorldViewOptions.DrawWater.
        if (_options.DrawWater) DrawWaterPlanes();
        if (_silhouettedObject != 0L) DrawSilhouettedObject(focus);
        if (_outlinedObject != 0L) DrawOutlinedObject(focus);
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
        bool cutInsideInteriors = CutArchetypeIds.Contains(archetypeId);
        for (int i = 0; i < parts.Count; i++)
            _scene.DrawMeshSilhouette(parts[i], world, _silhouetteColor, _silhouetteWidth, cutInsideInteriors);
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
        ReleaseAllWater();
        _dirty.Clear();
        _dirtyOrder.Clear();

        DisposeInteriorCut();
        _profileBuilds.Dispose();

        // The clusters hold the archetype handles and may still have builds in flight, so they drain before the
        // handles they reference are freed.
        _propClusters.Dispose();

        UnloadPropMeshes();
        _archetypeOverrides.Clear();

        if (_material.IsValid) _scene.UnloadTileGroundMaterial(_material);
    }

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

    // A complete fill can only change when an edit touches it or one of the four-connected tiles it could grow
    // into. A truncated fill does not know the bounds of the interior it omitted, so every same-plane mark stays
    // conservative there. Observer moves and the indoor flag under a stationary observer have their own gates.
    void MarkInteriorDirty(TileRect worldRect, int plane)
    {
        _interiorInputsDirty = true;
        if (_interiorStale || plane != _interior.Plane) return;
        if (_interior.Truncated || _interior.Bounds.Expand(1).Intersects(worldRect)) _interiorStale = true;
    }

    // The one place the dedup set and the order list are appended to, so they cannot drift apart.
    void Queue(RegionCoord region, int plane)
    {
        if (_dirty.Add((region, plane))) _dirtyOrder.Add((region, plane));
    }

    // Frees every archetype's parts exactly once. The placeholder set is shared by every unresolved archetype, so
    // the walk skips it and it is freed on its own afterwards.
    void UnloadPropMeshes()
    {
        foreach (IReadOnlyList<MeshHandle> parts in _propMeshes.Values)
            if (!ReferenceEquals(parts, _placeholderMeshes)) _scene.UnloadPropMeshes(parts);
        _propMeshes.Clear();
        if (_placeholderMeshes is not null) _scene.UnloadPropMeshes(_placeholderMeshes);
        _placeholderMeshes = null;
    }

    void FreeMeshes(RegionHandles handles)
    {
        _scene.ReleaseGroundCover(handles.Cover);
        ReleaseAnimatedFoliage(handles);
        FreeMeshes(handles.Meshes);
        Array.Clear(handles.Surfaces);
    }

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

    void RequestPropClusters(IReadOnlyList<TileRegionProps> snapshots)
    {
        for (int plane = 0; plane < snapshots.Count; plane++)
            _propClusters.Request(snapshots[plane]);
    }

    // One loaded region: the ground mesh handle of each plane (null where the plane has no drawable tile) and
    // that plane's placements. Dropped whole on unload, so a region never leaves half its handles behind. A
    // profile-managed region streams its ground and cover through the shared queue, and records the generation
    // each kind of result must carry to be applied. Zero expects nothing.
    sealed class RegionHandles
    {
        public MeshHandle?[] Meshes { get; }
        public TileGroundSurfaceSnapshot?[] Surfaces { get; }
        public TileRegionProps[] Props { get; }
        public IReadOnlyList<GroundCoverInstance> Cover { get; set; }
        public TileRegionResidencyState Residency { get; set; }
        public bool ProfileManaged { get; set; }
        public long[] FullGroundGenerations { get; }
        public long[] CoarseGroundGenerations { get; }
        public long CoverGeneration { get; set; }

        public RegionHandles(MeshHandle?[] meshes, TileRegionProps[] props, IReadOnlyList<GroundCoverInstance> cover,
                             TileRegionResidencyState residency, bool profileManaged)
        {
            Meshes = meshes;
            Surfaces = new TileGroundSurfaceSnapshot?[meshes.Length];
            Props = props;
            Cover = cover;
            Residency = residency;
            ProfileManaged = profileManaged;
            FullGroundGenerations = new long[meshes.Length];
            CoarseGroundGenerations = new long[meshes.Length];
        }
    }
}
