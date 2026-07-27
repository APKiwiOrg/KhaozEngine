using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEditor;

/// <summary>Owns the streamed world for the editor viewport: Room3D's lifecycle recipe behind one class. It
/// builds the <see cref="TerrainField"/> + <see cref="MapRuntime"/> scatter configs + <see cref="PropLayer"/>s +
/// <see cref="Scene3DChunkSink"/> + <see cref="TerrainStreamer"/> from the document, loads kit meshes from the
/// asset manifests, and primes the ring. <see cref="Rebuild"/> is NOT fully wholesale: it tears down and
/// reconstructs the streaming state (field, sink, streamer, ring) every time, but the loaded kit meshes and the
/// splat material persist across rebuilds (a rebuild's <see cref="LoadKitMeshes"/> pass is then a natural no-op,
/// since it skips any id already in the cache), so a rebuild stops re-decoding every prop glTF from disk. Call
/// <see cref="InvalidateKitMeshes"/> before a rebuild when the cached form would otherwise go stale (e.g. the
/// textured-props toggle, since the cache key is the entry id alone and does not encode which form was loaded).
/// Authored placements and spawn markers draw OUTSIDE the sink so transform drags never trigger a rebuild, and
/// the selected placement re-draws with the highlight tint through the per-call tint surface.
/// <para>The class is split so the GPU-free surface (manifest parsing into <see cref="KindHeights"/>, the
/// Build/Rebuild/Dispose state guards, the placement cache, and the selected/unselected <see cref="Partition"/>)
/// is testable without a device. Every GPU-touching call lives behind a small private method. It touches its
/// <see cref="Scene3D"/> only from <see cref="Build"/> onward, so the ctor and the state guards run headless.</para>
/// </summary>
public sealed class ViewportWorld : IDisposable
{
    /// <summary>Horizontal cull radius (m) for streamed companion foliage. Deliberately NOT part of
    /// <see cref="RenderDistance"/>: dense understory is a near-field layer whose cost is per-instance, so a short
    /// radius keeps it affordable however far the horizon reaches (per the multi-layer sink design).</summary>
    const float CompanionDrawRadius = 60f;

    /// <summary>Authored placements are the content being edited, so they are effectively never distance-culled
    /// (a very wide draw ring). Streamed scatter still uses <see cref="RenderDistanceProfile.PropDrawRadius"/>.</summary>
    const float AuthoredDrawRadius = 100_000f;

    /// <summary>Spawn-marker billboard half-size (the disc spans twice this) and its lift above the ground so it
    /// reads as a floating pin rather than z-fighting the terrain.</summary>
    const float SpawnMarkerSize = 0.6f;
    const float SpawnMarkerLift = 1.0f;

    static readonly Color EnabledSpawnColor = new(0.25f, 0.7f, 1f, 0.85f);
    static readonly Color DisabledSpawnColor = new(0.45f, 0.45f, 0.5f, 0.5f);

    // Player start markers read GREEN (vs the NPC spawn blue) so the two spawn kinds are told apart at a glance.
    // A disabled player spawn reuses the SAME grey as a disabled NPC spawn (one "off" colour for every marker).
    static readonly Color EnabledPlayerSpawnColor = new(0.3f, 0.85f, 0.35f, 0.85f);

    readonly Scene3D _scene;
    readonly IReadOnlyList<AssetEntry> _entries;
    readonly Dictionary<string, float> _kindHeights;
    readonly Dictionary<string, string> _kindCategories;
    readonly Dictionary<string, IReadOnlyList<MeshHandle>> _propMeshes = new();
    readonly PlacementCache _placements = new();

    Func<string, bool> _scatterLayerVisible = static _ => true;
    Func<bool> _texturedPropsEnabled = static () => true;
    RenderDistanceProfile _renderDistance = RenderDistanceProfile.Default;

    bool _built;
    bool _disposed;

    TerrainField? _field;
    MapDocument? _doc;
    Scene3DChunkSink? _sink;
    TerrainStreamer? _streamer;
    Scene3D.SplatMaterialHandle _splatMaterial;

