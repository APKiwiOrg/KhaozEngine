using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Terrain;

namespace KhaozEngine.TileWorld;

public sealed partial class TileWorldView
{
    static readonly TileWorldBuildKey InteriorFieldKey = new(default, -1, TileWorldBuildKind.InteriorCut);
    readonly TileWorldBuildQueue<TileInteriorBuildInput, TileInteriorBuildOutput> _interiorBuilds;
    readonly TileInteriorRoofSamples _interiorSamples = new();
    readonly Dictionary<(RegionCoord Region, int Plane), long> _interiorDigests = new();
    readonly HashSet<string> _reportedInteriorIssues = new(StringComparer.Ordinal);
    readonly ITileMeshResolver _propLayerResolver;
    readonly TileWorldBuildQueueOptions? _propLayerQueueOptions;
    readonly IChunkBuildDispatcher? _propLayerDispatcher;
    TilePropLayerDefinition[] _propLayerInputs = [];
    FrozenSet<string>[] _propLayerArchetypes = [];
    FrozenSet<string> _cutArchetypeIds = Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
    ITileMeshGeometry? _interiorMeshes;
    InteriorWindow? _requestedInteriorWindow;
    long _interiorGeneration;
    bool _interiorInputsDirty = true;
    bool _interiorWindowLogged;

