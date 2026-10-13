using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>Payload-1 light in world metres, after one owner-frame composition and one point/radius freeze.</summary>
public sealed record MapEffectiveLight(int PayloadVersion, string Id, string? OwnerPlacementId,
    MapExactPoint Position, Vector3 Colour, float Intensity, float Radius, bool CastShadows);

/// <summary>Payload-1 XYZ marker for role consumers. Ordered tags and optional instance provenance are retained.</summary>
public sealed record MapEffectiveMarker(int PayloadVersion, string Id, string Name, MapExactPoint Position,
    float YawRadians, string Role, bool Enabled, IReadOnlyList<string> Tags, string? InstanceId);

/// <summary>Immutable annotations produced by the bound effective expansion, including implicit leaf Light children.
/// This never scans or owns leaf resources a second time.</summary>
public static class MapEffectiveAnnotations
{
    public static IReadOnlyList<MapEffectiveLight> Lights(MapEffectiveResources resources)
    { ArgumentNullException.ThrowIfNull(resources); return resources.EffectiveLights; }

    public static IReadOnlyList<MapEffectiveMarker> Markers(MapEffectiveResources resources)
    { ArgumentNullException.ThrowIfNull(resources); return resources.EffectiveMarkers; }

    internal static IReadOnlyList<MapEffectiveLight> CaptureLights(MapBuildingIntent intent, IReadOnlyDictionary<string, MapExactPoint> points) =>
        Array.AsReadOnly(intent.Lights.OrderBy(l => l.Id, StringComparer.Ordinal).Select(l =>
            new MapEffectiveLight(l.PayloadVersion, l.Id, l.Owner?.Id, points[l.Id], l.Colour, l.Intensity, l.Radius, l.CastShadows)).ToArray());

    internal static IReadOnlyList<MapEffectiveMarker> CaptureMarkers(MapBuildingIntent intent,
        IReadOnlyDictionary<string, MapExactPoint> points, IReadOnlyDictionary<string, string> instances) =>
        Array.AsReadOnly(intent.Markers.OrderBy(m => m.Id, StringComparer.Ordinal).Select(m =>
            new MapEffectiveMarker(m.PayloadVersion, m.Id, m.Name, points[m.Id], m.YawRadians, m.Role, m.Enabled,
                Array.AsReadOnly(m.Tags.ToArray()), instances.GetValueOrDefault(m.Id))).ToArray());

    internal static string Digest(MapBuildingIntent intent, IReadOnlyList<MapEffectiveLight> lights, IReadOnlyList<MapEffectiveMarker> markers) =>
        lights.Count == 0 && markers.Count == 0 && intent.Roofs.Count == 0 && intent.RoofLinks.Count == 0 && intent.RoofGroups.Count == 0 ? "" :
        MapPrefabJson.Digest("kemap/effective-annotations/1", new { Roofs = intent.Roofs, Links = intent.RoofLinks, Groups = intent.RoofGroups, Lights = lights, Markers = markers });

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
