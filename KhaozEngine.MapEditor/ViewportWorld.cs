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
/// asset manifests, primes the ring, and <b>REBUILDS WHOLESALE</b> on <see cref="Rebuild"/> (unload old, dispose
/// sink, fresh construction: the engine has no partial chunk invalidation and the editor zone is bounded).
/// Authored placements and spawn markers draw OUTSIDE the sink so transform drags never trigger a rebuild, and
/// the selected placement re-draws with the highlight tint through the per-call tint surface.
/// <para>The class is split so the GPU-free surface (manifest parsing into <see cref="KindHeights"/>, the
/// Build/Rebuild/Dispose state guards, the placement cache, and the selected/unselected <see cref="Partition"/>)
/// is testable without a device; every GPU-touching call lives behind a small private method. It touches its
/// <see cref="Scene3D"/> only from <see cref="Build"/> onward, so the ctor and the state guards run headless.</para>
/// </summary>
public sealed class ViewportWorld : IDisposable
{
    /// <summary>Horizontal cull radius (m) for streamed scatter props, matching the Room3D showcase.</summary>
    const float PropDrawRadius = 90f;

    /// <summary>Horizontal cull radius (m) for streamed companion foliage (a short radius keeps a dense layer
    /// affordable, per the multi-layer sink design).</summary>
    const float CompanionDrawRadius = 60f;

    /// <summary>Authored placements are the content being edited, so they are effectively never distance-culled
    /// (a very wide draw ring). Streamed scatter still uses <see cref="PropDrawRadius"/>.</summary>
    const float AuthoredDrawRadius = 100_000f;

    /// <summary>Spawn-marker billboard half-size (the disc spans twice this) and its lift above the ground so it
    /// reads as a floating pin rather than z-fighting the terrain.</summary>
    const float SpawnMarkerSize = 0.6f;
    const float SpawnMarkerLift = 1.0f;

    static readonly Color EnabledSpawnColor = new(0.25f, 0.7f, 1f, 0.85f);
    static readonly Color DisabledSpawnColor = new(0.45f, 0.45f, 0.5f, 0.5f);

    readonly Scene3D _scene;
    readonly IReadOnlyList<AssetEntry> _entries;
    readonly Dictionary<string, float> _kindHeights;
    readonly Dictionary<string, string> _kindCategories;
    readonly Dictionary<string, MeshHandle> _propMeshes = new();
    readonly PlacementCache _placements = new();

    bool _built;
    bool _disposed;

