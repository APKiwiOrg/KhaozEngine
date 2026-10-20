using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.Tests.MapDoc.Markers;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

/// <summary>Markers are annotations. They never widen world bounds, so a marker-only edit leaves every static, the world
/// extent and every navigation capture identity unchanged, while residency still installs the marker's point.</summary>
public sealed class MapMarkerBoundsTests
{
    [Theory]
    [InlineData(500f)]
    [InlineData(-500f)]
    public void MarkerOutsideGeometryExtent_LeavesStaticsBoundsAndNavIdentities(float y)
    {
        MarkerFixture fixture = MarkerFixtures.Stacked();
        MapBuiltWorld before = Build(fixture);
        var grids = new MapWorldGrids(before.TileSize, 1, 4, Vector2.Zero);
        var options = new MapNavTileOptions(2f, "profile-a", "legacy-stepper");
        Assert.True(y > before.Bounds.MaxY || y < before.Bounds.MinY, "the marker lies outside every static");

        MapLocalMarkerDoc outlier = MapMarkerHeights.Apply(new(1, "outlier", "outlier", new Vector3(0.5f, 0, 0.5f), 0, "guard-role", true,
            new[] { "outside" }), new(y, null));
        MapPrefabEditResult edit = MapMarkerEdits.Prepare(fixture.Document, fixture.Assets, new MapSetMarker(outlier));
        Assert.Equal(MapNativeInvalidation.Residency, edit.Effects.Invalidates);
        MapBuiltWorld after = Build(fixture with { Document = edit.After });

        Assert.Equal(before.Statics.Select(s => (s.OwnerId, s.Digest, s.Bounds)), after.Statics.Select(s => (s.OwnerId, s.Digest, s.Bounds)));
        Assert.Equal(before.Bounds, after.Bounds);
        Assert.Equal(MapNavTiling.Partition(before, grids, options).Select(t => (t.Coord, t.Bounds, t.CaptureIdentity)),
            MapNavTiling.Partition(after, grids, options).Select(t => (t.Coord, t.Bounds, t.CaptureIdentity)));
        MapResidencyEntry entry = Assert.Single(MapResidencyOwnership.BuildAnnotations(after, grids), e => e.OwnerId == "marker:outlier");
        Assert.Equal(new[] { new MapTileCoord(0, 0) }, entry.Membership);
        Assert.Equal((double)y, entry.WorldBounds.MinY, 3);
        Assert.Contains("marker:outlier", MapResidencyOwnership.Affected(before, after, grids, edit.Effects).Owners);
    }

    static MapBuiltWorld Build(MarkerFixture fixture) => MapWorldBuilder.Build(MapDocumentFile.LoadText(MapDocumentFile.SaveText(fixture.Document)),
        fixture.Assets, new(MarkerFixtures.Options(), "public-marker-bounds/1", new()));
}
