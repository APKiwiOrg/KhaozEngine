using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapConversionRefusalTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(65, 64)]
    public void Subdivision_RefusesOutsideEachLimitAndAcceptsTheLimit(int invalid, int limit)
    {
        var set = ConversionFixtures.Slopes();
        var (surface, patch) = ConversionFixtures.Coarse(set);
        var before = MapSurfaceSemantics.PatchDigest(patch);
        var classifier = Assert.Throws<MapDocumentException>(() => MapConversionClassifier.Classify(surface, patch, 1, 1, invalid));
        Check(classifier);
        var converter = Assert.Throws<MapDocumentException>(() => MapFinePatchConversion.Convert(set, ConversionFixtures.Request(invalid, false)));
        Check(converter);
        Assert.Equal(before, MapSurfaceSemantics.PatchDigest(patch));
        Assert.Equal(MapCellConversionClass.ExactDiagonal, MapConversionClassifier.Classify(surface, patch, 1, 1, limit).Class);
        Assert.NotEmpty(MapFinePatchConversion.Convert(set, ConversionFixtures.Request(limit, false)).Cells);

        void Check(MapDocumentException error)
        {
            Assert.Equal(MapDocumentRefusal.ValueOutsideRange, error.Reason);
            Assert.Equal("Subdivision", error.FieldPath);
            Assert.Equal(invalid, error.ActualInteger);
            Assert.Equal(2, error.MinimumInteger);
            Assert.Equal(64, error.MaximumInteger);
        }
    }

    [Theory]
    [InlineData(null, "fine", "SourceSurfaceId", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("coarse", " ", "FineSurfaceId", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("coarse", "coarse", "FineSurfaceId", MapDocumentRefusal.AuthoredValueMismatch)]
    public void SurfaceIdentity_ReportsTheFirstFailedBranch(string? source, string fine, string field, MapDocumentRefusal reason)
    {
        var set = ConversionFixtures.Slopes();
        var request = ConversionFixtures.Request(2, false) with { SourceSurfaceId = source!, FineSurfaceId = fine };
        var error = Assert.Throws<MapDocumentException>(() => MapFinePatchConversion.Convert(set, request));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(field, error.FieldPath);
        Assert.Single(set.Refs);
        Assert.NotEmpty(MapFinePatchConversion.Convert(set, ConversionFixtures.Request(2, false)).Cells);
    }

    [Fact]
    public void MissingCell_NamesTheAuthoredAddressAndAcceptsRestoredPresence()
    {
        var set = ConversionFixtures.Slopes();
        var (surface, patch) = ConversionFixtures.Coarse(set);
        patch.SetPresent(1, 1, false);
        var error = Assert.Throws<MapDocumentException>(() => MapConversionClassifier.Classify(surface, patch, 1, 1, 2));
        Assert.Equal(MapDocumentRefusal.TerrainCellMissing, error.Reason);
        Assert.Equal(surface.Id, error.SurfaceId);
        Assert.Equal(patch.Key, error.Patch);
        Assert.Equal(MapLatticeAddress.Corner(1, 1), error.Address);
        Assert.False(patch.IsPresent(1, 1));
        patch.SetPresent(1, 1, true);
        Assert.Equal(MapCellConversionClass.ExactDiagonal, MapConversionClassifier.Classify(surface, patch, 1, 1, 2).Class);
    }

    [Fact]
    public void FineHeightOverflow_RetainsItsCellAndAcceptsTheLastRepresentableHeight()
    {
        var set = ConversionFixtures.Slopes();
        var (surface, patch) = ConversionFixtures.Coarse(set);
        // Refining by two multiplies each integer height by two.
        for (int i = 0; i < patch.Heights.Length; i++) patch.Heights[i] = int.MaxValue / 2 + 1;
        var geometry = new MapConversionCellGeometry(surface, patch, 1, 1);
        var error = Assert.Throws<MapDocumentException>(() => geometry.FineHeight(0, 0, 2));
        Assert.Equal(MapDocumentRefusal.GeometryNotRepresentable, error.Reason);
        Assert.Equal(surface.Id, error.SurfaceId);
        Assert.Equal(patch.Key, error.Patch);
        Assert.Equal(MapLatticeAddress.Corner(1, 1), error.Address);
        for (int i = 0; i < patch.Heights.Length; i++) patch.Heights[i] = int.MaxValue / 2;
        Assert.Equal(int.MaxValue - 1, new MapConversionCellGeometry(surface, patch, 1, 1).FineHeight(0, 0, 2));
    }
}
