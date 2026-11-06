using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapLowerCellFindingTests
{
    [Theory]
    [InlineData("space", MapDocumentRefusal.PatchUnavailable)]
    [InlineData("kind", MapDocumentRefusal.ReferenceKindMismatch)]
    [InlineData("surface", MapDocumentRefusal.ReferenceMissing)]
    [InlineData("patch", MapDocumentRefusal.PatchUnavailable)]
    public void UnavailableLowerCell_KeepsItsReadFailureWithoutChangingTheClass(string fault, MapDocumentRefusal reason)
    {
        var document = LegacyExteriorFixtures.Row("untagged");
        var patch = document.Surfaces.Patches.Values.Single();
        var footprint = patch.Records.OfType<MapSpaceFootprint>().Single();
        if (fault == "space") patch.Records.RemoveAll(r => r.Id == footprint.Space.Id);
        if (fault == "kind")
        {
            patch.Records.RemoveAll(r => r.Id == footprint.Space.Id);
            patch.Records.Add(new MapHorizontalOpening(footprint.Space.Id, patch.Key, [3]));
        }
        if (fault == "surface") footprint = footprint with { Lower = footprint.Lower with { SurfaceId = "missing" } };
        var view = MapScopedSurfaces.CompleteView(document.Surfaces);
        var refused = MapLowerCellClassifier.Classify(view, footprint, fault == "patch" ? 64 : 0, 0);
        Assert.Equal(MapLowerCellClass.Unavailable, refused.Class);
        var finding = Assert.IsType<MapDocumentFinding>(refused.Finding);
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(footprint.Id, finding.RecordId);
        Assert.Equal(footprint.Space.Id, finding.SpaceId);
        if (fault == "patch")
        {
            Assert.Equal(new MapPatchKey("plane-0", 1, 0), finding.Patch);
            Assert.Equal(MapPatchStatus.Unloaded, finding.PatchStatus);
        }
        var control = LegacyExteriorFixtures.View("untagged");
        var accepted = MapLowerCellClassifier.Classify(control, LegacyExteriorFixtures.Footprint(control), 0, 0);
        Assert.Equal(MapLowerCellClass.Physical, accepted.Class);
        Assert.Null(accepted.Finding);
    }

    [Theory]
    [InlineData(0, 32768)]
    [InlineData(1, -32769)]
    [InlineData(2, 32768)]
    [InlineData(3, -32769)]
    public void LegacyHeight_RetainsTheFirstFailedCornerAndInclusiveRange(int corner, int height)
    {
        var view = LegacyExteriorFixtures.View("tagged");
        var footprint = LegacyExteriorFixtures.Footprint(view);
        var surface = view.Surfaces.Single();
        var patch = view.Patch(footprint.Lattice).Patch!;
        int[] indices = [1, 2, patch.Width + 2, patch.Width + 3];
        patch.Heights[indices[corner]] = height;
        var refused = MapLowerCellClassifier.ClassifyPatchWithFaultForTests(footprint, surface, patch, 1);
        Assert.Equal(MapLowerCellClass.InvalidRecipe, refused.Class);
        var finding = Assert.IsType<MapDocumentFinding>(refused.Finding);
        Assert.Equal(MapDocumentRefusal.ValueOutsideRange, finding.Reason);
        Assert.Equal("h" + new[] { "00", "10", "01", "11" }[corner], finding.FieldPath);
        Assert.Equal((long)height, finding.ActualInteger);
        Assert.Equal((long)short.MinValue, finding.MinimumInteger);
        Assert.Equal((long)short.MaxValue, finding.MaximumInteger);
        Assert.Equal(patch.Key, finding.Patch);
        Assert.Equal(1, finding.SlotCell);
        patch.Heights[indices[corner]] = height < 0 ? short.MinValue : short.MaxValue;
        Assert.Equal(MapLowerCellClass.LegacyNonCapture,
            MapLowerCellClassifier.ClassifyPatchWithFaultForTests(footprint, surface, patch, 1).Class);
    }

    [Fact]
    public void CorruptPatch_KeepsTheConcreteInnerFindingAndUnavailableProjection()
    {
        var view = LegacyExteriorFixtures.View("tagged");
        var footprint = LegacyExteriorFixtures.Footprint(view);
        var patch = view.Patch(footprint.Lattice).Patch!;
        var presence = patch.Presence;
        patch.Presence = [];
        var refused = MapLowerCellClassifier.ClassifyPatchWithFaultForTests(footprint, view.Surfaces.Single(), patch, 0);
        Assert.Equal(MapLowerCellClass.Unavailable, refused.Class);
        var finding = Assert.IsType<MapDocumentFinding>(refused.Finding);
        Assert.Equal(MapDocumentRefusal.SchemaArrayLengthMismatch, finding.Reason);
        Assert.Equal("Presence", finding.FieldPath);
        Assert.Equal(0L, finding.ActualCount);
        Assert.Equal(1L, finding.ExpectedCount);
        Assert.Equal(patch.Key, finding.Patch);
        patch.Presence = presence;
        Assert.Equal(MapLowerCellClass.Physical,
            MapLowerCellClassifier.ClassifyPatchWithFaultForTests(footprint, view.Surfaces.Single(), patch, 0).Class);
    }
}
