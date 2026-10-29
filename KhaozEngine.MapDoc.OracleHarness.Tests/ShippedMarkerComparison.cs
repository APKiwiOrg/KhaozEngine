using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.Versioning;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;

namespace KhaozEngine.Tests.MapDocOracle;

internal sealed record ShippedMarkerReport(IReadOnlyList<string> Failures, string DetailJson, IReadOnlyList<OracleComparisonFailure> ClassifiedFailures);

/// <summary>Assertion-free comparison. Its source-derived details belong only in the secure private report.</summary>
[UnsupportedOSPlatform("windows")]
internal static partial class ShippedMarkerComparison
{
    internal const double PositionTolerance = 0.00001;
    internal const double YawTolerance = 0.000001;
    static MapWorldBuildOptions Options() => new(new("private-marker-oracle", 1, "marker-comparison/1", 2), "marker-comparison/1", new());

    internal static ShippedMarkerReport Compare(ShippedMarkerInputs inputs)
    {
        var evidence = new Evidence();
        // The authoritative server stands bodies on its collider ground and on walk surfaces. The ground sampler reads
        // terrain only, so it is built from a view without objects, which no unrelated archetype can refuse.
        var expected = SourcePoints(inputs);
        var owners = inputs.Supports.Values.Where(s => s.ObjectId is not null).ToDictionary(s => s.Name,
            s => ObjectView(inputs.World, s.ObjectId!.Value), StringComparer.Ordinal);
        foreach (var point in expected)
        {
            var support = inputs.Supports[point.Role.SupportName];
            if (support.SourceKind == "object")
                evidence.Resource("Marker", "marker", "source-").Check("source-support", TileWalkSurfaces.TryHeightAt(owners[support.Name], inputs.Catalogs,
                    point.Position.X, point.Position.Z, point.Source.Plane, out float top) && Near(top, point.Position.Y),
                    new { sourceName = point.Source.Name, supportName = support.Name, expectedY = point.Position.Y, ownerY = top });
        }
        MapDocument original = inputs.ReloadDocument();
        string tiled = Path.Combine(inputs.ReportDirectory, "marker-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tiled, PrivateOracleFixtures.SecureDirectoryMode);
        try
        {
            MapDocumentFile.SaveTiled(inputs.ReloadDocument(), tiled);
            var heads = new[]
            {
                MapWorldBuilder.Build(inputs.ReloadDocument(), inputs.ReloadAssets(), Options()),
                MapWorldBuilder.Build(inputs.ReloadDocument(), inputs.ReloadAssets(), Options()),
                MapWorldBuilder.Build(MapDocumentFile.LoadTiled(tiled), inputs.ReloadAssets(), Options()),
                MapWorldBuilder.Build(MapDocumentFile.LoadTiled(tiled), inputs.ReloadAssets(), Options()),
            };
            CheckExpectations(inputs, expected, evidence);
            for (int i = 0; i < heads.Length; i++)
            {
                var world = heads[i];
                evidence.Check("storage-tile-size", world.TileSize == original.TileSize, new { head = i, world.TileSize, expected = original.TileSize });
                CompareHead(inputs, world, expected, evidence, i);
                if (i == 0) continue;
                var first = heads[0];
                evidence.Check("independent-heads", !ReferenceEquals(first, world) && !ReferenceEquals(first.Document, world.Document) &&
                    !ReferenceEquals(first.Document.EffectiveResources, world.Document.EffectiveResources) && !ReferenceEquals(first.Surfaces, world.Surfaces) &&
                    !ReferenceEquals(first.Markers, world.Markers), new { head = i });
                evidence.Check("head-identity", first.AuthoredHash == world.AuthoredHash && first.BuildHash == world.BuildHash &&
                    first.AnnotationDigest == world.AnnotationDigest, new { head = i, first = first.BuildHash, other = world.BuildHash });
                foreach (var marker in first.Markers)
                {
                    var other = world.Markers.SingleOrDefault(m => m.Id == marker.Id);
                    evidence.Resource("Marker", "marker").Check("head-marker", other is not null && SameMarker(marker, other), new { head = i, marker.Id });
                }
            }
            return evidence.Report(heads.Length, inputs.SourceMarkers.Count);
        }
        finally { Directory.Delete(tiled, true); }
    }

    sealed record ExpectedPoint(MarkerSourcePoint Source, MarkerRoleRule Role, Vector3 Position);

    // Terrain is the server's ground triangle under the point. Standing is the higher of that and the walk surfaces on
    // the marker's plane. The bilinear TileDocumentGroundHeight is the client presenter's drawing rule and differs
    // from the drawn triangles on a twisted tile.
    static ExpectedPoint Expected(ShippedMarkerInputs inputs, MarkerSourcePoint marker, TileGroundSampler ground)
    {
        var role = inputs.Roles[marker.Name];
        float tileX = (float)(marker.X + role.OffsetX), tileZ = (float)(marker.Z + role.OffsetZ);
        ShippedMarkerInputs.Require(float.IsFinite(tileX) && float.IsFinite(tileZ), "marker offset exceeds source coordinate range");
        float worldX = TileWorldSpace.WorldX(tileX, inputs.World.TileSize), worldZ = TileWorldSpace.WorldZ(tileZ, inputs.World.TileSize);
        float height = ground.HeightAt(worldX, worldZ);
        if (role.HeightRule == "standing" && TileWalkSurfaces.TryHeightAt(inputs.World, inputs.Catalogs, worldX, worldZ, marker.Plane, out float surface) &&
            surface > height) height = surface;
        return new(marker, role, new(worldX, height, worldZ));
    }

    static void CompareHead(ShippedMarkerInputs inputs, MapBuiltWorld world, ExpectedPoint[] expected, Evidence evidence, int head)
    {
        var registry = new MapMarkerRegistry(world.Markers);
        var expectedIds = expected.Select(e => e.Role.Id).Order(StringComparer.Ordinal).ToArray();
        evidence.Check("source-inventory", world.Markers.Count == inputs.SourceMarkers.Count, new { head, source = inputs.SourceMarkers.Count, native = world.Markers.Count });
        evidence.Check("marker-inventory", expectedIds.SequenceEqual(registry.All.Select(m => m.Id)), new { head, expectedIds, actual = registry.All.Select(m => m.Id) });
        evidence.Check("marker-name-set", inputs.SourceMarkers.Select(m => m.Name).Order(StringComparer.Ordinal).SequenceEqual(registry.All.Select(m => m.Name).Order(StringComparer.Ordinal)), new { head });
        foreach (var point in expected)
        {
            var role = point.Role;
            var markerChecks = evidence.Resource("Marker", "marker", "marker-");
            var marker = registry.FindById(role.Id);
            if (marker is null) continue;
            markerChecks.Check("marker-name", marker.Name == point.Source.Name && registry.FindByName(point.Source.Name)?.Id == role.Id, new { head, marker.Id, expected = point.Source.Name, marker.Name });
            markerChecks.Check("marker-tags", marker.Tags.SequenceEqual(point.Source.Tags), new { head, marker.Id, expected = point.Source.Tags, marker.Tags });
            markerChecks.Check("marker-role", marker.Role == role.Role, new { head, marker.Id, expected = role.Role, marker.Role });
            markerChecks.Check("marker-enabled", marker.Enabled == role.Enabled, new { head, marker.Id, expected = role.Enabled, marker.Enabled });
            markerChecks.Check("marker-archetype", marker.ArchetypeId == role.Archetype, new { head, marker.Id, expected = role.Archetype, marker.ArchetypeId });
            markerChecks.Check("marker-position", Near(marker.Position.X.ToDouble(), point.Position.X) && Near(marker.Position.Z.ToDouble(), point.Position.Z),
                new { head, marker.Id, expected = new[] { point.Position.X, point.Position.Z }, actual = new[] { marker.Position.X.ToDouble(), marker.Position.Z.ToDouble() }, tolerance = PositionTolerance });
            markerChecks.Check("marker-height", Near(marker.Position.Y.ToDouble(), point.Position.Y),
                new { head, marker.Id, role.HeightRule, expected = point.Position.Y, actual = marker.Position.Y.ToDouble(), tolerance = PositionTolerance });
            markerChecks.Check("marker-yaw", Math.Abs(Math.IEEERemainder(marker.YawRadians - role.Yaw, 2 * Math.PI)) <= YawTolerance,
                new { head, marker.Id, expected = role.Yaw, marker.YawRadians, tolerance = YawTolerance });
            CompareSupport(inputs, world, marker, role, markerChecks, head);
            var correspondence = inputs.Correspondences[point.Source.Name];
            var membership = MapMarkerSpaces.Query(world.Surfaces, marker);
            markerChecks.Check("marker-space", membership.Status == (correspondence.SpaceId is null ? MapMembershipStatus.Outside : MapMembershipStatus.Resolved) && membership.SpaceId == correspondence.SpaceId,
                new { head, marker.Id, expected = correspondence.SpaceId, status = membership.Status.ToString(), membership.SpaceId });
            markerChecks.Check("marker-parents", membership.ParentSpaceIds.SequenceEqual(correspondence.Parents),
                new { head, marker.Id, expected = correspondence.Parents, actual = membership.ParentSpaceIds });
            if (role.Projection == "label") evidence.Record("label-point", new
            {
                point.Source.Name,
                sourceTile = new[] { point.Source.X, point.Source.Z, point.Source.Plane },
                role.OffsetX,
                role.OffsetZ,
                acceptance = "deferred R11 acceptance",
            });
        }
        CompareQueries(world, registry, expected, evidence.Resource("Marker", "registry"), head);
    }

    static void CompareSupport(ShippedMarkerInputs inputs, MapBuiltWorld world, MapEffectiveMarker marker, MarkerRoleRule role, OracleResourceChecks evidence, int head)
    {
        var provenance = marker.HeightProvenance;
        var support = world.Document.EffectiveResources!.MarkerSupports.SingleOrDefault(s => s.MarkerId == marker.Id)?.Support;
        var binding = inputs.Supports[role.SupportName].Binding;
        bool bound = role.HeightMode == MapMarkerHeightMode.Support;
        evidence.Check("marker-binding", marker.PayloadVersion == 2 && provenance?.Mode == role.HeightMode &&
            (bound ? provenance.Binding == binding : provenance.Binding is null), new { head, marker.Id, expected = binding, actual = provenance });
        evidence.Check("marker-provenance", bound
            ? support is { Status: MapSupportStatus.Supported, IsCaptureSupport: true, Face: not null } && provenance is not null &&
                (binding.Kind != MapSupportBindingKind.Surface || support.Face.Value.OwnerId == binding.SurfaceId) &&
                provenance.Face == support.Face && provenance.SpaceId == support.SpaceId && provenance.Via == support.Via && provenance.WorldY == support.WorldY &&
                marker.Position.Y.ToDouble() == support.WorldY
            : support is null && provenance is { Face: null, SpaceId: null, Via: null, WorldY: null }, new { head, marker.Id, actual = provenance });
    }

    static void CompareQueries(MapBuiltWorld world, MapMarkerRegistry registry, ExpectedPoint[] expected, OracleResourceChecks evidence, int head)
    {
        foreach (var group in expected.GroupBy(p => p.Role.Role, StringComparer.Ordinal))
        {
            foreach (bool enabledOnly in new[] { false, true })
            {
                string[] ids = group.Where(p => !enabledOnly || p.Role.Enabled).Select(p => p.Role.Id).Order(StringComparer.Ordinal).ToArray();
                var actual = registry.ByRole(group.Key, enabledOnly);
                evidence.Check("role-query", ids.SequenceEqual(actual.Select(m => m.Id)), new { head, role = group.Key, enabledOnly });
                evidence.Check("player-start-projection", ids.SequenceEqual(MapMarkerProjection.PlayerStarts(registry, group.Key, enabledOnly).Select(v => v.Marker.Id)), new { head, role = group.Key, enabledOnly });
                var spawns = MapMarkerProjection.Spawns(registry, group.Key, enabledOnly);
                evidence.Check("spawn-projection", ids.SequenceEqual(spawns.Select(v => v.Marker.Id)) && spawns.All(v => v.ArchetypeId == v.Marker.ArchetypeId), new { head, role = group.Key, enabledOnly });
                evidence.Check("landmark-projection", ids.SequenceEqual(MapMarkerProjection.Landmarks(registry, group.Key, enabledOnly).Select(v => v.Marker.Id)), new { head, role = group.Key, enabledOnly });
            }
        }
        foreach (string tag in expected.SelectMany(p => p.Source.Tags).Distinct(StringComparer.Ordinal))
            foreach (bool enabledOnly in new[] { false, true })
            {
                var ids = expected.Where(p => p.Source.Tags.Contains(tag) && (!enabledOnly || p.Role.Enabled)).Select(p => p.Role.Id).Order(StringComparer.Ordinal);
                evidence.Check("tag-query", ids.SequenceEqual(registry.ByTag(tag, enabledOnly).Select(m => m.Id)), new { head, tag, enabledOnly });
            }
        foreach (var point in expected)
        {
            // The engine filters exact native positions, so the edges and the expected set use this head's own native
            // points. Source fidelity is the separate marker-position check. Both axes include their lower edge and
            // exclude their upper edge, even west of the origin.
            if (registry.FindById(point.Role.Id) is not { } anchor) continue;
            double x = anchor.Position.X.ToDouble(), z = anchor.Position.Z.ToDouble();
            foreach (bool enabledOnly in new[] { false, true })
                foreach (var rect in new[] { new MapMarkerRect(x, z, x + 0.25, z + 0.25), new MapMarkerRect(x - 0.25, z - 0.25, x, z) })
                {
                    var ids = expected.Where(p => !enabledOnly || p.Role.Enabled).Select(p => registry.FindById(p.Role.Id)).OfType<MapEffectiveMarker>()
                        .Where(m => m.Position.X.ToDouble() >= rect.MinX && m.Position.X.ToDouble() < rect.MaxX &&
                            m.Position.Z.ToDouble() >= rect.MinZ && m.Position.Z.ToDouble() < rect.MaxZ)
                        .Select(m => m.Id).Order(StringComparer.Ordinal).ToArray();
                    var found = registry.InRect(rect, enabledOnly, m => MapMarkerSpaces.Query(world.Surfaces, m));
                    evidence.Check("rectangle-query", ids.SequenceEqual(found.Select(p => p.Marker.Id)), new { head, point.Role.Id, rect, enabledOnly });
                    foreach (var row in found)
                    {
                        var direct = MapMarkerSpaces.Query(world.Surfaces, row.Marker);
                        evidence.Check("rectangle-membership", row.Space.Status == direct.Status && row.Space.SpaceId == direct.SpaceId &&
                            row.Space.ParentSpaceIds.SequenceEqual(direct.ParentSpaceIds), new { head, row.Marker.Id });
                    }
                }
        }

    }

    static void CheckExpectations(ShippedMarkerInputs inputs, ExpectedPoint[] expected, Evidence evidence)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["total"] = inputs.SourceMarkers.Count,
            ["playerStart"] = expected.Count(p => p.Role.Projection == "player-start"),
            ["spawn"] = expected.Count(p => p.Role.Projection == "spawn"),
            ["landmark"] = expected.Count(p => p.Role.Projection == "landmark"),
            ["label"] = expected.Count(p => p.Role.Projection == "label"),
            ["negativeX"] = expected.Count(p => p.Position.X < 0),
            ["supportBound"] = expected.Count(p => p.Role.HeightMode == MapMarkerHeightMode.Support),
            ["enabled"] = expected.Count(p => p.Role.Enabled),
        };
        foreach (var (name, count) in counts)
            evidence.Check("expectation-" + name.ToLowerInvariant(), inputs.Expectations.GetProperty(name).GetInt32() == count,
                new { name, expected = inputs.Expectations.GetProperty(name).GetInt32(), actual = count });
    }

    // Share the frozen terrain as read-only data, with a separate object list holding only the named source owner, or
    // no object at all. Ground, owner coverage and owner height still go through released samplers, never a copy.
    static TileWorldDocument ObjectView(TileWorldDocument source, long? objectId)
    {
        var result = new TileWorldDocument { TileSize = source.TileSize, PlaneCount = source.PlaneCount, PlaneHeight = source.PlaneHeight };
        foreach (var region in source.Regions.Values)
        {
            var target = result.GetOrCreateRegion(region.Coord);
            for (int plane = 0; plane < source.PlaneCount; plane++) target.Planes[plane] = region.Planes[plane];
            target.Objects.AddRange(region.Objects.Where(o => o.Id == objectId));
        }
        return result;
    }

    static bool SameMarker(MapEffectiveMarker a, MapEffectiveMarker b) => a.PayloadVersion == b.PayloadVersion && a.Id == b.Id && a.Name == b.Name &&
        a.Position == b.Position && a.YawRadians == b.YawRadians && a.Role == b.Role && a.Enabled == b.Enabled && a.InstanceId == b.InstanceId &&
        a.ArchetypeId == b.ArchetypeId && a.HeightProvenance == b.HeightProvenance && a.Tags.SequenceEqual(b.Tags);
    static bool Near(double a, double b) => Math.Abs(a - b) <= PositionTolerance;

    sealed class Evidence
    {
        readonly List<string> _failures = new();
        readonly List<OracleComparisonFailure> _classified = new();
        readonly List<object> _details = new();
        readonly List<object> _records = new();
        readonly Dictionary<string, int> _checks = new(StringComparer.Ordinal);
        internal OracleResourceChecks Resource(string kind, string keyClass, string prefix = "") => new(CheckClassified, kind, keyClass, prefix);
        internal void Check(string category, bool ok, object detail) => CheckClassified(category, ok, detail, new("Scene", "scene", category));
        void CheckClassified(string category, bool ok, object detail, SceneFailureClassification classification)
        {
            _checks[category] = _checks.GetValueOrDefault(category) + 1;
            if (ok) return;
            _failures.Add(category); _details.Add(new { category, detail });
            _classified.Add(new(category, detail, classification));
        }
        internal void Record(string category, object detail) => _records.Add(new { category, detail });
        internal ShippedMarkerReport Report(int heads, int sourceMarkers) => new(_failures.AsReadOnly(), JsonSerializer.Serialize(new
        { heads, sourceMarkers, checks = _checks, failures = _details, records = _records }), _classified.AsReadOnly());
    }
}
