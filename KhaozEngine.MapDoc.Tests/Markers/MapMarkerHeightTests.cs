using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Markers;

public sealed class MapMarkerHeightTests
{
    // Captured from the released digest on the accepted Task 1 head, before any Task 2 product edit.
    const string BasicAnnotationDigest = "aa710fcabca84ac29c1c2a2595a379c8c69b5d8354c0b12f625a147cddbe8265";
    const string PrefabBasicAnnotationDigest = "209f69c59b44046c602bd8431c29502d8bfbc0f64f7c7dcf1b57a404e8d6ad36";

    [Fact]
    public void StackedCaveMarkerSupport_DoesNotSnapToSurface()
    {
        foreach (bool tiled in new[] { false, true })
        {
            MarkerFixture fixture = MarkerFixtures.Reload(MarkerFixtures.Stacked(), tiled);
            MapEffectiveResources resources = MarkerFixtures.Effective(fixture);
            foreach (string id in new[] { "stacked-surface", "stacked-space" })
            {
                MapEffectiveMarker marker = MapEffectiveAnnotations.Markers(resources).Single(m => m.Id == id);
                Assert.Equal(20f, marker.Position.Y.ToSingle());
                Assert.Equal("floor-2", marker.HeightProvenance!.Face!.Value.OwnerId);
                Assert.Equal(MapMarkerHeightMode.Support, marker.HeightProvenance.Mode);
                Assert.Equal(20f, marker.HeightProvenance.WorldY);
                Assert.Equal(MapSupportStatus.Supported, resources.MarkerSupports.Single(s => s.MarkerId == marker.Id).Support.Status);
                Assert.Equal("level-2", marker.HeightProvenance.SpaceId);
                Assert.Equal(new MapExactPoint(new(1, 2), new(20, 1), new(1, 2)), marker.Position);
                // The authored world binding is the provenance request, and the stored point still carries no height.
                MapLocalMarkerDoc stored = fixture.Document.BuildingIntent!.Markers.Single(m => m.Id == id);
                Assert.Equal(stored.SupportBinding, marker.HeightProvenance.Binding);
                Assert.Equal(0u, BitConverter.SingleToUInt32Bits(resources.BuildingIntent.Markers.Single(m => m.Id == id).Position.Y));
            }
            Assert.Equal(new[] { "stacked-disabled", "stacked-space", "stacked-surface" }, resources.MarkerSupports.Select(s => s.MarkerId));
            // A disabled Support marker still resolves its binding and publishes its witness row.
            MapEffectiveMarker disabled = MapEffectiveAnnotations.Markers(resources).Single(m => m.Id == "stacked-disabled");
            Assert.False(disabled.Enabled);
            Assert.Equal(new MapExactPoint(new(3, 2), new(20, 1), new(3, 2)), disabled.Position);
            Assert.Equal(new MapMarkerHeightProvenance(MapMarkerHeightMode.Support, MarkerFixtures.SpaceHeight().Binding,
                disabled.HeightProvenance!.Face, "level-2", null, 20f), disabled.HeightProvenance);
            Assert.Equal("floor-2", disabled.HeightProvenance.Face!.Value.OwnerId);
            Assert.Equal(MapSupportStatus.Supported, resources.MarkerSupports.Single(s => s.MarkerId == "stacked-disabled").Support.Status);
        }
    }

    [Fact]
    public void SlopedSupport_EffectiveYEqualsQueryWorldY()
    {
        MarkerFixture fixture = MarkerFixtures.Sloped();
        MapSupportResult direct = new MapSupportQuery(MarkerFixtures.Effective(fixture).CompleteView())
            .SampleSurfaceExact(MarkerFixtures.Exact(0.3f, 0, 0.7f), "floor-2");

        Assert.Equal(MapSupportStatus.Supported, direct.Status);
        Assert.NotEqual(MathF.Round(direct.WorldY * 64f) / 64f, direct.WorldY);
        foreach (bool tiled in new[] { false, true })
        {
            MarkerFixture stored = MarkerFixtures.Reload(fixture, tiled);
            var markers = MapEffectiveAnnotations.Markers(MarkerFixtures.Effective(stored));
            MapEffectiveMarker slope = markers.Single(m => m.Id == "slope");
            MapEffectiveMarker flat = markers.Single(m => m.Id == "flat");
            MapEffectiveMarker reloadedSlope = MapEffectiveAnnotations.Markers(MarkerFixtures.Effective(MarkerFixtures.Reload(stored, false)))
                .Single(m => m.Id == "slope");

            Assert.Equal(MapExactValue.FromSingle(direct.WorldY), slope.Position.Y);
            Assert.Equal(direct.WorldY, slope.HeightProvenance!.WorldY);
            Assert.Equal(direct.Face, slope.HeightProvenance.Face);
            Assert.Equal(slope.Position.Y, reloadedSlope.Position.Y);
            Assert.Equal(slope.HeightProvenance, reloadedSlope.HeightProvenance);
            Assert.Null(flat.HeightProvenance!.WorldY);
            Assert.Equal(new MapMarkerHeightProvenance(MapMarkerHeightMode.Explicit, null, null, null, null, null), flat.HeightProvenance);
            Assert.Equal(MapExactValue.FromSingle(12.25f), flat.Position.Y);
        }
    }

