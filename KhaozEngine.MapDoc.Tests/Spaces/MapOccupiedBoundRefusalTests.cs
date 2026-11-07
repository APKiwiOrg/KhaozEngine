using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapOccupiedBoundRefusalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOccupiedBound_NamesItsSpaceAndBoundary(bool upper)
    {
        var (view, _) = BoundFaceContextFixtures.EightByEight();
        var key = new MapPatchKey("floor8", 0, 0);
        MapBoundFaceContext context = MapBoundFaceContext.Prepare(view, [new(key, 0)], 1, 2, null, out _)!;
        var faces = context.Faces(new(key, 0));
        var boundary = new MapOccupiedBoundary("floor8", "canonical-floor", "boundary-digest");
        var point = MapWideXz.From(new MapExactXz(new(0, 1), new(0, 1)));
        var piece = new MapOccupiedPiece("room8", "fragment", boundary, boundary, [],
            upper ? faces : [], upper ? [] : faces, null, null);

        MapDocumentException error = Assert.Throws<MapDocumentException>(() => Height(piece));
        Assert.Equal(MapDocumentRefusal.PatchUnavailable, error.Reason);
        Assert.Equal("room8", error.SpaceId);
        Assert.Equal("floor8", error.OwnerId);
        Assert.Equal(upper ? "Upper" : "Lower", error.FieldPath);

        MapOccupiedPiece repaired = piece with { LowerFaces = faces, UpperFaces = faces };
        Assert.Equal(MapWideRational.From(new MapExactValue(0, 1)), Height(repaired));
        MapWideRational? Height(MapOccupiedPiece value) => upper ? value.Top(point) : value.Bottom(point);
    }

    [Fact]
    public void PrismRecordBudget_ReportsMeasuredCountAndAcceptsTheLimit()
    {
        var resolved = PrefabFixtures.Effective(PrefabFixtures.Create());
        MapScopedSurfaces view = resolved.CompleteView();
        MapPrismDomain prism = MapSpaceGeometry.Records(view).OfType<MapPrismDomain>().Single();
        int limit = view.Scope.Limits.MaxRecordReads;
        MapPrismDomain[] prisms = Enumerable.Repeat(prism, limit + 1).ToArray();
        var outside = new MapExactXz(new(100, 1), new(100, 1));
        MapDocumentException error = Assert.Throws<MapDocumentException>(() => MapOccupiedPartitions.At(view, outside, [], prisms));
        Assert.Equal(MapDocumentRefusal.WorkCapacityExceeded, error.Reason);
        Assert.Equal("MaxRecordReads", error.Budget);
        Assert.Equal(limit + 1L, error.ActualCount);
        Assert.Equal(limit, error.Limit);
        // Repeated domain observations are deduplicated by the existing partition traversal.
        Assert.Empty(MapOccupiedPartitions.At(view, outside, [], prisms.Take(limit).ToArray()));
    }
}