    TerrainField? _field;
    MapDocument? _doc;
    Scene3DChunkSink? _sink;
    TerrainStreamer? _streamer;

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
                // heights/categories/meshes consistent; heights used to be last-wins, a divergence closed here).
                if (!heights.ContainsKey(entry.Id)) heights[entry.Id] = entry.HeightMeters;
                if (!categories.ContainsKey(entry.Id)) categories[entry.Id] = entry.Category ?? stem;
            }
        }
        _entries = entries;
        _kindHeights = heights;
        _kindCategories = categories;
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

    /// <summary>Rebuilds the streamed world wholesale from <paramref name="doc"/>: tears down the current sink +
    /// streamer + kit meshes (freeing the loaded ring), then reruns the full <see cref="Build"/> construction. The
    /// editor calls this when <see cref="EditorDocument.WorldRebuildPending"/> is set (terrain shape or scatter
    /// inputs changed); placement/spawn drags never reach here. Throws <see cref="ObjectDisposedException"/> after
    /// <see cref="Dispose"/> and <see cref="InvalidOperationException"/> if never built.</summary>
    public void Rebuild(MapDocument doc, MapDocRegistry registry)
    {
        ThrowIfDisposed();
        if (!_built) throw new InvalidOperationException("ViewportWorld has not been built; call Build first.");
        TeardownGpu();
        BuildCore(doc, registry);
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

    /// <summary>Draws the streamed world plus the authored content. The terrain + streamed props go through the
    /// sink; authored placements draw OUTSIDE it (instanced, so a drag never rebuilds a chunk); the placement whose
    /// stable id is <paramref name="selectedPlacementId"/> re-draws once with <paramref name="highlightTint"/> via
    /// the per-call tint; spawn markers draw as ground-height billboards. Throws
    /// <see cref="ObjectDisposedException"/> after <see cref="Dispose"/> and <see cref="InvalidOperationException"/>
    /// before <see cref="Build"/>.</summary>
    public void Draw(Vector3 viewPos, string? selectedPlacementId, Color highlightTint)
    {
        ThrowIfDisposed();
        ThrowIfNotBuilt();

        _sink!.Draw(viewPos);

        IReadOnlyList<EditorPlacement> placements = _placements.Get(_doc!, _field!);
        (IReadOnlyList<EditorPlacement> unselected, EditorPlacement? selected) = Partition(placements, selectedPlacementId);
        DrawAuthoredPlacements(unselected, viewPos);
        if (selected is EditorPlacement sel) DrawHighlighted(sel, highlightTint);
        DrawSpawnMarkers();
    }

    /// <summary>Marks the authored-placement cache dirty so the next <see cref="Draw"/> rebuilds it from the
    /// current document. The editor scene wires <see cref="EditorDocument.DocumentChanged"/> to this. Deliberately
    /// unguarded (a safe no-op after <see cref="Dispose"/>) so a late change event during teardown never throws.</summary>
    public void InvalidatePlacements() => _placements.Invalidate();

    /// <summary>Frees the GPU world (loaded ring, sink, splat material, kit meshes). Idempotent; a call before
    /// <see cref="Build"/> is a no-op beyond flipping the disposed flag.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_built)
        {
            TeardownGpu();
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

        IReadOnlyList<PropLayer> layers = BuildPropLayers(doc);
        Scene3D.SplatMaterialHandle material = _scene.LoadTerrainMaterial(TerrainMaterialPresets.Procedural());
        // No physics in the editor: the viewport only renders, and the sink owns the splat material so its
        // Dispose (via the streamer) frees it, matching Room3D's ownsMaterial: true teardown.
        _sink = new Scene3DChunkSink(_scene, _field, layers, TerrainChunkRegion.DefaultSize,
            material: material, ownsMaterial: true);
        _streamer = new TerrainStreamer(StreamerConfig.Default, _sink);

        PrimeRing(FocusFor(doc));
        _placements.Invalidate();
        _built = true;
    }

    void LoadKitMeshes()
    {
        foreach (AssetEntry entry in _entries)
            if (!_propMeshes.ContainsKey(entry.Id))   // first mesh wins on a duplicate id across manifests (no leak)
                _propMeshes[entry.Id] = _scene.LoadMesh(PropLoader.LoadProp(entry));
    }

    // Turns the document's scatter + companion layers into the sink's index-aligned PropLayer list: scatter
    // layers first (recording each name's index), then companions pointing at their host scatter layer. A document
    // with no scatter layers still gets one empty scatter layer so the terrain streams (the sink needs >= 1 layer).
    IReadOnlyList<PropLayer> BuildPropLayers(MapDocument doc)
    {
        var layers = new List<PropLayer>();
        var scatterIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, ScatterConfig> scatters = MapRuntime.BuildScatterConfigs(doc);

        foreach (MapScatterLayer sl in doc.ScatterLayers)
        {
            scatterIndex[sl.Name] = layers.Count;
            layers.Add(PropLayer.ScatterLayer(scatters[sl.Name], _propMeshes, PropDrawRadius));
        }

        foreach (MapCompanionLayer cl in doc.CompanionLayers)
        {
            if (!scatterIndex.TryGetValue(cl.HostLayer, out int hostIndex))
                throw new MapDocumentException(
                    $"companion layer '{cl.Name}' names unknown host scatter layer '{cl.HostLayer}' in map '{doc.Id}'.");
            CompanionConfig companions = MapRuntime.BuildCompanionConfig(doc, cl.Name);
            layers.Add(PropLayer.CompanionLayer(hostIndex, companions, _propMeshes, CompanionDrawRadius));
        }

        if (layers.Count == 0)
            layers.Add(PropLayer.ScatterLayer(EmptyScatter(), _propMeshes, PropDrawRadius));
        return layers;
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
    // pump the streamer until the loaded set stops growing, exactly as Room3D primes.
    void PrimeRing(Vector3 focus)
    {
        int loadedBefore = -1;
        while (_streamer!.Loaded.Count != loadedBefore)
        {
            loadedBefore = _streamer.Loaded.Count;
            _streamer.Update(focus, 0f);
        }
    }

    // Teardown order per Room3D.OnExit: the streamer owns the sink, so streamer.Dispose flushes the loaded ring
    // through the sink (freeing every chunk mesh) and disposes the sink (which frees the owned splat material). Do
    // NOT dispose the sink separately (double-dispose). Then free the kit meshes this class uploaded, and null the
    // per-build state so a rebuild starts clean.
    void TeardownGpu()
    {
        _streamer?.Dispose();
        foreach (MeshHandle handle in _propMeshes.Values) _scene.UnloadMesh(handle);
        _propMeshes.Clear();
        _placements.Invalidate();
        _sink = null;
        _streamer = null;
        _field = null;
        _doc = null;
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
        if (!_propMeshes.TryGetValue(ep.Prop.Id, out MeshHandle mesh)) return;
        Matrix4x4 world = Matrix4x4.CreateScale(ep.Prop.Scale)
                          * Matrix4x4.CreateRotationY(ep.Prop.Yaw)
                          * Matrix4x4.CreateTranslation(ep.Prop.X, ep.Prop.Y, ep.Prop.Z);
        _scene.Draw(mesh, world, tint);
    }

    void DrawSpawnMarkers()
    {
        foreach (MapSpawn spawn in _doc!.Spawns)
        {
            float groundY = _field!.SampleHeight(spawn.X, spawn.Z);
            Color color = spawn.Enabled ? EnabledSpawnColor : DisabledSpawnColor;
            _scene.DrawBillboard(new Vector3(spawn.X, groundY + SpawnMarkerLift, spawn.Z), SpawnMarkerSize, color);
        }
    }

    // ---- headless surface -------------------------------------------------------------------------------

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

/// <summary>An authored placement paired with its stable document id. <see cref="MapRuntime.BuildPlacements"/>
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