    [Fact]
    public void PayloadOneAnnotationDigest_IsUnchanged()
    {
        MapEffectiveResources basic = MarkerFixtures.Effective(MarkerFixtures.Create("basic"));
        MapEffectiveResources prefab = MarkerFixtures.Effective(MarkerFixtures.Create("prefab-basic"));

        Assert.Equal(BasicAnnotationDigest, basic.AnnotationDigest);
        Assert.Equal(PrefabBasicAnnotationDigest, prefab.AnnotationDigest);
        foreach (MapEffectiveMarker marker in new[] { basic, prefab }.Select(r => Assert.Single(MapEffectiveAnnotations.Markers(r))))
        {
            Assert.Null(marker.HeightProvenance);
            Assert.Null(marker.ArchetypeId);
        }
        Assert.Empty(basic.MarkerSupports);

        // The same point as payload-2 Explicit digests under the payload-2 scheme.
        MarkerFixture two = MarkerFixtures.Create("basic");
        two.Document.BuildingIntent = new(1, [], [], [], new[] { MapMarkerHeights.Apply(MarkerFixtures.BasicMarker(), MarkerFixtures.ExplicitHeight()) });
        MapEffectiveResources twoResources = MarkerFixtures.Effective(two);
        MapEffectiveMarker explicitMarker = Assert.Single(MapEffectiveAnnotations.Markers(twoResources));
        Assert.NotEqual(BasicAnnotationDigest, twoResources.AnnotationDigest);
        Assert.NotEqual("", twoResources.AnnotationDigest);
        Assert.Equal(Assert.Single(MapEffectiveAnnotations.Markers(basic)).Position, explicitMarker.Position);
        Assert.Equal(MapMarkerHeightMode.Explicit, explicitMarker.HeightProvenance!.Mode);

        // The scheme name is what switches: the same rows digest to /2 and would differ under /1.
        Assert.Equal(Scheme("kemap/effective-annotations/1", basic), basic.AnnotationDigest);
        Assert.Equal(Scheme("kemap/effective-annotations/1", prefab), prefab.AnnotationDigest);
        Assert.Equal(Scheme("kemap/effective-annotations/2", twoResources), twoResources.AnnotationDigest);
        Assert.NotEqual(Scheme("kemap/effective-annotations/1", twoResources), twoResources.AnnotationDigest);
    }

    // The released annotation rows under a named scheme, read back from the published effective views.
    static string Scheme(string name, MapEffectiveResources resources)
    {
        MapBuildingIntent intent = resources.BuildingIntent;
        return MapPrefabJson.Digest(name, new
        {
            Roofs = intent.Roofs,
            Links = intent.RoofLinks,
            Groups = intent.RoofGroups,
            Lights = MapEffectiveAnnotations.Lights(resources),
            Markers = MapEffectiveAnnotations.Markers(resources),
        });
    }

    [Fact]
    public void ProvenanceAndDigest_IgnoreSnapshotWitness()
    {
        MarkerFixture fixture = MarkerFixtures.Stacked();
        MapEffectiveResources expanded = MapPrefabResolver.Expand(fixture.Document, fixture.Assets);
        MapDocument reloaded = MapDocumentFile.LoadText(MapDocumentFile.SaveText(fixture.Document));
        MapEffectiveResources resolved = MapResolverV2.Resolve(reloaded, fixture.Assets, MapDocumentSurfaceSource.Capture(reloaded),
            MarkerFixtures.Options()).Document.EffectiveResources!;

        Assert.Equal(MapEffectiveAnnotations.Markers(expanded).Select(m => (m.Id, m.Position, m.HeightProvenance)),
            MapEffectiveAnnotations.Markers(resolved).Select(m => (m.Id, m.Position, m.HeightProvenance)));
        Assert.All(MapEffectiveAnnotations.Markers(expanded), m => Assert.NotNull(m.HeightProvenance!.WorldY));
        Assert.Equal(expanded.AnnotationDigest, resolved.AnnotationDigest);
        Assert.Equal(expanded.MarkerSupports.Select(s => s.MarkerId), resolved.MarkerSupports.Select(s => s.MarkerId));
        Assert.All(expanded.MarkerSupports.Zip(resolved.MarkerSupports), pair =>
            Assert.NotEqual(pair.First.Support.Witness.SnapshotId, pair.Second.Support.Witness.SnapshotId));
    }