    /// <summary>Reads and parses every manifest in <paramref name="manifestPaths"/> into
    /// <see cref="KindHeights"/> / <see cref="KindCategories"/> and retains the entries for the mesh upload in
    /// <see cref="Build"/>. Does NO GPU work: <paramref name="scene"/> is stored and only dereferenced from
    /// <see cref="Build"/> onward (so a null scene is a valid headless fixture for the parse + guard surface).
    /// Throws <see cref="ArgumentNullException"/> if <paramref name="manifestPaths"/> is null, and
    /// <see cref="InvalidOperationException"/> (via <see cref="AssetManifest.Load"/>) for an unreadable or
    /// malformed manifest.</summary>
    public ViewportWorld(Scene3D scene, IReadOnlyList<string> manifestPaths)
    {
        ArgumentNullException.ThrowIfNull(manifestPaths);
        _scene = scene;

        var entries = new List<AssetEntry>();
        var heights = new Dictionary<string, float>(StringComparer.Ordinal);
        var categories = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in manifestPaths)
        {
            AssetManifest manifest = AssetManifest.Load(path);
            string stem = ManifestStem(path);
            foreach (AssetEntry entry in manifest.Props)
            {
                entries.Add(entry);
                // First-manifest-wins on a duplicate id, matching the mesh tiebreak in LoadKitMeshes (keeps
                // heights/categories/meshes consistent. Heights used to be last-wins, a divergence closed here).
                if (!heights.ContainsKey(entry.Id)) heights[entry.Id] = entry.HeightMeters;
                if (!categories.ContainsKey(entry.Id)) categories[entry.Id] = entry.Category ?? stem;
            }
        }
        _entries = entries;
        _kindHeights = heights;
        _kindCategories = categories;
    }

    /// <summary>Predicate deciding whether a named scatter layer is streamed: the next <see cref="Build"/> /
    /// <see cref="Rebuild"/> skips the prop layers of any layer for which this returns false (its companions go with
    /// it), so hiding a scatter layer strips its props from the streamed world while the terrain streams unchanged.
    /// Defaults to streaming every layer, and the scene points it at its
    /// <see cref="EditorVisibility.GetLayer"/> and calls <see cref="Rebuild"/> when a toggle changes.</summary>
    public Func<string, bool> ScatterLayerVisible
    {
        get => _scatterLayerVisible;
        set => _scatterLayerVisible = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Predicate deciding whether a manifest entry's <see cref="AssetEntry.Textured"/> flag is honoured on
    /// the next <see cref="Build"/> / <see cref="Rebuild"/>: <see cref="LoadKitMeshes"/> reads it once per rebuild
    /// (see <see cref="ResolvePropParts"/>), so an entry loads its textured parts only while this returns true AND
    /// the entry itself declares <see cref="AssetEntry.Textured"/>, otherwise it loads the flattened single-part
    /// form. Defaults to always-on, matching gameplay. The editor scene points it at its
    /// <see cref="MapEditorOptions.TexturedProps"/> option and calls <see cref="Rebuild"/> when the toggle
    /// flips, mirroring <see cref="ScatterLayerVisible"/>.</summary>
    public Func<bool> TexturedPropsEnabled
    {
        get => _texturedPropsEnabled;
        set => _texturedPropsEnabled = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The viewport's render distance as one coherent set: the next <see cref="Build"/> /
    /// <see cref="Rebuild"/> builds its streamer ring from <see cref="RenderDistanceProfile.ToStreamerConfig()"/> and
    /// culls streamed scatter at <see cref="RenderDistanceProfile.PropDrawRadius"/>, and <see cref="Draw"/> sizes the
    /// water plane from <see cref="RenderDistanceProfile.OceanHalfExtent"/>. The editor scene points this at its
    /// <see cref="MapEditorOptions.RenderDistance"/> option and applies the same profile's
    /// <see cref="RenderDistanceProfile.FarClip"/> to the viewport camera, so terrain residency, prop cull, ocean rim
    /// and frustum agree, and re-points it whenever the operator changes the render-distance multiplier in the
    /// settings menu (<see cref="MapEditorScene.ScaledRenderDistance"/>). The setter runs
    /// <see cref="RenderDistanceProfile.Validate"/>, so an incoherent hand-rolled set throws instead of quietly
    /// rendering a void horizon. In practice that only ever fires at editor start, on the head's own option:
    /// <see cref="RenderDistanceProfile.Scaled"/> preserves every rule <see cref="RenderDistanceProfile.Validate"/>
    /// checks, so a live rescale of an already-valid profile cannot produce an incoherent one. Defaults to
    /// <see cref="RenderDistanceProfile.Default"/>.</summary>
    public RenderDistanceProfile RenderDistance
    {
        get => _renderDistance;
        set
        {
            value.Validate(nameof(RenderDistance));
            _renderDistance = value;
        }
    }

    /// <summary>The built terrain field, or null before <see cref="Build"/> (and after <see cref="Dispose"/>).</summary>
    public TerrainField? Field => _field;

    /// <summary>True once <see cref="Build"/> has run and before <see cref="Dispose"/>.</summary>
    public bool IsBuilt => _built;

    /// <summary>Each manifest kit id's declared <see cref="AssetEntry.HeightMeters"/>, the world-space box height
    /// picking multiplies by a placement's scale (feeds <see cref="EditorPicking"/>). First-manifest-wins on a
    /// duplicate id across manifests, matching <see cref="KindCategories"/> and the mesh tiebreak in
    /// <see cref="LoadKitMeshes"/>.</summary>
    public IReadOnlyDictionary<string, float> KindHeights => _kindHeights;

    /// <summary>Each manifest kit id's palette grouping label: <see cref="AssetEntry.Category"/> when the entry
    /// declares one, else the declaring manifest's own file-name stem with any <c>.manifest</c> suffix stripped
    /// (<c>props.manifest.json</c> falls back to <c>"props"</c>). First-manifest-wins on a duplicate id, matching
    /// <see cref="KindHeights"/> and the mesh tiebreak in <see cref="LoadKitMeshes"/>.</summary>
    public IReadOnlyDictionary<string, string> KindCategories => _kindCategories;

    /// <summary>Builds the streamed world from <paramref name="doc"/> for the first time (field, kit meshes,
    /// scatter/companion prop layers, splat material, sink with NO physics, streamer, and a primed ring). Throws
    /// <see cref="ObjectDisposedException"/> after <see cref="Dispose"/> and
    /// <see cref="InvalidOperationException"/> if already built (use <see cref="Rebuild"/>).</summary>
    public void Build(MapDocument doc, MapDocRegistry registry)
    {
        ThrowIfDisposed();
        if (_built) throw new InvalidOperationException("ViewportWorld is already built; call Rebuild to rebuild it.");
        BuildCore(doc, registry);
    }

    /// <summary>Rebuilds the streamed world from <paramref name="doc"/>: tears down the current sink + streamer
    /// (freeing the loaded ring) and reruns the full <see cref="Build"/> construction, honouring the live
    /// <see cref="ScatterLayerVisible"/> filter (a scatter-layer visibility toggle rebuilds here, so hidden layers
    /// drop out of the fresh prop layers). Unlike the old wholesale rebuild, the loaded kit meshes and the splat
    /// material are NOT torn down here: <see cref="LoadKitMeshes"/> skips any id already cached, and the retained
    /// splat material is reused as-is, so a rebuild stops re-decoding every prop glTF from disk. Call
    /// <see cref="InvalidateKitMeshes"/> first when the cached form must change (the textured-props toggle). The
    /// editor calls this when <see cref="EditorDocument.WorldRebuildPending"/> is set (terrain shape or scatter
    /// inputs changed) and, separately, when a scatter-layer visibility toggle flips. Placement/spawn drags never
    /// reach here. Throws <see cref="ObjectDisposedException"/> after <see cref="Dispose"/> and
    /// <see cref="InvalidOperationException"/> if never built.</summary>
    public void Rebuild(MapDocument doc, MapDocRegistry registry)
    {
        ThrowIfDisposed();
        if (!_built) throw new InvalidOperationException("ViewportWorld has not been built; call Build first.");
        TeardownStreaming();
        BuildCore(doc, registry);
    }

    /// <summary>Rebuilds ONLY the loaded chunks overlapping <paramref name="dirty"/> after a localized terrain edit,
    /// instead of the whole streamed world: it swaps in the new field, tells the sink to sample from it, and asks the
    /// streamer to re-mesh just the chunks the dirty rect touches (Task 1's <see cref="TerrainStreamer.Invalidate(RectArea)"/>).
    /// Nothing is torn down (not the sink, streamer, ring, kit meshes, nor the splat material), so this is far cheaper
    /// than <see cref="Rebuild"/>. The placement cache is invalidated so authored placements re-ground-snap to the new
    /// field. The water plane needs nothing here: <see cref="Draw"/> derives it live from the document each frame.
    /// <para>Returns false (a no-op) when the world is not built, so the caller can fall back to a full
    /// <see cref="Rebuild"/> or skip. Throws <see cref="ObjectDisposedException"/> after <see cref="Dispose"/>, like
    /// its siblings.</para>
    /// <para>This path does NOT rebuild the prop LAYERS (the scatter/companion configs are constructed once per
    /// <see cref="Build"/> / <see cref="Rebuild"/>), so it is valid ONLY for an edit that cannot change any
    /// scatter-layer or companion config or any exclusion. That holds this round because only the feature commands
    /// report a bounded <see cref="EditorCommand.DirtyRegion"/>, and a feature edit changes terrain height alone (the
    /// re-meshed chunks re-scatter off the new field automatically). An exclusion or scatter-layer edit reports a null
    /// region and so takes the full rebuild.</para></summary>
    public bool PartialRebuild(MapDocument doc, MapDocRegistry registry, RectArea dirty)
    {
        ThrowIfDisposed();
        if (!_built) return false;
        _field = MapRuntime.BuildField(doc, registry);   // null-checks doc + registry before any state is swapped
        _doc = doc;
        _sink!.UpdateField(_field);        // future chunk builds sample the new field
        _streamer!.Invalidate(dirty);      // re-mesh the loaded chunks the dirty rect overlaps, in place
        _placements.Invalidate();          // authored placements re-ground-snap to the new field on the next Draw
        return true;
    }

    /// <summary>Frees every retained kit mesh (and the cached splat material, if loaded), so the next
    /// <see cref="Build"/> / <see cref="Rebuild"/> reloads them from disk instead of serving the stale cached
    /// form. Needed because <see cref="LoadKitMeshes"/> keys its cache on the manifest entry id alone: it does not
    /// encode which form (textured parts vs. the flattened single part) was loaded, so a toggle that changes
    /// <see cref="TexturedPropsEnabled"/> must invalidate the cache before the next rebuild picks up the new form.
    /// Safe to call at any time except after <see cref="Dispose"/> (including before the first <see cref="Build"/>,
    /// where it is a no-op). Throws <see cref="ObjectDisposedException"/> after <see cref="Dispose"/>.</summary>
    public void InvalidateKitMeshes()
    {
        ThrowIfDisposed();
        TeardownKitMeshes();
    }

    /// <summary>Streams the world around <paramref name="viewPos"/> (loads/unloads/re-LODs within the streamer's
    /// per-frame budget). Throws <see cref="ObjectDisposedException"/> after <see cref="Dispose"/> and
    /// <see cref="InvalidOperationException"/> before <see cref="Build"/>.</summary>
    public void Update(Vector3 viewPos, float dt)
    {
        ThrowIfDisposed();
        ThrowIfNotBuilt();
        _streamer!.Update(viewPos, dt);
    }

    /// <summary>Draws the streamed world plus the authored content, filtered by <paramref name="visibility"/>. The
    /// terrain + streamed props go through the sink (hidden scatter layers already dropped out at
    /// <see cref="Rebuild"/>). Authored placements draw OUTSIDE it (instanced, so a drag never rebuilds a chunk),
    /// skipping any placement the <see cref="VisibilityGroup.Placements"/> group or its per-element hide flag turns
    /// off. The placement whose stable id is <paramref name="selectedPlacementId"/> re-draws once with
    /// <paramref name="highlightTint"/> when it is still visible. NPC spawn markers draw as ground-height billboards
    /// under the <see cref="VisibilityGroup.Spawns"/> group and per-element hide. Player start markers draw the same
    /// way (green when enabled) under the <see cref="VisibilityGroup.PlayerSpawns"/> group and per-element hide. The water plane draws only when
    /// the <see cref="VisibilityGroup.Water"/> group is on. Throws <see cref="ObjectDisposedException"/> after
    /// <see cref="Dispose"/> and <see cref="InvalidOperationException"/> before <see cref="Build"/>.</summary>
    public void Draw(Vector3 viewPos, string? selectedPlacementId, Color highlightTint, EditorVisibility visibility)
    {
        ThrowIfDisposed();
        ThrowIfNotBuilt();
        ArgumentNullException.ThrowIfNull(visibility);

        // One water plane per frame, camera-centred at the live water level so its rim stays outside the frustum
        // (see BuildWaterPlane). Always submitted while the Water group is on, with no "skip when dry" guard: the
        // water pass is depth-tested against the terrain and its shore-fade drives the alpha to zero at the
        // waterline, so a level below all terrain renders nothing at negligible cost (a fixed-budget grid, one
        // draw). Deriving the plane live from the document means a water-level edit shows up without a rebuild. The
        // wholesale rebuild an EditTerrainCommand triggers is for scatter (which skips underwater candidates), not
        // for the surface.
        if (visibility.GetGroup(VisibilityGroup.Water))
            _scene.DrawWater(BuildWaterPlane(viewPos, _doc!.Terrain.WaterLevel, _renderDistance.OceanHalfExtent));

        _sink!.Draw(viewPos);

        IReadOnlyList<EditorPlacement> placements = FilterVisiblePlacements(_placements.Get(_doc!, _field!), visibility);
        (IReadOnlyList<EditorPlacement> unselected, EditorPlacement? selected) = Partition(placements, selectedPlacementId);
        DrawAuthoredPlacements(unselected, viewPos);
        if (selected is EditorPlacement sel) DrawHighlighted(sel, highlightTint);
        DrawSpawnMarkers(visibility);
        DrawPlayerSpawnMarkers(visibility);
    }

    /// <summary>Keeps only the placements <paramref name="visibility"/> shows: the
    /// <see cref="VisibilityGroup.Placements"/> group is on AND the placement is not individually hidden. Pure and
    /// order-preserving, so the draw filter is headless-testable. Returns the input unchanged (a fast path) when
    /// nothing is hidden.</summary>
    internal static IReadOnlyList<EditorPlacement> FilterVisiblePlacements(
        IReadOnlyList<EditorPlacement> placements, EditorVisibility visibility)
    {
        if (!visibility.GetGroup(VisibilityGroup.Placements)) return Array.Empty<EditorPlacement>();
        List<EditorPlacement>? kept = null;
        for (int i = 0; i < placements.Count; i++)
        {
            bool visible = !visibility.IsElementHidden(SelectionKind.Placement, placements[i].Id);
            if (visible) kept?.Add(placements[i]);
            else kept ??= FirstN(placements, i);   // a hidden one: start copying the prefix we skipped over
        }
        return kept ?? placements;
    }

    // The first n elements of a list, used to seed the filtered copy only once the first hidden element is met.
    static List<EditorPlacement> FirstN(IReadOnlyList<EditorPlacement> placements, int n)
    {
        var list = new List<EditorPlacement>(placements.Count);
        for (int i = 0; i < n; i++) list.Add(placements[i]);
        return list;
    }

    /// <summary>Marks the authored-placement cache dirty so the next <see cref="Draw"/> rebuilds it from the
    /// current document. The editor scene wires <see cref="EditorDocument.DocumentChanged"/> to this. Deliberately
    /// unguarded (a safe no-op after <see cref="Dispose"/>) so a late change event during teardown never throws.</summary>
    public void InvalidatePlacements() => _placements.Invalidate();

    /// <summary>Frees the GPU world (loaded ring, sink, splat material, kit meshes). Idempotent. A call before
    /// <see cref="Build"/> is a no-op beyond flipping the disposed flag.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_built)
        {
            TeardownStreaming();
            TeardownKitMeshes();
            _built = false;
        }
    }

    // ---- GPU build / teardown (never exercised headless) -------------------------------------------------

    void BuildCore(MapDocument doc, MapDocRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(registry);

        _doc = doc;
        _field = MapRuntime.BuildField(doc, registry);
        LoadKitMeshes();
        if (!_splatMaterial.IsValid)   // first build, or after InvalidateKitMeshes: load once and retain
            _splatMaterial = _scene.LoadTerrainMaterial(TerrainMaterialPresets.Procedural());

        IReadOnlyList<PropLayer> layers = BuildPropLayers(doc);
        // No physics in the editor: the viewport only renders. Unlike Room3D's ownsMaterial: true, the WORLD
        // (not the sink) owns the splat material here so it survives a Rebuild's TeardownStreaming. It is freed
        // by TeardownKitMeshes instead (see Dispose / InvalidateKitMeshes).
        _sink = new Scene3DChunkSink(_scene, _field, layers, TerrainChunkRegion.DefaultSize,
            material: _splatMaterial, ownsMaterial: false);
        // Synchronous streaming in the editor: the viewport wants blocking, deterministic loads (a mesh edit rebuilds
        // the ring and the result must be on screen immediately), not the game's background-build/apply-budget path.
        // The ring radii come from RenderDistance so the streamed far field reaches past the camera's far clip: the
        // decor chunks between the gameplay ring and DecorRadiusChunks mesh coarse and carry no scatter or physics,
        // so the far horizon costs a few hundred triangles a chunk rather than a gameplay chunk's full build.
        _streamer = new TerrainStreamer(_renderDistance.ToStreamerConfig().Synchronous(), _sink);

        PrimeRing(FocusFor(doc));
        _placements.Invalidate();
        _built = true;
    }

    void LoadKitMeshes()
    {
        bool texturedProps = _texturedPropsEnabled();
        foreach (AssetEntry entry in _entries)
            if (!_propMeshes.ContainsKey(entry.Id))   // first mesh wins on a duplicate id across manifests (no leak)
                _propMeshes[entry.Id] = _scene.LoadPropMeshes(ResolvePropParts(entry, texturedProps));
    }

    /// <summary>Resolves the glTF parts <see cref="LoadKitMeshes"/> would upload for one manifest entry, honouring
    /// <paramref name="texturedProps"/>: the entry loads its textured multi-part form (via
    /// <see cref="PropLoader.LoadPropParts"/>) only when it declares <see cref="AssetEntry.Textured"/> AND
    /// <paramref name="texturedProps"/> is true, otherwise it loads the flattened single-part form (via
    /// <see cref="PropLoader.LoadProp"/>) exactly as an untextured entry would. Delegates to
    /// <see cref="PropLoader.LoadPropAuto"/> so the manifest flag stays the single source of truth for the decision.
    /// Only the CPU-side glTF decode runs here, no GPU upload, so the branch a rebuild takes is
    /// headless-testable without a <see cref="Scene3D"/> device.</summary>
    internal static IReadOnlyList<GltfMeshPart> ResolvePropParts(
        AssetEntry entry, bool texturedProps, PropValidation? validation = null)
    {
        bool effectiveTextured = entry.Textured && texturedProps;
        AssetEntry effective = effectiveTextured == entry.Textured ? entry : WithTextured(entry, effectiveTextured);
        return PropLoader.LoadPropAuto(effective, validation);
    }

    // A copy of entry with only its Textured flag replaced, so ResolvePropParts can force the flattened path for an
    // entry the manifest declares textured when the editor's TexturedPropsEnabled predicate says no, without a
    // PropLoader/AssetEntry change (AssetEntry.Textured has no public setter, so a copy via the full ctor is the
    // only way to override it for this one load).
    static AssetEntry WithTextured(AssetEntry entry, bool textured) =>
        new(entry.Id, entry.File, entry.HeightMeters, entry.Source, entry.License, entry.Collider,
            entry.Surface, entry.Heightmap, entry.CollisionShape, entry.CollisionProxy, textured, entry.Category);

    // Turns the document's scatter + companion layers into the sink's index-aligned PropLayer list: the VISIBLE
    // scatter layers first (recording each name's index), then companions pointing at their host scatter layer. A
    // hidden scatter layer is skipped (its props drop out of the streamed world, terrain unchanged), and its
    // companions go with it (their host is gone, so they cannot ring anything). A document with no visible scatter
    // layers still gets one empty scatter layer so the terrain streams (the sink needs >= 1 layer).
    IReadOnlyList<PropLayer> BuildPropLayers(MapDocument doc)
    {
        var layers = new List<PropLayer>();
        var scatterIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, ScatterConfig> scatters = MapRuntime.BuildScatterConfigs(doc);

        foreach (string name in VisibleScatterLayerNames(doc, _scatterLayerVisible))
        {
            scatterIndex[name] = layers.Count;
            layers.Add(PropLayer.ScatterLayer(scatters[name], _propMeshes, _renderDistance.PropDrawRadius));
        }

        foreach (MapCompanionLayer cl in doc.CompanionLayers)
        {
            if (!scatterIndex.TryGetValue(cl.HostLayer, out int hostIndex))
            {
                // Host absent from the visible set: skip a companion whose host layer merely got hidden (a hidden
                // host suppresses its companions too), but still surface a companion naming a host the document
                // never declares at all (a genuine authoring error, the pre-visibility guard).
                if (DeclaresScatterLayer(doc, cl.HostLayer)) continue;
                throw new MapDocumentException(
                    $"companion layer '{cl.Name}' names unknown host scatter layer '{cl.HostLayer}' in map '{doc.Id}'.");
            }
            CompanionConfig companions = MapRuntime.BuildCompanionConfig(doc, cl.Name);
            layers.Add(PropLayer.CompanionLayer(hostIndex, companions, _propMeshes, CompanionDrawRadius));
        }

        if (layers.Count == 0)
            layers.Add(PropLayer.ScatterLayer(EmptyScatter(), _propMeshes, _renderDistance.PropDrawRadius));
        return layers;
    }

    /// <summary>The document's scatter-layer names, in document order, keeping only those <paramref name="visible"/>
    /// accepts. This is the seam <see cref="BuildPropLayers"/> uses to decide which scatter prop layers a
    /// <see cref="Build"/> / <see cref="Rebuild"/> constructs, so a rebuild with a hidden layer omits it. Pure (no
    /// GPU, no state), so the exclusion is headless-testable without building the world.</summary>
    internal static List<string> VisibleScatterLayerNames(MapDocument doc, Func<string, bool> visible)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(visible);
        var names = new List<string>(doc.ScatterLayers.Count);
        foreach (MapScatterLayer sl in doc.ScatterLayers)
            if (visible(sl.Name)) names.Add(sl.Name);
        return names;
    }

    // Whether the document declares a scatter layer of the given name (so a companion's missing host can be told
    // apart: a hidden-but-declared host is skipped, an undeclared host is an authoring error).
    static bool DeclaresScatterLayer(MapDocument doc, string name)
    {
        foreach (MapScatterLayer sl in doc.ScatterLayers)
            if (string.Equals(sl.Name, name, StringComparison.Ordinal)) return true;
        return false;
    }

    // A scatter config that places nothing (no biome rules), so a scatter-less zone still streams its terrain.
    static ScatterConfig EmptyScatter() => new ScatterConfig
    {
        Biomes = Array.Empty<BiomeScatterRule>(),
        ClearingRadius = 0f,
    };

    // The initial streaming focus: the centre of the authored bounds, so the primed ring covers the zone the
    // editor camera starts on. Update() takes over once the camera moves.
    static Vector3 FocusFor(MapDocument doc)
    {
        MapBounds b = doc.Bounds;
        return new Vector3((b.MinX + b.MaxX) * 0.5f, 0f, (b.MinZ + b.MaxZ) * 0.5f);
    }

    // Prime the FULL initial ring at load time (the loading moment, not a frame, so MaxLoadsPerFrame is irrelevant):
    // load the whole ring around the focus before the first frame, exactly as Room3D primes.
    void PrimeRing(Vector3 focus) => _streamer!.PrimeAround(focus);

    // Teardown order per Room3D.OnExit: the streamer owns the sink, so streamer.Dispose flushes the loaded ring
    // through the sink (freeing every chunk mesh) and disposes the sink. Do NOT dispose the sink separately
    // (double-dispose). The sink is constructed with ownsMaterial: false (the world owns the splat material, see
    // TeardownKitMeshes), so this does NOT free it: a Rebuild calls only this, retaining kit meshes and the splat
    // material. Null the per-build streaming state so a rebuild starts clean.
    void TeardownStreaming()
    {
        _streamer?.Dispose();
        _placements.Invalidate();
        _sink = null;
        _streamer = null;
        _field = null;
        _doc = null;
    }

    // Frees every kit mesh this class uploaded plus the cached splat material (if loaded), and clears both caches
    // so the next BuildCore reloads them. Runs from Dispose (full teardown) and InvalidateKitMeshes (the
    // textured-props toggle needs a fresh load since the mesh cache key is the entry id alone). A Rebuild never
    // calls this, which is exactly what lets kit meshes and the splat material persist across it. A world that is
    // never disposed still leaks nothing: Scene3D.Dispose frees every mesh and splat material it holds directly,
    // independent of which caller "owns" them at this level (see KhaozEngine.MapEdit.Tool/RenderService.cs).
    void TeardownKitMeshes()
    {
        foreach (IReadOnlyList<MeshHandle> parts in _propMeshes.Values)
            foreach (MeshHandle handle in parts) _scene.UnloadMesh(handle);
        _propMeshes.Clear();
        if (_splatMaterial.IsValid)
        {
            _scene.UnloadSplatMaterial(_splatMaterial);
            _splatMaterial = Scene3D.SplatMaterialHandle.Invalid;
        }
    }

    void DrawAuthoredPlacements(IReadOnlyList<EditorPlacement> placements, Vector3 focus)
    {
        // Reuse the instanced prop path. Authored content is never distance-culled, so pass the wide radius.
        var props = new List<PropPlacement>(placements.Count);
        foreach (EditorPlacement ep in placements) props.Add(ep.Prop);
        _scene.DrawProps(props, _propMeshes, focus, AuthoredDrawRadius);
    }

    void DrawHighlighted(EditorPlacement ep, Color tint)
    {
        if (!_propMeshes.TryGetValue(ep.Prop.Id, out IReadOnlyList<MeshHandle>? parts)) return;
        Matrix4x4 world = Matrix4x4.CreateScale(ep.Prop.Scale)
                          * Matrix4x4.CreateRotationY(ep.Prop.Yaw)
                          * Matrix4x4.CreateTranslation(ep.Prop.X, ep.Prop.Y, ep.Prop.Z);
        foreach (MeshHandle mesh in parts) _scene.Draw(mesh, world, tint);
    }

    void DrawSpawnMarkers(EditorVisibility visibility)
    {
        if (!visibility.GetGroup(VisibilityGroup.Spawns)) return;   // whole group hidden: no markers
        foreach (MapSpawn spawn in _doc!.Spawns)
        {
            if (visibility.IsElementHidden(SelectionKind.Spawn, spawn.Id)) continue;   // this one individually hidden
            float groundY = _field!.SampleHeight(spawn.X, spawn.Z);
            Color color = spawn.Enabled ? EnabledSpawnColor : DisabledSpawnColor;
            _scene.DrawBillboard(new Vector3(spawn.X, groundY + SpawnMarkerLift, spawn.Z), SpawnMarkerSize, color);
        }
    }

    // Player start markers, drawn exactly like the NPC spawn markers (ground-height billboards, same size and lift)
    // but GREEN when enabled and the shared grey when disabled. Gated by the PlayerSpawns group first (mirroring
    // DrawSpawnMarkers) then the per-element hide, matching EditorVisibility.IsElementVisible (which maps
    // SelectionKind.PlayerSpawn to VisibilityGroup.PlayerSpawns), so draw and pick stay in lockstep.
    void DrawPlayerSpawnMarkers(EditorVisibility visibility)
    {
        if (!visibility.GetGroup(VisibilityGroup.PlayerSpawns)) return;   // whole group hidden: no markers
        foreach (MapPlayerSpawn spawn in _doc!.PlayerSpawns)
        {
            if (visibility.IsElementHidden(SelectionKind.PlayerSpawn, spawn.Id)) continue;   // this one individually hidden
            float groundY = _field!.SampleHeight(spawn.X, spawn.Z);
            Color color = spawn.Enabled ? EnabledPlayerSpawnColor : DisabledSpawnColor;
            _scene.DrawBillboard(new Vector3(spawn.X, groundY + SpawnMarkerLift, spawn.Z), SpawnMarkerSize, color);
        }
    }

    // ---- headless surface -------------------------------------------------------------------------------

    /// <summary>Derives the editor's single water plane: a square footprint of <paramref name="halfExtent"/> either
    /// side, centred on <paramref name="viewPos"/> in XZ at surface height <paramref name="level"/>. Pure (no GPU,
    /// no state) so the derivation is headless-testable. <see cref="Draw"/> submits the result via
    /// <see cref="Scene3D.DrawWater(in WaterPlane)"/> every frame, which is what re-centres it as the camera moves.
    /// <para>It follows the CAMERA rather than spanning the document, which is what keeps the rim out of shot. A
    /// document-sized plane on a map smaller than the far clip puts its own edge inside the frustum, so the sea
    /// reads as a rectangular slab with a visible lip. Sized from
    /// <see cref="RenderDistanceProfile.OceanHalfExtent"/> instead, the rim always sits past the far clip (and still
    /// inside the streamed terrain far field), so the water runs to the horizon at any camera position and on any
    /// document size. The plane is a fixed vertex budget however large it is (see <see cref="WaterPlane"/>), so the
    /// wider footprint costs nothing per frame.</para></summary>
    internal static WaterPlane BuildWaterPlane(Vector3 viewPos, float level, float halfExtent) =>
        new(viewPos.X, level, viewPos.Z, halfExtent, halfExtent);

    // The fallback category label for an entry with no declared AssetEntry.Category: the manifest's own file
    // name minus its extension, minus a trailing ".manifest" suffix if present, so "props.manifest.json" and
    // "props.json" both fall back to "props".
    static string ManifestStem(string path)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        const string manifestSuffix = ".manifest";
        return stem.EndsWith(manifestSuffix, StringComparison.OrdinalIgnoreCase)
            ? stem[..^manifestSuffix.Length]
            : stem;
    }

    /// <summary>Splits <paramref name="placements"/> into the unselected list plus the single placement whose
    /// stable id equals <paramref name="selectedId"/> (or null when the id is null or unmatched). Total and
    /// order-preserving: every input lands in exactly one output, and only the FIRST id match is the selected one
    /// (ids are unique in a real document, but the guard keeps the split total for a duplicate). Pure, so the
    /// tests exercise the draw partition without a device.</summary>
    internal static (IReadOnlyList<EditorPlacement> Unselected, EditorPlacement? Selected) Partition(
        IReadOnlyList<EditorPlacement> placements, string? selectedId)
    {
        var unselected = new List<EditorPlacement>(placements.Count);
        EditorPlacement? selected = null;
        for (int i = 0; i < placements.Count; i++)
        {
            EditorPlacement ep = placements[i];
            if (selected is null && selectedId is not null && string.Equals(ep.Id, selectedId, StringComparison.Ordinal))
                selected = ep;
            else
                unselected.Add(ep);
        }
        return (unselected, selected);
    }

    void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ViewportWorld));
    }

    void ThrowIfNotBuilt()
    {
        if (!_built) throw new InvalidOperationException("ViewportWorld has not been built; call Build first.");
    }
}

