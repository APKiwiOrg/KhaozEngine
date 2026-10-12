using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>The immutable effective resource snapshot shared by support and geometry consumers.</summary>
public sealed class MapEffectiveResources
{
    readonly MapEffectiveContext _context;
    readonly MapWaterSet? _water;
    readonly MapBuildingIntent _intent;
    public IReadOnlyList<MapResolvedPlacement> Placements { get; }
    public IReadOnlyList<MapPlacementSupport> PlacementSupports { get; }
    public MapSurfaceSet Surfaces => _context.Surfaces.Clone();
    /// <summary>Mathematical poses keyed by Kind:world-ID. Backend presentation is a separate approximation.</summary>
    public IReadOnlyDictionary<string, MapEffectiveTransform> Transforms => _context.Transforms;
    public MapWaterSet? Water => _water?.Clone();
    public MapBuildingIntent BuildingIntent => _intent.Clone();
    public string Digest => _context.Digest;
    public IMapSurfaceSource SurfaceSource { get; }
    internal IReadOnlyDictionary<string, MapExactPoint> LightPoints { get; }
    internal IReadOnlyDictionary<string, IReadOnlyList<MapExactPoint>> WaterCorners { get; }
    internal IReadOnlyList<MapPrefabAttachmentTransfer> Attachments { get; }

    internal MapEffectiveResources(MapEffectiveContext context, IMapSurfaceSource source,
        IReadOnlyList<MapResolvedPlacement> placements, IReadOnlyList<MapPlacementSupport> supports,
        MapWaterSet? water, MapBuildingIntent intent, IReadOnlyDictionary<string, MapExactPoint> lights,
        IReadOnlyDictionary<string, IReadOnlyList<MapExactPoint>> waterCorners, IReadOnlyList<MapPrefabAttachmentTransfer> attachments)
    {
        _context = context;
        SurfaceSource = source;
        Placements = Array.AsReadOnly(placements.ToArray());
        PlacementSupports = Array.AsReadOnly(supports.ToArray());
        _water = water?.Clone();
        _intent = intent.Clone();
        LightPoints = new ReadOnlyDictionary<string, MapExactPoint>(new Dictionary<string, MapExactPoint>(lights, StringComparer.Ordinal));
        WaterCorners = new ReadOnlyDictionary<string, IReadOnlyList<MapExactPoint>>(waterCorners.ToDictionary(p => p.Key,
            p => (IReadOnlyList<MapExactPoint>)Array.AsReadOnly(p.Value.ToArray()), StringComparer.Ordinal));
        Attachments = Array.AsReadOnly(attachments.ToArray());
    }
    public MapScopedSurfaces CompleteView() => SurfaceSource is MapComposedSurfaceSource
        ? Acquire(MapScopedSurfaces.CompleteEffective(_context).Scope) : MapScopedSurfaces.CompleteEffective(_context);
    public MapScopedSurfaces Acquire(MapSurfaceScope scope) => MapScopedSurfaces.Acquire(SurfaceSource, scope, new[] { Digest }, 2, 2);
}

/// <summary>Owned local descriptors and unfrozen poses. Acquired views retain this captured context.</summary>
internal sealed class MapEffectiveContext
{
    readonly IReadOnlyDictionary<string, MapSurfaceRef> _surfaces;
    internal MapSurfaceSet Surfaces { get; }
    internal IReadOnlyDictionary<string, MapEffectiveTransform> Transforms { get; }
    internal string Digest { get; }
    internal IReadOnlyList<MapStoreyDoc> Storeys { get; }
    internal MapEffectiveContext(MapSurfaceSet surfaces, IReadOnlyDictionary<string, MapEffectiveTransform> transforms, string digest, IReadOnlyList<MapStoreyDoc>? storeys = null)
    {
        Storeys = Array.AsReadOnly((storeys ?? Array.Empty<MapStoreyDoc>()).ToArray());
        Surfaces = surfaces.Clone();
        _surfaces = new ReadOnlyDictionary<string, MapSurfaceRef>(Surfaces.Refs.ToDictionary(s => s.Id, StringComparer.Ordinal));
        Transforms = new ReadOnlyDictionary<string, MapEffectiveTransform>(new Dictionary<string, MapEffectiveTransform>(transforms, StringComparer.Ordinal));
        Digest = digest;
    }
    internal MapSurfaceRef Surface(string id) => _surfaces.TryGetValue(id, out var surface) ? surface : throw new MapDocumentException($"Missing effective surface '{id}'.");
    internal MapExactXz Xz(string id, MapExactXz local) => Transform(MapPrefabChildKind.Surface, id)?.Xz(local) ?? local;
    internal static string Key(MapPrefabChildKind kind, string id) => kind + ":" + id;
    internal MapResourceTransform? Transform(MapPrefabChildKind kind, string id) =>
        Transforms.TryGetValue(Key(kind, id), out var transform) ? new(transform) : null;
    internal MapExactPoint Point(MapPrefabChildKind kind, string id, MapExactPoint local) => Transform(kind, id)?.Point(local) ?? local;
}
