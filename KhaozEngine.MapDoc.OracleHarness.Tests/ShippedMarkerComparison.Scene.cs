using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;

namespace KhaozEngine.Tests.MapDocOracle;

internal static partial class ShippedMarkerComparison
{
    static ExpectedPoint[] SourcePoints(ShippedMarkerInputs inputs)
    {
        var ground = TileWorldColliders.Build(ObjectView(inputs.World, null), inputs.Catalogs).Ground;
        return inputs.SourceMarkers.Select(marker => Expected(inputs, marker, ground)).ToArray();
    }

    // The linked native scaffold supplies no expected marker. Both scene heads and plans use source standing points.
    internal static ShippedMarkerReport CompareScene(ShippedMarkerInputs inputs, IReadOnlyList<MapEffectiveMarker> actual,
        MapScopedSurfaces view, MapSceneScope? scope = null)
    {
        var evidence = new Evidence();
        var expected = SourcePoints(inputs).ToDictionary(p => p.Role.Id, StringComparer.Ordinal);
        var ids = actual.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var required = expected.Values.Where(p => scope is null || InScope(p, scope)).Select(p => p.Role.Id).ToArray();
        evidence.Check(scope is null ? "marker-inventory" : "marker-scope-inventory",
            actual.Count == ids.Count && ids.All(expected.ContainsKey) && required.All(ids.Contains) &&
            (scope is not null || ids.Count == expected.Count), new { required, actual = actual.Select(m => m.Id) });
        foreach (var marker in actual)
        {
            if (!expected.TryGetValue(marker.Id, out var point)) continue;
            var role = point.Role;
            evidence.Check("marker-values", marker.Name == point.Source.Name && marker.Tags.SequenceEqual(point.Source.Tags) &&
                marker.Role == role.Role && marker.Enabled == role.Enabled && marker.ArchetypeId == role.Archetype &&
                Math.Abs(Math.IEEERemainder(marker.YawRadians - role.Yaw, 2 * Math.PI)) <= YawTolerance, new { marker.Id });
            evidence.Check("marker-position", Near(marker.Position.X.ToDouble(), point.Position.X) && Near(marker.Position.Y.ToDouble(), point.Position.Y) &&
                Near(marker.Position.Z.ToDouble(), point.Position.Z), new { marker.Id, expected = point.Position, actual = marker.Position });
            var binding = inputs.Supports[role.SupportName].Binding;
            var provenance = marker.HeightProvenance;
            bool bound = role.HeightMode == MapMarkerHeightMode.Support;
            var query = bound ? new MapSupportQuery(view) : null;
            var support = !bound ? null : binding.Kind == MapSupportBindingKind.Surface
                ? query!.SampleSurfaceExact(marker.Position, binding.SurfaceId!)
                : query!.Select(new(ShippedSceneInputs.Point(marker.Position.X.ToDouble(), binding.ReferenceY ?? 0, marker.Position.Z.ToDouble()),
                    null, binding.SpaceId, null, binding.SearchAbove ?? 0, binding.SearchBelow ?? 0, null));
            evidence.Check("marker-binding", marker.PayloadVersion == 2 && provenance?.Mode == role.HeightMode &&
                (bound ? provenance.Binding == binding : provenance.Binding is null), new { marker.Id });
            evidence.Check("marker-provenance", bound
                ? support is { Status: MapSupportStatus.Supported, IsCaptureSupport: true, Face: not null } && provenance is not null &&
                    (binding.Kind != MapSupportBindingKind.Surface || support.Face.Value.OwnerId == binding.SurfaceId) &&
                    provenance.Face == support.Face && provenance.SpaceId == support.SpaceId && provenance.Via == support.Via &&
                    provenance.WorldY == support.WorldY && marker.Position.Y.ToDouble() == support.WorldY
                : support is null && provenance is { Face: null, SpaceId: null, Via: null, WorldY: null }, new { marker.Id });
            var correspondence = inputs.Correspondences[point.Source.Name];
            var membership = MapMarkerSpaces.Query(view, marker);
            evidence.Check("marker-space", membership.Status == (correspondence.SpaceId is null ? MapMembershipStatus.Outside : MapMembershipStatus.Resolved) &&
                membership.SpaceId == correspondence.SpaceId && membership.ParentSpaceIds.SequenceEqual(correspondence.Parents),
                new
                {
                    marker.Id,
                    expected = correspondence.SpaceId,
                    status = membership.Status.ToString(),
                    membership.SpaceId,
                    expectedParents = correspondence.Parents,
                    parents = membership.ParentSpaceIds,
                    membership.Detail
                });
        }
        return evidence.Report(1, expected.Count);
    }

    static bool InScope(ExpectedPoint point, MapSceneScope scope)
    {
        var b = scope.DrawBounds; double pad = scope.MotionPaddingMetres;
        return scope.Channels.HasFlag(MapSceneChannels.Annotations) && point.Position.X >= b.MinX - pad && point.Position.X <= b.MaxX + pad &&
            point.Position.Z >= b.MinZ - pad && point.Position.Z <= b.MaxZ + pad;
    }
}
