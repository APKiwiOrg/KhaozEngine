using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

/// <summary>Knobs for <see cref="TileWorldView"/>.</summary>
public sealed class TileWorldViewOptions
{
    /// <summary>Horizontal radius in metres around the draw focus inside which a prop is drawn.</summary>
    public float PropDrawRadius { get; set; } = 96f;

    /// <summary>The settings every region-plane ground mesh is built with.</summary>
    public TileGroundMesherOptions Mesher { get; set; } = new();

    /// <summary>Where the view reports an archetype with no mesh, once per archetype. Null discards the line.</summary>
    public Action<string>? Log { get; set; }
}

/// <summary>Owns one tile world's meshes and props inside a scene: one ground mesh handle per drawable
/// region-plane, one prop mesh set per catalog archetype, per-region prop placements, and the roof rule that
/// hides the planes above an indoor observer. Edits are announced with <see cref="MarkDirty(RegionCoord, int)"/>
/// and coalesced into one rebuild per region-plane at the start of the next <see cref="Draw"/>, so a stroke that
/// touches the same tiles a hundred times still remeshes each region-plane once. Everything goes through
/// <see cref="ITileWorldScene"/>, so the whole class runs headless.</summary>
public sealed class TileWorldView : IDisposable
{
    /// <summary>Side in metres of the box an archetype with no mesh is drawn as.</summary>
    public const float PlaceholderSize = 1f;

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
    readonly HashSet<(RegionCoord Region, int Plane)> _dirty = new();
    readonly int _planes;
    TileCoord _observer;
    bool _disposed;

    /// <summary>Binds a world to a scene and uploads one mesh set per catalog archetype up front, so a region
    /// load is placements alone. An archetype the resolver has no mesh for gets the placeholder box and one log
    /// line, because a half-authored catalog has to keep rendering.</summary>
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

        Observer = default;
    }

    /// <summary>The tile the roof rule is judged from. Setting it recomputes <see cref="ObserverIndoors"/>.</summary>
    public TileCoord Observer
    {
        get => _observer;
        set
        {
            _observer = value;
            ObserverIndoors = (_doc.GetSettings(value.X, value.Z, value.Plane) & TileSettings.Indoors) != 0;
        }
    }

    /// <summary>Whether the observer's tile is flagged indoors, which hides the roofs on the planes above it.</summary>
    public bool ObserverIndoors { get; private set; }

    /// <summary>How many prop placements the last <see cref="Draw"/> queued, roofs included when shown.</summary>
    public int LastDrawnProps { get; private set; }

    /// <summary>How many regions are loaded right now.</summary>
    public int LoadedRegionCount => _loaded.Count;

    /// <summary>The loaded regions, in no particular order.</summary>
    public IReadOnlyCollection<RegionCoord> LoadedRegions => _loaded.Keys;

    /// <summary>Builds and uploads every plane of one region. Loading a region that is already loaded rebuilds it
    /// from the document rather than doubling up, so a caller may use this as a whole-region refresh.</summary>
    public void LoadRegion(RegionCoord region)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UnloadRegion(region);

        var meshes = new MeshHandle?[_planes];
        var props = new TileRegionProps[_planes];
        for (int plane = 0; plane < _planes; plane++)
        {
            meshes[plane] = BuildMesh(region, plane);
            props[plane] = TileObjectProps.Build(_doc, _catalogs, region, plane);
        }
        _loaded[region] = new RegionHandles(meshes, props);
    }

    /// <summary>Frees every mesh handle of one region and forgets it, along with any rebuild it had queued.
    /// Unloading a region that is not loaded does nothing.</summary>
    public void UnloadRegion(RegionCoord region)
    {
        for (int plane = 0; plane < _planes; plane++) _dirty.Remove((region, plane));
        if (!_loaded.Remove(region, out RegionHandles? handles)) return;
        FreeMeshes(handles);
    }

    /// <summary>Queues one region-plane for a rebuild at the next <see cref="Flush"/>. A plane outside the
    /// document is ignored, and a region that is not loaded is dropped when the flush runs.</summary>
    public void MarkDirty(RegionCoord region, int plane)
    {
        if (plane < 0 || plane >= _planes) return;
        _dirty.Add((region, plane));
    }

    /// <summary>Queues every region the world-space tile rect touches on one plane. This is the edit-facing
    /// overload: an editor hands over the tiles it wrote and the view works out which regions that is.</summary>
    public void MarkDirty(TileRect worldRect, int plane)
    {
        if (worldRect.IsEmpty || plane < 0 || plane >= _planes) return;
        RegionCoord min = RegionCoord.Of(worldRect.X, worldRect.Z);
        RegionCoord max = RegionCoord.Of(worldRect.X1 - 1, worldRect.Z1 - 1);
        for (int rz = min.Rz; rz <= max.Rz; rz++)
            for (int rx = min.Rx; rx <= max.Rx; rx++)
                _dirty.Add((new RegionCoord(rx, rz), plane));
    }

    /// <summary>Rebuilds every queued region-plane that is currently loaded and clears the queue. Called at the
    /// start of <see cref="Draw"/>, so an explicit call is only needed to pay the cost off the draw path.</summary>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_dirty.Count == 0) return;

        foreach ((RegionCoord region, int plane) in _dirty)
        {
            if (!_loaded.TryGetValue(region, out RegionHandles? handles)) continue;
            if (handles.Meshes[plane] is { } old) _scene.UnloadMesh(old);
            handles.Meshes[plane] = BuildMesh(region, plane);
            handles.Props[plane] = TileObjectProps.Build(_doc, _catalogs, region, plane);
        }
        _dirty.Clear();
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

    /// <summary>Frees every region and every archetype mesh set this view uploaded. The scene itself is not
    /// owned, so it outlives the view. Safe to call twice.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (RegionHandles handles in _loaded.Values) FreeMeshes(handles);
        _loaded.Clear();
        _dirty.Clear();

        foreach (IReadOnlyList<MeshHandle> parts in _propMeshes.Values) _scene.UnloadPropMeshes(parts);
        _propMeshes.Clear();
    }

    // The roof rule: standing indoors hides the roofs of every plane ABOVE the observer's own, so the storey the
    // observer is on keeps its own props and the ceiling between them and the camera goes.
    bool RoofsHiddenOn(int plane) => ObserverIndoors && plane > _observer.Plane;

    MeshHandle? BuildMesh(RegionCoord region, int plane)
    {
        GltfMesh? mesh = TileGroundMesher.Build(_doc, _catalogs, region, plane, _options.Mesher);
        return mesh is null ? null : _scene.LoadMesh(mesh);
    }

    void FreeMeshes(RegionHandles handles)
    {
        for (int plane = 0; plane < handles.Meshes.Length; plane++)
        {
            if (handles.Meshes[plane] is { } mesh) _scene.UnloadMesh(mesh);
            handles.Meshes[plane] = null;
        }
    }

    // One loaded region: the ground mesh handle of each plane (null where the plane has no drawable tile) and
    // that plane's placements. Dropped whole on unload, so a region never leaves half its handles behind.
    sealed record RegionHandles(MeshHandle?[] Meshes, TileRegionProps[] Props);
}
