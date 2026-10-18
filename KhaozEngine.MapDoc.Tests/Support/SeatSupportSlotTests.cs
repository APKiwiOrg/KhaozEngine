using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Markers;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Support;

/// <summary>The shared seat query on sparse floors. A floor the per-point scope captures, through records incident to
/// another patch or through an empty neighbouring slot, must have evidence for the point's own slot, so a valid point
/// seats on its named owner and the effective and native resolution paths agree.</summary>
public sealed class SeatSupportSlotTests
{
    [Theory]
    [InlineData("incident", "start", -452.4f, "exterior")]
    [InlineData("neighbour", "cave-bound", -39.375f, "high-floor")]
    public void SparseFloorSeat_ResolvesMarkerAndPlacementOnBothPaths(string variant, string markerId, float worldY, string owner)
    {
        MarkerFixture fixture = MarkerFixtures.SeatSlots(variant);
        MapEffectiveResources expanded = MapPrefabResolver.Expand(fixture.Document, fixture.Assets);
        MapEffectiveResources native = MapNativeResolution.Resolve(fixture.Document, fixture.Assets, MarkerFixtures.Options()).EffectiveResources!;

        foreach (MapEffectiveResources resources in new[] { expanded, native })
        {
            MapEffectiveMarker marker = MapEffectiveAnnotations.Markers(resources).Single(m => m.Id == markerId);
            Assert.Equal(MapExactValue.FromSingle(worldY), marker.Position.Y);
            Assert.Equal(owner, marker.HeightProvenance!.Face!.Value.OwnerId);
            Assert.Equal(worldY, marker.HeightProvenance.WorldY);
            MapSupportResult seat = resources.PlacementSupports.Single(s => s.PlacementId == "seat-probe").Support;
            Assert.Equal(MapSupportStatus.Supported, seat.Status);
            Assert.Equal(owner, seat.Face!.Value.OwnerId);
            Assert.Equal(worldY, resources.Placements.Single(p => p.PlacementId == "seat-probe").Transform.Position.Y);
        }
        Assert.Equal(MapEffectiveAnnotations.Markers(expanded).Select(m => (m.Id, m.Position, m.HeightProvenance)),
            MapEffectiveAnnotations.Markers(native).Select(m => (m.Id, m.Position, m.HeightProvenance)));
        Assert.Equal(expanded.AnnotationDigest, native.AnnotationDigest);
    }
}