    [Fact]
    public void SupportReads_PassAtExactLimitAndRefuseOverLimit()
    {
        MarkerFixture fixture = MarkerFixtures.Stacked();
        var candidates = MapMarkerExpansion.Capture(fixture.Document, fixture.Assets);
        var source = MapDocumentSurfaceSource.Capture(fixture.Document);
        string[] sha = fixture.Assets.Roots.Select(r => r.Sha256).ToArray();

        var resolution = MapMarkerResolution.Resolve(candidates, source, sha, new MapQueryLimits());
        var (markers, supports) = (resolution.Markers, resolution.Supports);
        Assert.Equal(MapEffectiveAnnotations.Markers(MarkerFixtures.Effective(fixture)).Select(m => (m.Position, m.HeightProvenance)),
            markers.Select(m => (m.Position, m.HeightProvenance)));
        // Each per-point scope holds the eight stacked floor and eight ceiling patches, so sixteen is the exact limit.
        var exact = MapMarkerResolution.Resolve(candidates, source, sha, new MapQueryLimits(MaxCandidatePatches: 16));
        Assert.Equal(supports.Select(s => s.Support.WorldY), exact.Supports.Select(s => s.Support.WorldY));
        var error = Assert.Throws<MapDocumentException>(() => MapMarkerResolution.Resolve(candidates, source, sha, new MapQueryLimits(MaxCandidatePatches: 15)));
        Assert.Contains("support status CapacityExceeded", error.Message, StringComparison.Ordinal);

        // The captured document source visits two index pages for each of the sixteen surfaces in a per-point scope.
        var pages = MapMarkerResolution.Resolve(candidates, source, sha, new MapQueryLimits(MaxPageReads: 32));
        Assert.Equal(supports.Select(s => s.Support.WorldY), pages.Supports.Select(s => s.Support.WorldY));
        error = Assert.Throws<MapDocumentException>(() => MapMarkerResolution.Resolve(candidates, source, sha, new MapQueryLimits(MaxPageReads: 31)));
        Assert.Contains("support status CapacityExceeded", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AsymmetricSpaceSearch_SeatsPlacementAndMarkerAtOneHeight()
    {
        // The floor at 20 is 0.6 below the reference height: a 0.7 search below finds it and a 0.1 search above does not need to.
        MapSupportBinding binding = MarkerFixtures.SpaceBinding("level-2", 20.6f, 0.7f, 0.1f);
        MarkerFixture both = Seated(binding, placement: true, marker: true);
        MapEffectiveResources resources = MarkerFixtures.Effective(both);
        MapPlacementSupport seat = Assert.Single(resources.PlacementSupports);
        MapEffectiveMarker marker = MapEffectiveAnnotations.Markers(resources).Single(m => m.Id == "seated");
        Assert.Equal(MapSupportStatus.Supported, seat.Support.Status);
        Assert.Equal(20f, seat.Support.WorldY);
        Assert.Equal(seat.Support.WorldY, resources.Placements.Single(p => p.PlacementId == "seat").Transform.Position.Y);
        Assert.Equal(MapExactValue.FromSingle(seat.Support.WorldY), marker.Position.Y);
        Assert.Equal(seat.Support.WorldY, marker.HeightProvenance!.WorldY);
        Assert.Equal(seat.Support.Face, marker.HeightProvenance.Face);

        // Swapped extents put the floor 0.5 below the 0.1 lower bound, so both seats refuse it the same way.
        MapSupportBinding swapped = binding with { SearchBelow = 0.1f, SearchAbove = 0.7f };
        var placementError = Assert.Throws<MapDocumentException>(() => MarkerFixtures.Effective(Seated(swapped, placement: true, marker: false)));
        var markerError = Assert.Throws<MapDocumentException>(() => MarkerFixtures.Effective(Seated(swapped, placement: false, marker: true)));
        Assert.Contains("Placement 'seat' support status NoSupport", placementError.Message, StringComparison.Ordinal);
        Assert.Contains("Marker 'seated' support status NoSupport", markerError.Message, StringComparison.Ordinal);
    }

    static MarkerFixture Seated(MapSupportBinding binding, bool placement, bool marker)
    {
        MarkerFixture fixture = MarkerFixtures.Stacked();
        fixture.Document.BuildingIntent = new(1, [], [], [], marker ? new[] { MarkerFixtures.Marker("seated", 0.5f, 0.5f, new(null, binding)) } : []);
        if (placement)
            fixture.Document.Placements.Add(new MapPlacement { Id = "seat", Kind = "scenery", AssetId = "tree", X = 0.5f, Z = 0.5f, Scale = 1, SupportBinding = binding });
        return fixture;
    }

    [Theory]
    [InlineData("surface")]
    [InlineData("space")]
    public void PrefabChildSupportMarker_UnderNonzeroFrameY_ReportsSupportHeight(string form)
    {
        MarkerFixture fixture = form == "surface" ? MarkerFixtures.TransferredSurface() : MarkerFixtures.TransferredSpace();
        MapEffectiveResources resources = MarkerFixtures.Effective(fixture);
        AssertSupportHeight(resources, "a/" + form + "-marker", form);
    }

    [Theory]
    [InlineData("surface")]
    [InlineData("space")]
    public void FramedStandaloneSupportMarker_ReportsSupportHeight(string form)
    {
        MarkerFixture fixture = form == "surface" ? MarkerFixtures.TransferredSurface() : MarkerFixtures.TransferredSpace();
        string id = "a/" + form + "-marker";
        MapEffectiveMarker placed = MapEffectiveAnnotations.Markers(MarkerFixtures.Effective(fixture)).Single(m => m.Id == id);
        MapDocument unpacked = MapPrefabEdits.Prepare(fixture.Document, fixture.Assets, new MapUnpackPrefab("a")).After;

        Assert.Contains(unpacked.NativeResourceTransforms!, p => p.Kind == MapPrefabChildKind.Marker && p.OwnerId == id);
        Assert.Contains(unpacked.BuildingIntent!.Markers, m => m.Id == id);
        MapEffectiveResources resources = MarkerFixtures.Effective(fixture with { Document = unpacked });
        MapEffectiveMarker standalone = AssertSupportHeight(resources, id, form);
        Assert.Equal((placed.Position, placed.HeightProvenance, placed.InstanceId), (standalone.Position, standalone.HeightProvenance, standalone.InstanceId));
    }

    static MapEffectiveMarker AssertSupportHeight(MapEffectiveResources resources, string id, string form)
    {
        MapEffectiveMarker marker = MapEffectiveAnnotations.Markers(resources).Single(m => m.Id == id);
        MapLocalMarkerDoc entry = resources.BuildingIntent.Markers.Single(m => m.Id == id);
        // The effective intent keeps the canonical carrier, so no sampled height appears as a second authored height.
        Assert.Equal(0u, BitConverter.SingleToUInt32Bits(entry.Position.Y));
        Assert.Equal((marker.Position.X.ToSingle(), marker.Position.Z.ToSingle()), (entry.Position.X, entry.Position.Z));
        // The transferred frame's yaw composed independently with the child's local yaw.
        float yaw = (float)((form == "surface" ? MarkerFixtures.SurfaceChild() : MarkerFixtures.SpaceChild()).Value.YawRadians + (double)0.371f);
        Assert.Equal((yaw, yaw), (entry.YawRadians, marker.YawRadians));
        MapSupportBinding expected = form == "surface"
            ? MarkerFixtures.SurfaceBinding("a/floor")
            : MarkerFixtures.SpaceBinding("a/room", Frozen(-450d + 0.5d * 1.2f), 1.2f, 1.2f);
        Assert.Equal(expected, entry.SupportBinding);
        Assert.Equal(expected, marker.HeightProvenance!.Binding);

        var view = resources.CompleteView();
        var world = new MapExactPoint(marker.Position.X, MapExactValue.FromSingle(expected.ReferenceY ?? 0), marker.Position.Z);
        MapSupportResult direct = form == "surface"
            ? new MapSupportQuery(view).SampleSurfaceExact(world, "a/floor")
            : new MapSupportQuery(view).SelectExact(new(MapEffectiveGeometry.Presentation(view, world), null, "a/room", null, 1.2f, 1.2f, null), world);
        Assert.Equal(MapSupportStatus.Supported, direct.Status);
        Assert.Equal(-450f, direct.WorldY);
        Assert.Equal(MapExactValue.FromSingle(direct.WorldY), marker.Position.Y);
        Assert.Equal(direct.WorldY, marker.HeightProvenance.WorldY);
        Assert.Equal(direct.Face, marker.HeightProvenance.Face);
        Assert.Equal("a/room", marker.HeightProvenance.SpaceId);
        Assert.Equal(MapMarkerHeightMode.Support, marker.HeightProvenance.Mode);
        Assert.Equal(MapSupportStatus.Supported, resources.MarkerSupports.Single(s => s.MarkerId == id).Support.Status);
        return marker;
    }

    // The 2^-20 metre freeze of a composed height, then its binary32 value, computed here from the frame values.
    static float Frozen(double metres) => (float)(Math.Round(metres * 1048576, MidpointRounding.ToEven) / 1048576);
}