/// <summary>An authored placement paired with its stable document id.
/// <see cref="MapRuntime.BuildPlacements(MapDocument, TerrainField)"/>
/// yields <see cref="PropPlacement"/>s keyed only by kit id (what the renderer instances), so the editor keeps the
/// document id alongside to match the selection. <see cref="Id"/> is the <see cref="MapPlacement.Id"/> and
/// <see cref="Prop"/> the built render placement.</summary>
internal readonly record struct EditorPlacement(string Id, PropPlacement Prop);

/// <summary>Caches the authored placements as index-aligned <see cref="EditorPlacement"/>s and rebuilds them lazily
/// after an <see cref="Invalidate"/>. The editor scene invalidates it on
/// <see cref="EditorDocument.DocumentChanged"/>, so <see cref="ViewportWorld.Draw"/> rebuilds the list only when
/// the document actually changed, not every frame. GPU-free (it only reads the document + field), so the
/// invalidation semantics are headless-testable.</summary>
internal sealed class PlacementCache
{
    List<EditorPlacement>? _cached;

    /// <summary>True when the next <see cref="Get"/> will rebuild.</summary>
    public bool IsDirty => _cached is null;

    /// <summary>Drops the cached list so the next <see cref="Get"/> rebuilds from the document.</summary>
    public void Invalidate() => _cached = null;

    /// <summary>The cached authored placements, rebuilt from <paramref name="doc"/> + <paramref name="field"/> when
    /// dirty. The same list instance is returned until the next <see cref="Invalidate"/>.</summary>
    public IReadOnlyList<EditorPlacement> Get(MapDocument doc, TerrainField field) => _cached ??= Build(doc, field);

    // Pairs each built PropPlacement with its authored stable id. MapRuntime.BuildPlacements emits one placement
    // per MapPlacement in document order, so index i aligns with doc.Placements[i].
    internal static List<EditorPlacement> Build(MapDocument doc, TerrainField field)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(field);
        IReadOnlyList<PropPlacement> props = MapRuntime.BuildPlacements(doc, field);
        var list = new List<EditorPlacement>(props.Count);
        for (int i = 0; i < props.Count; i++)
            list.Add(new EditorPlacement(doc.Placements[i].Id, props[i]));
        return list;
    }
}
