using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Spaces;
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
    /// <summary>Render-only partitions compiled once per affected physical target at strict expansion.</summary>
    public IReadOnlyDictionary<string, MapPaintRegionSet> PaintRegions { get; }
    internal IReadOnlyDictionary<string, MapExactPoint> LightPoints { get; }
    internal IReadOnlyList<MapEffectiveLight> EffectiveLights { get; }
    internal IReadOnlyList<MapEffectiveMarker> EffectiveMarkers { get; }
    /// <summary>Versioned roof, light and marker intent, empty without annotations.</summary>
    public string AnnotationDigest { get; }
    internal IReadOnlyDictionary<string, IReadOnlyList<MapExactPoint>> WaterCorners { get; }
    internal IReadOnlyList<MapPrefabAttachmentTransfer> Attachments { get; }

    internal MapEffectiveResources(MapEffectiveContext context, IMapSurfaceSource source,
        IReadOnlyList<MapResolvedPlacement> placements, IReadOnlyList<MapPlacementSupport> supports,
        MapWaterSet? water, MapBuildingIntent intent, IReadOnlyDictionary<string, MapExactPoint> lights,
        IReadOnlyDictionary<string, IReadOnlyList<MapExactPoint>> waterCorners, IReadOnlyList<MapPrefabAttachmentTransfer> attachments,
        IReadOnlyDictionary<string, MapExactPoint> markers, IReadOnlyDictionary<string, string> markerInstances)
    {
        _context = context;
        SurfaceSource = source;
        Placements = Array.AsReadOnly(placements.ToArray());
        PlacementSupports = Array.AsReadOnly(supports.ToArray());
        _water = water?.Clone();
        _intent = intent.Clone();
        _intent = new(_intent.PayloadVersion, _intent.RoofLinks.OrderBy(r => r.Id, StringComparer.Ordinal).Select(link =>
            link with { AboveY = context.Transform(MapPrefabChildKind.RoofLink, link.Id)?.Height(MapExactValue.FromSingle(link.AboveY)).ToSingle() ?? link.AboveY }).ToArray(),
            _intent.RoofGroups.OrderBy(g => g.Id, StringComparer.Ordinal).ToArray(), _intent.Lights, _intent.Markers)
        { Roofs = _intent.Roofs.DistinctBy(r => (r.Kind, r.Id)).OrderBy(r => r.Id, StringComparer.Ordinal).ToArray(), Storeys = _intent.Storeys };
        MapPrefabValidation.RequireEffectiveAnnotations(_intent, placements, context.Surfaces);
        EffectiveLights = MapEffectiveAnnotations.CaptureLights(_intent, lights);
        EffectiveMarkers = MapEffectiveAnnotations.CaptureMarkers(_intent, markers, markerInstances);
        AnnotationDigest = MapEffectiveAnnotations.Digest(_intent, EffectiveLights, EffectiveMarkers);
        LightPoints = new ReadOnlyDictionary<string, MapExactPoint>(new Dictionary<string, MapExactPoint>(lights, StringComparer.Ordinal));
        WaterCorners = new ReadOnlyDictionary<string, IReadOnlyList<MapExactPoint>>(waterCorners.ToDictionary(p => p.Key,
            p => (IReadOnlyList<MapExactPoint>)Array.AsReadOnly(p.Value.ToArray()), StringComparer.Ordinal));
        Attachments = Array.AsReadOnly(attachments.ToArray());
        var paintTargets = context.Surfaces.Refs.Where(context.IsPartialPaint)
            .Select(s => s.PaintTargetSurfaceId ?? throw new MapDocumentException("PaintOverride requires a target surface."))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var paintRegions = new Dictionary<string, MapPaintRegionSet>(StringComparer.Ordinal);
        if (paintTargets.Length != 0)
        {
            var scope = MapScopedSurfaces.CompleteEffective(context).Scope with
            {
                DiscoverySurfaces = context.Surfaces.Refs.Where(context.IsPartialPaint).Select(s => s.Id)
                    .Concat(paintTargets).ToHashSet(StringComparer.Ordinal),
            };
            var view = SurfaceSource is MapComposedSurfaceSource ? Acquire(scope) : MapScopedSurfaces.CompleteEffective(context);
            foreach (string target in paintTargets) paintRegions.Add(target, MapPartialPaintCompiler.Compile(view, target));
        }
        PaintRegions = new ReadOnlyDictionary<string, MapPaintRegionSet>(paintRegions);
    }
    internal void RequireView(MapScopedSurfaces view)
    {
        if (view.EffectiveContext?.Digest != Digest) throw new ArgumentException("Acquired view differs from effective resources.", nameof(view));
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
    internal IReadOnlyDictionary<string, MapWaterBodyDoc> WaterSources { get; }
    internal MapEffectiveContext(MapSurfaceSet surfaces, IReadOnlyDictionary<string, MapEffectiveTransform> transforms, string digest,
        IReadOnlyList<MapStoreyDoc>? storeys = null, IReadOnlyList<MapWaterBodyDoc>? waterSources = null)
    {
        Storeys = Array.AsReadOnly((storeys ?? Array.Empty<MapStoreyDoc>()).ToArray());
        var water = new MapWaterSet();
        water.Bodies.AddRange(waterSources ?? Array.Empty<MapWaterBodyDoc>());
        WaterSources = new ReadOnlyDictionary<string, MapWaterBodyDoc>(water.Clone().Bodies.ToDictionary(b => b.Id, StringComparer.Ordinal));
        Surfaces = surfaces.Clone();
        _surfaces = new ReadOnlyDictionary<string, MapSurfaceRef>(Surfaces.Refs.ToDictionary(s => s.Id, StringComparer.Ordinal));
        Transforms = new ReadOnlyDictionary<string, MapEffectiveTransform>(new Dictionary<string, MapEffectiveTransform>(transforms, StringComparer.Ordinal));
        Digest = digest;
    }
    internal MapSurfaceRef Surface(string id) => _surfaces.TryGetValue(id, out var surface) ? surface : throw new MapDocumentException($"Missing effective surface '{id}'.");
    internal MapExactXz Xz(string id, MapExactXz local) => Transform(MapPrefabChildKind.Surface, id)?.Xz(local) ?? local;
    // Installed prefab/resource surfaces and precise native poses are additive R5 geometry. World paint
    // without such a pose keeps the released path, including when an unrelated prefab is present.
    internal bool IsPartialPaint(MapSurfaceRef surface) => surface.Role == MapSurfaceRole.PaintOverride &&
        Transforms.ContainsKey(Key(MapPrefabChildKind.Surface, surface.Id));
    internal static string Key(MapPrefabChildKind kind, string id) => kind + ":" + id;
    internal MapResourceTransform? Transform(MapPrefabChildKind kind, string id) =>
        Transforms.TryGetValue(Key(kind, id), out var transform) ? new(transform) : null;
    internal MapExactPoint Point(MapPrefabChildKind kind, string id, MapExactPoint local) => Transform(kind, id)?.Point(local) ?? local;
}
