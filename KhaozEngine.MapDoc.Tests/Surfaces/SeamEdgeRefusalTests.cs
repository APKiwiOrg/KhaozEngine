using System.Collections.Generic;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SeamEdgeRefusalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Corner_owner_findings_keep_the_dependent_and_requested_owner(bool missingSurface)
    {
        var (view, _, pos) = BoundaryFixtures.NegativeSlotPair();
        var patch = pos.Patch;
        var original = patch.CornerDependencies[0];
        var typed = new List<MapDocumentFinding>();
        if (missingSurface)
        {
            var empty = KhaozEngine.MapDoc.Storage.MapScopedSurfaces.CompleteView(new MapSurfaceSet());
            MapSeamValidator.ValidateCornerDependencies(patch, empty, typed);
            var finding = Assert.Single(typed);
            Assert.Equal(MapDocumentRefusal.ReferenceMissing, finding.Reason);
            Assert.Equal(patch.Key, finding.Patch);
            Assert.Equal(patch.Key.SurfaceId, finding.SurfaceId);
        }
        else
        {
            var address = MapLatticeAddress.Create(0, 1, 2);
            patch.CornerDependencies[0] = original with { Owner = original.Owner with { Address = address } };
            MapSeamValidator.ValidateCornerDependencies(patch, view, typed);
            var position = Assert.Single(typed, f => f.Reason == MapDocumentRefusal.SurfaceSeamPositionMismatch);
            var vertex = Assert.Single(typed, f => f.Reason == MapDocumentRefusal.SurfaceSeamVertexUndeclared);
            Assert.Equal(patch.Key, position.Patch);
            Assert.Equal(original.Owner.Patch, vertex.ExpectedPatch);
            Assert.Equal(address, vertex.Address);
        }
        patch.CornerDependencies[0] = original;
        typed.Clear();
        Assert.Empty(MapSeamValidator.ValidateCornerDependencies(patch, view, typed));
        Assert.Empty(typed);
    }

    [Theory]
    [InlineData("from-surface", MapDocumentRefusal.AuthoredValueMismatch)]
    [InlineData("to-surface", MapDocumentRefusal.AuthoredValueMismatch)]
    [InlineData("collapsed", MapDocumentRefusal.SurfaceSeamEdgeInvalid)]
    [InlineData("diagonal", MapDocumentRefusal.SurfaceSeamEdgeInvalid)]
    [InlineData("from-vertex", MapDocumentRefusal.SurfaceSeamVertexUndeclared)]
    [InlineData("to-vertex", MapDocumentRefusal.SurfaceSeamVertexUndeclared)]
    public void Edge_findings_preserve_the_failing_endpoint(string fault, MapDocumentRefusal reason)
    {
        var (view, original) = BoundaryFixtures.CoarseFineSeam(2, true, 0);
        var edge = original.First;
        edge = fault switch
        {
            "from-surface" => edge with { From = edge.From with { SurfaceId = "other" } },
            "to-surface" => edge with { To = edge.To with { SurfaceId = "other" } },
            "collapsed" => edge with { To = edge.From },
            "diagonal" => edge with { To = edge.To with { Address = MapLatticeAddress.Corner(1, 0) } },
            "from-vertex" => edge with { From = edge.From with { Address = MapLatticeAddress.Corner(-1, 1) } },
            _ => edge with { To = edge.To with { Address = MapLatticeAddress.Corner(2, 1) } },
        };
        var typed = new List<MapDocumentFinding>();
        MapSeamValidator.Validate(original with { First = edge }, view, typed);
        var finding = Assert.Single(typed);
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(original.Id, finding.RecordId);
        Assert.Equal(edge.Patch, finding.Patch);
        if (fault.EndsWith("vertex", System.StringComparison.Ordinal))
            Assert.Equal(fault == "from-vertex" ? edge.From.Address : edge.To.Address, finding.Address);
        if (fault.EndsWith("surface", System.StringComparison.Ordinal)) Assert.Equal("other", finding.SurfaceId);
        typed.Clear();
        Assert.Empty(MapSeamValidator.Validate(original, view, typed));
        Assert.Empty(typed);
    }
}
