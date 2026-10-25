using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using KhaozEngine.MapDoc.Markers;
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
    readonly MapEffectiveResolutionProduct _product;
    internal MapEffectiveResolutionProduct Product => _product;
    internal MapEffectiveResources(MapEffectiveResolutionProduct product) => _product = product;
    public IReadOnlyList<MapResolvedPlacement> Placements => _product.Placements;
    public IReadOnlyList<MapPlacementSupport> PlacementSupports => _product.PlacementSupports;
    public MapSurfaceSet Surfaces => _product.Surfaces;
    public IReadOnlyDictionary<string, MapEffectiveTransform> Transforms => _product.Transforms;
    public MapWaterSet? Water => _product.Water;
    public MapBuildingIntent BuildingIntent => _product.BuildingIntent;
    public string Digest => _product.Digest;
    public IMapSurfaceSource SurfaceSource => _product.SurfaceSource;
    public IReadOnlyDictionary<string, MapPaintRegionSet> PaintRegions => _product.PaintRegions;
    internal IReadOnlyDictionary<string, MapExactPoint> LightPoints => _product.LightPoints;
    internal IReadOnlyList<MapEffectiveLight> EffectiveLights => _product.EffectiveLights;
    internal IReadOnlyList<MapEffectiveMarker> EffectiveMarkers => _product.EffectiveMarkers;
    public IReadOnlyList<MapMarkerSupport> MarkerSupports => _product.MarkerSupports;
    public string AnnotationDigest => _product.AnnotationDigest;
    internal IReadOnlyDictionary<string, IReadOnlyList<MapExactPoint>> WaterCorners => _product.WaterCorners;
    internal IReadOnlyList<MapPrefabAttachmentTransfer> Attachments => _product.Attachments;
    internal string? ClosureHash { get; init; }
    internal string? SourceFingerprint { get; set; }
    internal IReadOnlyDictionary<string, MapSupportBinding> SupportRequests => _product.SupportRequests;
    internal void RequireView(MapScopedSurfaces view) => _product.RequireView(view);
    public MapScopedSurfaces CompleteView() => _product.CompleteView();
    public MapScopedSurfaces Acquire(MapSurfaceScope scope) => _product.Acquire(scope);
}

/// <summary>Owned local descriptors and unfrozen poses. Acquired views retain this captured context.</summary>
internal sealed class MapEffectiveContext
{
    internal Scene.MapPreparationReservation? PreparationOwnership { get; }
    internal long IndexBytes { get; }
    readonly IReadOnlyDictionary<string, MapSurfaceRef> _surfaces;
    readonly Dictionary<string, MapEffectiveTransform> _transformValues;
    internal MapSurfaceSet Surfaces { get; }
    internal IReadOnlyDictionary<string, MapEffectiveTransform> Transforms { get; }
    internal string Digest { get; }
    internal IReadOnlyList<MapStoreyDoc> Storeys { get; }
    internal IReadOnlyDictionary<string, MapWaterBodyDoc> WaterSources { get; }
    internal MapEffectiveContext(MapSurfaceSet surfaces, IReadOnlyDictionary<string, MapEffectiveTransform> transforms, string digest,
        IReadOnlyList<MapStoreyDoc>? storeys = null, IReadOnlyList<MapWaterBodyDoc>? waterSources = null)
    {
        using var preparation = Scene.MapSceneAdmission.Current?.BeginPreparation();
        Scene.MapSceneAdmission.Current?.ReserveBytes(checked(256L + 128L * surfaces.Refs.Count + 96L * transforms.Count + 8L * (storeys?.Count ?? 0)));
        MapSurfaceSourcePreparation.BeforeDictionary<string, MapSurfaceRef>(surfaces.Refs.Count);
        MapSurfaceSourcePreparation.BeforeDictionary<string, MapEffectiveTransform>(transforms.Count);
        MapSurfaceSourcePreparation.BeforeDictionary<string, MapWaterBodyDoc>(waterSources?.Count ?? 0);
        Storeys = Array.AsReadOnly((storeys ?? Array.Empty<MapStoreyDoc>()).ToArray());
        var water = new MapWaterSet();
        water.Bodies.AddRange(waterSources ?? Array.Empty<MapWaterBodyDoc>());
        var waterIndex = water.Clone().Bodies.ToDictionary(b => b.Id, StringComparer.Ordinal);
        WaterSources = new ReadOnlyDictionary<string, MapWaterBodyDoc>(waterIndex);
        Surfaces = surfaces.Clone();
        var surfaceIndex = Surfaces.Refs.ToDictionary(s => s.Id, StringComparer.Ordinal);
        _surfaces = new ReadOnlyDictionary<string, MapSurfaceRef>(surfaceIndex);
        _transformValues = new(transforms, StringComparer.Ordinal);
        Transforms = new ReadOnlyDictionary<string, MapEffectiveTransform>(_transformValues);
        IndexBytes = checked(MapSurfaceSourcePreparation.DictionaryBytes(surfaceIndex) +
            MapSurfaceSourcePreparation.DictionaryBytes(_transformValues) + MapSurfaceSourcePreparation.DictionaryBytes(waterIndex));
        Digest = digest;
        PreparationOwnership = preparation?.Complete(new Scene.MapSceneInventory().Context(this));
    }
    internal MapSurfaceRef Surface(string id) => _surfaces.TryGetValue(id, out var surface) ? surface : throw new MapDocumentException($"Missing effective surface '{id}'.");
    internal MapExactXz Xz(string id, MapExactXz local) => Transform(MapPrefabChildKind.Surface, id)?.Xz(local) ?? local;
    // Installed prefab/resource surfaces and precise native poses are additive R5 geometry. World paint
    // without such a pose keeps the released path, including when an unrelated prefab is present.
    internal bool IsPartialPaint(MapSurfaceRef surface) => surface.Role == MapSurfaceRole.PaintOverride &&
        Transforms.ContainsKey(Key(MapPrefabChildKind.Surface, surface.Id));
    internal static string Key(MapPrefabChildKind kind, string id) => kind + ":" + id;
    // Preparation preflight must not allocate a concatenated key or a transform wrapper.
    internal bool TryGeometryTransform(MapPrefabChildKind kind, string id, out MapEffectiveTransform value)
    {
        string prefix = kind == MapPrefabChildKind.Surface ? "Surface:" : "Record:";
        foreach (var entry in _transformValues)
            if (entry.Key.AsSpan().StartsWith(prefix, StringComparison.Ordinal) &&
                entry.Key.AsSpan(prefix.Length).SequenceEqual(id.AsSpan()))
            { value = entry.Value; return true; }
        value = default;
        return false;
    }
    internal MapResourceTransform? Transform(MapPrefabChildKind kind, string id) =>
        Transforms.TryGetValue(Key(kind, id), out var transform) ? new(transform) : null;
    internal MapExactPoint Point(MapPrefabChildKind kind, string id, MapExactPoint local) => Transform(kind, id)?.Point(local) ?? local;
}
