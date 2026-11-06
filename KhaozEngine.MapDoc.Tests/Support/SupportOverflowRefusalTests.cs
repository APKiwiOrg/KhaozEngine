using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Markers;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Support;

public sealed class SupportOverflowRefusalTests
{
    [Fact]
    public void MarkerSeat_ReportsItsIdentityWhenTheExactReferenceHeightOverflows()
    {
        var fixture = MarkerFixtures.Stacked();
        var marker = fixture.Document.BuildingIntent!.Markers.Single(m => m.Id == "stacked-space");
        // 2^-80 requires an exact denominator larger than signed int64, without float cancellation.
        var refused = marker with { SupportBinding = marker.SupportBinding! with { ReferenceY = MathF.ScaleB(1, -80) } };
        var source = MapDocumentSurfaceSource.Capture(fixture.Document);
        var error = Assert.Throws<MapDocumentException>(() => MapMarkerResolution.Resolve(
            [new(refused, null, null)], source, [], new()));
        Assert.Equal(MapDocumentRefusal.GeometryNotRepresentable, error.Reason);
        Assert.Equal(marker.Id, error.MarkerId);
        Assert.Equal(MapSupportStatus.NotRepresentable, error.SupportStatus);
        Assert.Single(MapMarkerResolution.Resolve([new(marker, null, null)], source, [], new()).Supports);
    }

    [Fact]
    public void PlacementSeat_ReportsItsIdentityWhenTheExactCoordinateOverflows()
    {
        var fixture = MarkerFixtures.Stacked();
        // 2^-80 is a normal binary32 value with an exact denominator larger than signed int64.
        var placement = new MapPlacement { Id = "seat", Kind = "item", AssetId = "item", X = MathF.ScaleB(1, -80), Z = 0.5f,
            SupportBinding = new(MapSupportBindingKind.Surface, "floor-2", null, null, null, null) };
        var error = Assert.Throws<MapDocumentException>(() => MapResolverV2.ResolvePlacements(fixture.Document, fixture.Assets, [placement]));
        Assert.Equal(MapDocumentRefusal.GeometryNotRepresentable, error.Reason);
        Assert.Equal(placement.Id, error.PlacementId);
        Assert.Equal(MapSupportStatus.NotRepresentable, error.SupportStatus);
        placement.X = 0.5f;
        Assert.Single(MapResolverV2.ResolvePlacements(fixture.Document, fixture.Assets, [placement]));
    }
}
