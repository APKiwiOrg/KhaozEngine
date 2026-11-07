using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapMembershipClassificationRefusalTests
{
    [Theory]
    [InlineData(false, MapMembershipStatus.MissingGeometry, "LegacyTileWorld requires ImportedMetreCentimetre")]
    [InlineData(true, MapMembershipStatus.Invalid, "footprint 'footprint': legacy recipe: surface")]
    public void LegacySurfaceAdmission_DistinguishesCompilerMetadataFromTheLegacyRecipe(bool recipe, MapMembershipStatus expected, string detail)
    {
        // W6-C1 review regression: the compiler's frame refusal was never an Invalid recipe projection.
        var set = MapOccupiedPartitionRefusalTests.Scene(0);
        var original = set.Refs.Single();
        var patch = set.Patches.Values.Single();
        var footprint = patch.Records.OfType<MapSpaceFootprint>().Single();
        int index = patch.Records.FindIndex(r => r.Id == footprint.Id);
        if (recipe) patch.Records[index] = footprint with { Lower = footprint.Lower with { Kind = MapBoundKind.LegacyExteriorV1 } };
        else set.Refs[0] = original with { PresencePolicy = MapPresencePolicy.LegacyTileWorld };
        var point = new MapFramePoint(WorldFrame.Origin, new(0.5f, 1, 0.5f));
        var result = new MapSpaceMembership(MapScopedSurfaces.CompleteView(set)).Query(point);
        Assert.Equal(expected, result.Status);
        Assert.Null(result.SpaceId);
        Assert.Equal(detail, result.Detail);
        patch.Records[index] = footprint;
        set.Refs[0] = original;
        Assert.Equal(MapMembershipStatus.Resolved, new MapSpaceMembership(MapScopedSurfaces.CompleteView(set)).Query(point).Status);
    }

}