    /// <summary>Dedicated geometry reader for this serialized driver. It must not be shared with other jobs or
    /// the view thread, because readers such as GltfTileMeshGeometry are single-threaded.</summary>
    public ITileMeshGeometry? InteriorMeshes
    {
        get => _interiorMeshes;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (ReferenceEquals(value, _interiorMeshes)) return;
            _interiorMeshes = value;
            ResetInteriorCut();
        }
    }

    /// <summary>The exact field published to the scene, also used by cut-aware picks.</summary>
    public InteriorHeightField? AppliedInteriorCut { get; private set; }

    internal IReadOnlySet<string> CutArchetypeIds => _cutArchetypeIds;

    void RememberInteriorLayers(TileWorldPropClusters.ValidatedLayers validated)
    {
        _propLayerInputs = new TilePropLayerDefinition[validated.Layers.Length];
        _propLayerArchetypes = new FrozenSet<string>[validated.Layers.Length];
        var cut = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < validated.Layers.Length; i++)
        {
            TileWorldPropClusters.ValidatedLayer layer = validated.Layers[i];
            _propLayerInputs[i] = layer.Definition;
            _propLayerArchetypes[i] = layer.ArchetypeIds.ToFrozenSet(StringComparer.Ordinal);
            if (layer.Definition.CutInsideInteriors) cut.UnionWith(layer.ArchetypeIds);
        }
        _cutArchetypeIds = cut.ToFrozenSet(StringComparer.Ordinal);
    }

    void RefreshInteriorLayers()
    {
        IReadOnlyList<TilePropLayerDefinition> current = _options.PropLayers;
        ArgumentNullException.ThrowIfNull(current);
        bool same = current.Count == _propLayerInputs.Length;
        for (int i = 0; same && i < current.Count; i++)
            same = current[i] == _propLayerInputs[i] &&
                TilePropLayerMembership.Matches(_propLayerArchetypes[i], current[i].ArchetypeIds);
        if (same) return;
        TileWorldPropClusters.ValidatedLayers validated = TileWorldPropClusters.Validate(_catalogs, current);
        // Build the replacement before retiring the live resources, so a resolver failure keeps them intact.
        var replacement = new TileWorldPropClusters(_scene, _catalogs, _propLayerResolver, _propMeshes,
            validated, _doc.TileSize, _propLayerQueueOptions, _propLayerDispatcher, _cameraFades);
        ResetInteriorCut();
        _propClusters.Dispose();
        _propClusters = replacement;
        RememberInteriorLayers(validated);
        foreach (KeyValuePair<RegionCoord, RegionHandles> entry in _loaded)
        {
            RegionHandles handles = entry.Value;
            for (int plane = 0; plane < _planes; plane++)
                handles.Props[plane] = _propClusters.Build(_doc, entry.Key, plane, OverrideLookup());
            if (handles.Residency == TileRegionResidencyState.Gameplay) RequestPropClusters(handles.Props);
            ReleaseAnimatedFoliage(handles);
        }
    }

    void PumpInteriorCut(Vector3 focus)
    {
        if (_interiorMeshes is null || _cutArchetypeIds.Count == 0)
        {
            if (_requestedInteriorWindow.HasValue || AppliedInteriorCut is not null) ResetInteriorCut();
            // Canceled workers still finish off-thread. Drop their completions before another source starts.
            _interiorBuilds.Pump(default);
            return;
        }
        float cellSize = TileWorldInteriors.CellSizeFor(_doc.TileSize);
        int cells = InteriorWindow.CellsFor(512f, cellSize);
        if (cells == InteriorWindow.MaxCells && cellSize * cells < 512f && !_interiorWindowLogged)
        {
            _interiorWindowLogged = true;
            _options.Log?.Invoke(string.Create(CultureInfo.InvariantCulture,
                $"interior.window: {cells} cells reduce the interior window to {cellSize * cells:0.###} m."));
        }
        var horizontalFocus = new Vector2(focus.X, focus.Z);
        bool recentre = _requestedInteriorWindow is not { } window || window.CellSize != cellSize ||
            window.Cells != cells || window.NeedsRecentre(horizontalFocus, 64f);
        bool changed = _interiorInputsDirty && RefreshInteriorDigests();
        _interiorInputsDirty = false;
        if (recentre || changed)
        {
            InteriorWindow next = recentre
                ? InteriorWindow.Centred(horizontalFocus, cellSize, cells) : _requestedInteriorWindow!.Value;
            TileWorldDocument detached = TileWorldBuildSnapshot.CaptureInteriors(_doc, next);
            TileWorldCatalogs catalogs = TileWorldBuildSnapshot.CaptureInteriorCatalogs(_catalogs);
            _requestedInteriorWindow = next;
            _interiorBuilds.Request(new TileWorldBuildRequest<TileInteriorBuildInput>(InteriorFieldKey,
                ++_interiorGeneration, new TileInteriorBuildInput(detached, catalogs, _interiorMeshes, next,
                    _interiorSamples)));
        }
        // Request before pumping, so even a completed old generation cannot publish after an edit or re-centre.
        _interiorBuilds.Pump(default);
    }

    bool RefreshInteriorDigests()
    {
        bool changed = false;
        var next = new Dictionary<(RegionCoord Region, int Plane), long>();
        foreach (RegionCoord region in _doc.Regions.Keys)
            for (int plane = 0; plane < _doc.PlaneCount; plane++)
            {
                long digest = TileInteriorInputs.Digest(_doc, _catalogs, region, plane);
                var key = (region, plane);
                next.Add(key, digest);
                if (!_interiorDigests.TryGetValue(key, out long previous) || previous != digest) changed = true;
            }
        if (next.Count != _interiorDigests.Count) changed = true;
        _interiorDigests.Clear();
        foreach (var entry in next) _interiorDigests.Add(entry.Key, entry.Value);
        return changed;
    }

    TileInteriorBuildOutput BuildInteriorCpu(TileWorldBuildRequest<TileInteriorBuildInput> request)
    {
        _onCpuBuild?.Invoke(request.Key);
        TileInteriorBuildInput input = request.Input;
        var source = new TileWorldInteriors(input.Document, input.Catalogs, input.Meshes, input.Samples);
        InteriorSourceResult collected = source.Collect(input.Window);
        return new TileInteriorBuildOutput(
            InteriorHeightFieldBuilder.Build(collected.Volumes, input.Window, collected.Encoding), collected.Issues);
    }

    void ApplyInteriorBuild(TileWorldBuildResult<TileInteriorBuildOutput> result)
    {
        if (_disposed || _interiorMeshes is null || _cutArchetypeIds.Count == 0 ||
            result.Generation != _interiorGeneration) return;
        PublishInteriorCut(result.Payload.Field);
        foreach (string issue in result.Payload.Issues)
            if (_reportedInteriorIssues.Add(issue)) _options.Log?.Invoke(issue);
    }

    void PublishInteriorCut(InteriorHeightField? field)
    {
        AppliedInteriorCut = field;
        _scene.SetInteriorCut(field);
    }

    void ResetInteriorCut()
    {
        _interiorGeneration++;
        _interiorBuilds.Cancel(InteriorFieldKey);
        _requestedInteriorWindow = null;
        _interiorInputsDirty = true;
        _interiorDigests.Clear();
        if (AppliedInteriorCut is not null) PublishInteriorCut(null);
    }

    void DisposeInteriorCut()
    {
        ResetInteriorCut();
        _interiorMeshes = null;
        _interiorBuilds.Dispose();
    }
}
