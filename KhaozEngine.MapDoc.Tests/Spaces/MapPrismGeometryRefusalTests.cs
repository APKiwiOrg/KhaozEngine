using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapPrismGeometryRefusalTests
{
    [Theory]
    [InlineData("version", MapDocumentRefusal.SchemaPayloadVersionUnsupported)]
    [InlineData("lower", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("upper", MapDocumentRefusal.ValueNonfinite)]
    [InlineData("separation", MapDocumentRefusal.SpacePrismSeparationInvalid)]
    public void PrismPayload_ReportsTheFirstFailedField(string defect, MapDocumentRefusal reason)
    {
        var set = MapOccupiedPartitionRefusalTests.Scene(1);
        var view = MapOccupiedPartitionRefusalTests.View(set);
        MapPrismDomain valid = set.AllRecords().OfType<MapPrismDomain>().Single();
        MapPrismDomain invalid = defect switch
        {
            "version" => valid with { PayloadVersion = 2 },
            "lower" => valid with { LowerY = float.NaN },
            "upper" => valid with { UpperY = float.PositiveInfinity },
            _ => valid with { UpperY = valid.LowerY },
        };
        MapDocumentException error = Assert.Throws<MapDocumentException>(() => MapPrismGeometry.Compile(view, invalid));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(valid.Id, error.RecordId);
        Assert.Equal(valid.Space.Id, error.SpaceId);
        Assert.Equal(defect == "version" ? "PayloadVersion" : defect == "lower" ? "LowerY" : "UpperY", error.FieldPath);
        if (defect == "version")
        {
            Assert.Equal(2, error.ActualVersion);
            Assert.Equal(new[] { 1 }, error.SupportedVersions);
        }
        Assert.NotEmpty(MapPrismGeometry.Compile(view, valid).Pieces);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordLookup_SeparatesUnavailableFromTheWrongRecordType(bool wrongType)
    {
        var set = MapOccupiedPartitionRefusalTests.Scene(0);
        var view = MapOccupiedPartitionRefusalTests.View(set);
        var patch = set.Patches.Values.Single();
        var reference = new MapRecordRef(wrongType ? "exterior" : "absent", patch.Key);
        MapDocumentException error = Assert.Throws<MapDocumentException>(() => MapSpaceGeometry.Record<MapSpaceFootprint>(view, reference));
        Assert.Equal(wrongType ? MapDocumentRefusal.ReferenceKindMismatch : MapDocumentRefusal.PatchUnavailable, error.Reason);
        Assert.Equal(reference.Id, error.RecordId);
        Assert.Equal(reference.Anchor, error.Patch);
        Assert.Equal(typeof(MapSpaceFootprint), error.ExpectedRecordType);
        Assert.Equal(wrongType ? typeof(MapSpaceDoc) : null, error.RecordType);
        Assert.Equal("footprint", MapSpaceGeometry.Record<MapSpaceFootprint>(view, new("footprint", patch.Key)).Id);
    }
}
