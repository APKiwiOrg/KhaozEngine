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
    [Theory]
    [InlineData("missing", MapDocumentRefusal.TerrainConversionRegionsMissing)]
    [InlineData("empty", MapDocumentRefusal.BoundsInvalid)]
    [InlineData("overlap", MapDocumentRefusal.TerrainConversionRegionsOverlap)]
    [InlineData("surface", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("duplicate", MapDocumentRefusal.IdentityDuplicate)]
    [InlineData("cell", MapDocumentRefusal.TerrainCellMissing)]
    [InlineData("paint", MapDocumentRefusal.TerrainPhysicalSurfaceRequired)]
    public void ConversionAdmission_RefusesOneDefectWithoutChangingTheSource(string defect, MapDocumentRefusal reason)
    {
        var set = ConversionFixtures.Slopes();
        var request = ConversionFixtures.Request(2, false);
        var validSurface = set.Refs.Single();
        request = defect switch
        {
            "missing" => request with { Regions = [] },
            "empty" => request with { Regions = [new(1, 1, 1, 2)] },
            "overlap" => request with { Regions = [new(1, 1, 2, 2), new(1, 1, 2, 2)] },
            "surface" => request with { SourceSurfaceId = "missing" },
            "cell" => request with { Regions = [new(3, 1, 4, 2)] },
            _ => request
        };
        if (defect == "duplicate") set.Refs.Add(validSurface with { Id = request.FineSurfaceId });
        if (defect == "paint") set.Refs[0] = validSurface with { Role = MapSurfaceRole.PaintOverride };
        var before = MapSurfaceSemantics.PatchDigest(set.Patches.Values.Single());
        var refs = set.Refs.ToArray();
        var error = Assert.Throws<MapDocumentException>(() => MapFinePatchConversion.Convert(set, request));
        Assert.Equal(reason, error.Reason);
        if (defect == "empty") Assert.Equal(0, error.Index);
        if (defect == "overlap") Assert.Equal(1, error.Index);
        if (defect == "cell") Assert.Equal(MapLatticeAddress.Corner(3, 1), error.Address);
        if (defect == "surface") Assert.Equal("missing", error.SurfaceId);
        if (defect == "duplicate") Assert.Equal(request.FineSurfaceId, error.SurfaceId);
        Assert.Equal(before, MapSurfaceSemantics.PatchDigest(set.Patches.Values.Single()));
        Assert.Equal(refs, set.Refs);
        set.Refs.Clear();
        set.Refs.Add(validSurface);
        Assert.NotEmpty(MapFinePatchConversion.Convert(set, ConversionFixtures.Request(2, false)).Cells);
    }
}
