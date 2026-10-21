using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Serialization;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>Payload-1 light in world metres, after one owner-frame composition and one point/radius freeze.</summary>
public sealed record MapEffectiveLight(int PayloadVersion, string Id, string? OwnerPlacementId,
    MapExactPoint Position, Vector3 Colour, float Intensity, float Radius, bool CastShadows);

/// <summary>Resolved XYZ marker for role consumers. Ordered tags and optional instance provenance are retained. The
/// position always carries the resolved height. Payload 2 adds the value-only height provenance and archetype, both
/// omitted when null, so payload-1 rows serialize as released.</summary>
public sealed record MapEffectiveMarker(int PayloadVersion, string Id, string Name, MapExactPoint Position,
    float YawRadians, string Role, bool Enabled, IReadOnlyList<string> Tags, string? InstanceId)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MapMarkerHeightProvenance? HeightProvenance { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArchetypeId { get; init; }
}

/// <summary>Immutable annotations produced by the bound effective expansion, including implicit leaf Light children.
/// This never scans or owns leaf resources a second time.</summary>
public static class MapEffectiveAnnotations
{
    public static IReadOnlyList<MapEffectiveLight> Lights(MapEffectiveResources resources)
    { ArgumentNullException.ThrowIfNull(resources); return resources.EffectiveLights; }

    public static IReadOnlyList<MapEffectiveMarker> Markers(MapEffectiveResources resources)
    { ArgumentNullException.ThrowIfNull(resources); return resources.EffectiveMarkers; }

    public static IReadOnlyList<MapEffectiveLight> Lights(Scene.MapScopedScene scene)
    { ArgumentNullException.ThrowIfNull(scene); return scene.EffectiveLights; }

    public static IReadOnlyList<MapEffectiveMarker> Markers(Scene.MapScopedScene scene)
    { ArgumentNullException.ThrowIfNull(scene); return scene.EffectiveMarkers; }

    /// <summary>An explicit light frame takes precedence over its placement owner's frame.</summary>
    internal static MapEffectiveTransform? LightFrame(IReadOnlyDictionary<string, MapEffectiveTransform> transforms, MapLocalLightDoc light)
    {
        if (transforms.TryGetValue(MapEffectiveContext.Key(MapPrefabChildKind.Light, light.Id), out var frame)) return frame;
        return light.Owner is { Kind: MapPrefabChildKind.Placement } owner &&
            transforms.TryGetValue(MapEffectiveContext.Key(MapPrefabChildKind.Placement, owner.Id), out frame) ? frame : null;
    }

    internal static IReadOnlyList<MapEffectiveLight> CaptureLights(MapBuildingIntent intent, IReadOnlyDictionary<string, MapExactPoint> points) =>
        Array.AsReadOnly(intent.Lights.OrderBy(l => l.Id, StringComparer.Ordinal).Select(l =>
            new MapEffectiveLight(l.PayloadVersion, l.Id, l.Owner?.Id, points[l.Id], l.Colour, l.Intensity, l.Radius, l.CastShadows)).ToArray());

    // R6-P12. The released scheme stays unless a payload-2 marker is present. The witness channel is never digested.
    internal static string Digest(MapBuildingIntent intent, IReadOnlyList<MapEffectiveLight> lights, IReadOnlyList<MapEffectiveMarker> markers) =>
        lights.Count == 0 && markers.Count == 0 && intent.Roofs.Count == 0 && intent.RoofLinks.Count == 0 && intent.RoofGroups.Count == 0 ? "" :
        MapPrefabJson.Digest(markers.Any(m => m.PayloadVersion == 2) ? "kemap/effective-annotations/2" : "kemap/effective-annotations/1",
            new { Roofs = intent.Roofs, Links = intent.RoofLinks, Groups = intent.RoofGroups, Lights = lights, Markers = markers });

    internal static MapBox3 Bounds(MapEffectiveLight light) => Bounds(light.Position, MapExactValue.FromSingle(light.Radius));
    internal static MapBox3 Bounds(MapEffectiveMarker marker) => Bounds(marker.Position, new(0, 1));

    static MapBox3 Bounds(MapExactPoint point, MapExactValue radius)
    {
        var r = MapWideRational.From(radius);
        double Lower(MapExactValue v) => Outward(MapWideRational.From(v).Subtract(r), false);
        double Upper(MapExactValue v) => Outward(MapWideRational.From(v).Add(r), true);
        return new(Lower(point.X), Lower(point.Y), Lower(point.Z), Upper(point.X), Upper(point.Y), Upper(point.Z));
    }

    static double Outward(MapWideRational exact, bool upper)
    {
        double value = exact.ToDouble();
        int direction = MapWideRational.FromDouble(value).CompareTo(exact);
        while (upper ? direction < 0 : direction > 0)
        {
            value = upper ? Math.BitIncrement(value) : Math.BitDecrement(value);
            direction = MapWideRational.FromDouble(value).CompareTo(exact);
        }
        return value;
    }
}
