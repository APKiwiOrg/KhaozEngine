using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Serialization;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Markers;

/// <summary>Value-only height provenance of a payload-2 effective marker. Binding is the world-space request. For a
/// Support height, WorldY is the support query's answer and the effective position Y is its exact value. An Explicit
/// height carries only its mode. Null members are omitted, and no member carries an acquisition witness.</summary>
public sealed record MapMarkerHeightProvenance(MapMarkerHeightMode Mode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MapSupportBinding? Binding,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MapFaceKey? Face,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SpaceId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MapRecordRef? Via,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? WorldY);

/// <summary>The witness-bearing support answer of one Support-height marker, published beside the effective rows.</summary>
public sealed record MapMarkerSupport(string MarkerId, MapSupportResult Support);

/// <summary>Resolves captured markers to effective rows. Explicit and payload-1 heights compose once through their
/// frame and never resample. A Support height is the shared support query's float answer, the value a support-bound
/// placement seats on. Only Supported succeeds.</summary>
internal static class MapMarkerResolution
{
    internal sealed record Resolution(IReadOnlyList<MapLocalMarkerDoc> Intent, IReadOnlyList<MapEffectiveMarker> Markers,
        IReadOnlyList<MapMarkerSupport> Supports);

    /// <summary>Every row in ordinal ID order, with the effective building-intent entry of each marker. That entry keeps
    /// its authored canonical encoding in world space: transformed XZ and yaw, a composed explicit height, and for a
    /// Support height Position.Y +0 with the binding taken through its frame once. Every support query takes the caller's
    /// <paramref name="limits"/>.</summary>
    internal static Resolution Resolve(IReadOnlyList<MapMarkerCandidate> candidates, IMapSurfaceSource surfaces,
        IReadOnlyList<string> assetSha256, MapQueryLimits limits)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(surfaces);
        ArgumentNullException.ThrowIfNull(assetSha256);
        ArgumentNullException.ThrowIfNull(limits);
        var intent = new List<MapLocalMarkerDoc>(candidates.Count);
        var markers = new List<MapEffectiveMarker>(candidates.Count);
        var supports = new List<MapMarkerSupport>();
        foreach (var candidate in candidates)
        {
            var marker = candidate.Value;
            var kernel = candidate.Frame is { } frame ? new MapResourceTransform(frame) : null;
            var point = kernel?.Point(Exact(marker.Position)) ?? Exact(marker.Position);
            float yaw = kernel is null ? marker.YawRadians : (float)(marker.YawRadians + kernel.Transform.YawRadians);
            MapMarkerHeightProvenance? provenance = null;
            MapLocalMarkerDoc entry;
            if (marker is { HeightMode: MapMarkerHeightMode.Support, SupportBinding: { } local })
            {
                var binding = kernel is null ? local with { } : MapPrefabResolver.WorldRequest(local, kernel.Transform);
                var support = Sample(marker.Id, point, binding, surfaces, assetSha256, limits);
                supports.Add(new(marker.Id, support));
                point = point with { Y = MapExactValue.FromSingle(support.WorldY) };
                provenance = new(MapMarkerHeightMode.Support, binding with { }, support.Face, support.SpaceId, support.Via, support.WorldY);
                entry = kernel is null ? marker : marker with
                {
                    Position = new(point.X.ToSingle(), 0f, point.Z.ToSingle()),
                    YawRadians = yaw,
                    SupportBinding = binding,
                };
            }
            else
            {
                if (marker.HeightMode == MapMarkerHeightMode.Explicit) provenance = new(MapMarkerHeightMode.Explicit, null, null, null, null, null);
                entry = kernel is null ? marker : marker with
                {
                    Position = new(point.X.ToSingle(), point.Y.ToSingle(), point.Z.ToSingle()),
                    YawRadians = yaw,
                };
            }
            intent.Add(entry);
            markers.Add(new(marker.PayloadVersion, marker.Id, marker.Name, point, entry.YawRadians, marker.Role, marker.Enabled,
                Array.AsReadOnly(marker.Tags.ToArray()), candidate.InstanceId)
            { HeightProvenance = provenance, ArchetypeId = marker.ArchetypeId });
        }
        return new(intent.AsReadOnly(),
            Array.AsReadOnly(markers.OrderBy(m => m.Id, StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(supports.OrderBy(s => s.MarkerId, StringComparer.Ordinal).ToArray()));
    }

    // The shared seat query, so a marker's height is exactly the value a support-bound placement seats on.
    static MapSupportResult Sample(string id, MapExactPoint point, MapSupportBinding binding, IMapSurfaceSource surfaces,
        IReadOnlyList<string> assetSha256, MapQueryLimits limits)
    {
        MapSupportResult support;
        try { support = MapResolverV2.SeatSupport(point, binding, surfaces, assetSha256, limits, Scene.MapAcquisitionCaller.Marker); }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        {
            throw new MapDocumentException($"Marker '{id}' support status NotRepresentable: overflow.");
        }
        if (support.Status != MapSupportStatus.Supported)
            throw support.ResolutionRefusal($"Marker '{id}'");
        return support;
    }

    static MapExactPoint Exact(Vector3 point) => new(MapExactValue.FromSingle(point.X), MapExactValue.FromSingle(point.Y), MapExactValue.FromSingle(point.Z));
}
