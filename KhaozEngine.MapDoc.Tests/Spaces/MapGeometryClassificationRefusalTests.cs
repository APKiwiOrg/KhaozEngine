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

public sealed class MapGeometryClassificationRefusalTests
{
    [Fact]
    public void WaterPairBudget_KeepsCapacityThroughAnInvalidRefinementProjection()
    {
        // W6-C1 review regression: refinement's Invalid projection must retain the capacity cause.
        var view = MapOccupiedPartitionRefusalTests.View(MapOccupiedPartitionRefusalTests.Scene(0));
        var min = new MapExactXz(new(0, 1), new(0, 1));
        var max = new MapExactXz(new(1, 1), new(1, 1));
        var result = MapOccupiedPartitions.BuildWide(view, min, max, new(MaxPairChecks: 1), false, new(new()));
        Assert.Equal(MapRefinementStatus.CapacityExceeded, result.Status);
        Assert.Empty(result.Pieces);
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, result.Finding!.Reason);
        Assert.Equal("WaterDomainPairChecks", result.Finding.Budget);
        Assert.Equal(2, result.Finding.ActualCount);
        Assert.Equal(1, result.Finding.Limit);
        Assert.Equal(result.Finding.Message, result.Detail);
        Assert.Equal(MapRefinementStatus.Complete,
            MapOccupiedPartitions.BuildWide(view, min, max, new(MaxPairChecks: 2), false, new(new())).Status);
    }

    [Fact]
    public void DomainPrefix_KeepsContainedInvalidDetailUnverified()
    {
        var frame = MapLatticeFrame.ImportedMetreCentimetre;
        var domain = new MapWaterDomainDoc(frame with { RowDirection = (MapRowDirection)255 }, [new(0, 0, 1, 1)], []);
        var error = Assert.Throws<MapDocumentException>(() => MapWaterDomain.Compile(domain));
        Assert.Equal(MapDocumentRefusal.SchemaEnumUnsupported, error.Reason);
        Assert.True(MapGeometryRefusals.Invalid(error));
        Assert.False(MapWaterContainment.Invalid(error));
        Assert.NotEmpty(MapWaterDomain.Compile(domain with { Frame = frame }).Pieces);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("capacity exceeded")]
    [InlineData("domain work exceeds")]
    [InlineData("not representable")]
    [InlineData("invalid")]
    [InlineData("legacy recipe")]
    public void MissingRecordKeyword_KeepsGeometryQueriesMissing(string recordId)
    {
        var set = MapOccupiedPartitionRefusalTests.Scene(0);
        var patch = set.Patches.Values.Single();
        var footprint = patch.Records.OfType<MapSpaceFootprint>().Single();
        int index = patch.Records.FindIndex(r => r.Id == footprint.Id);
        patch.Records[index] = footprint with { Space = new(recordId, patch.Key) };
        var view = MapScopedSurfaces.CompleteView(set);
        var result = MapOccupiedPartitions.BuildWide(view, new(new(0, 1), new(0, 1)), new(new(1, 1), new(1, 1)), new(), false);
        Assert.Equal(MapRefinementStatus.MissingGeometry, result.Status);
        Assert.Equal(MapDocumentRefusal.PatchUnavailable, result.Finding!.Reason);
        Assert.Equal(recordId, result.Finding.RecordId);
        var error = Assert.Throws<MapDocumentException>(() => MapSpaceGeometry.Record<MapSpaceDoc>(view, new(recordId, patch.Key)));
        Assert.Equal(MapRelationStatus.MissingGeometry, MapSpaceRelations.RefusalStatus(error));
        var point = new MapFramePoint(WorldFrame.Origin, new(0.5f, 1, 0.5f));
        Assert.Equal(MapMembershipStatus.MissingGeometry, new MapSpaceMembership(view).Query(point).Status);
        patch.Records[index] = footprint;
        Assert.Equal(MapMembershipStatus.Resolved, new MapSpaceMembership(MapScopedSurfaces.CompleteView(set)).Query(point).Status);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("budget")]
    [InlineData("limit exceeded")]
    public void UnavailableDemandKeyword_KeepsFoliageUnavailable(string surfaceId)
    {
        var set = MapOccupiedPartitionRefusalTests.Scene(0);
        var view = MapScopedSurfaces.CompleteView(set);
        var error = Assert.Throws<MapDocumentException>(() => MapBoundFaceContext.Prepare(view,
            [new(new(surfaceId, 0, 0), 0)], 1, 2, null, out _));
        Assert.Equal(MapDocumentRefusal.ReferenceMissing, error.Reason);
        Assert.Equal(MapFoliageStatus.Unavailable, MapFoliageSupport.Classify(error));
        Assert.NotNull(MapBoundFaceContext.Prepare(view, [new(set.Patches.Keys.Single(), 0)], 1, 2, null, out _));
    }
}
