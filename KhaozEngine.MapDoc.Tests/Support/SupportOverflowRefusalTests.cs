using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Markers;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Support;

public sealed class SupportOverflowRefusalTests
{
    [Fact]
    public void MarkerSeat_ReportsItsIdentityWhenTheExactReferenceHeightOverflows()
    {
        var fixture = MarkerFixtures.Stacked();
        var marker = fixture.Document.BuildingIntent!.Markers.Single(m => m.Id == "stacked-space") with
        { SupportBinding = new(MapSupportBindingKind.Space, null, "level-0", 0, 0.5f, 0.5f) };
        // 2^-80 requires an exact denominator larger than signed int64, without float cancellation.
        var refused = marker with { SupportBinding = marker.SupportBinding! with { ReferenceY = MathF.ScaleB(1, -80) } };
        var source = MapDocumentSurfaceSource.Capture(fixture.Document);
        var error = Assert.Throws<MapDocumentException>(() => MapMarkerResolution.Resolve(
            [new(refused, null, null)], source, [], new()));
        Assert.Equal(MapDocumentRefusal.GeometryNotRepresentable, error.Reason);
        Assert.Equal(marker.Id, error.MarkerId);
        Assert.Equal(MapSupportStatus.NotRepresentable, error.SupportStatus);
        fixture.Document.BuildingIntent = new(1, [], [], [], [refused]);
        string before = MapDocumentFile.SaveText(fixture.Document);
        Assert.Equal(MapFoliageStatus.Invalid, MapFoliageGenerationContext.Capture(fixture.Document, fixture.Assets).Status);
        Assert.Equal(before, MapDocumentFile.SaveText(fixture.Document));
        fixture.Document.BuildingIntent = new(1, [], [], [], [marker]);
        Assert.Single(MapMarkerResolution.Resolve([new(marker, null, null)], source, [], new()).Supports);
        Assert.Equal(MapFoliageStatus.Complete, MapFoliageGenerationContext.Capture(fixture.Document, fixture.Assets).Status);
    }

    [Fact]
    public void PlacementSeat_ReportsItsIdentityWhenTheExactCoordinateOverflows()
    {
        var fixture = MarkerFixtures.Stacked();
        // 2^-80 is a normal binary32 value with an exact denominator larger than signed int64.
        string assetId = fixture.Assets.Assets.First().Id;
        var placement = new MapPlacement
        {
            Id = "seat",
            Kind = assetId,
            AssetId = assetId,
            X = MathF.ScaleB(1, -80),
            Z = 0.5f,
            SupportBinding = new(MapSupportBindingKind.Surface, "floor-2", null, null, null, null)
        };
        fixture.Document.Placements.Add(placement);
        var error = Assert.Throws<MapDocumentException>(() => MapResolverV2.ResolvePlacements(fixture.Document, fixture.Assets, [placement], new()));
        Assert.Equal(MapDocumentRefusal.GeometryNotRepresentable, error.Reason);
        Assert.Equal(placement.Id, error.PlacementId);
        Assert.Equal(MapSupportStatus.NotRepresentable, error.SupportStatus);
        placement.X = 0.5f;
        Assert.Single(MapResolverV2.ResolvePlacements(fixture.Document, fixture.Assets, [placement], new()));
    }
}
