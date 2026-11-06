using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SeamAdmissionRefusalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_seam_patch_keeps_the_requested_side(bool second)
    {
        var (view, seam) = BoundaryFixtures.CoarseFineSeam(2, true, 0);
        var set = new MapSurfaceSet();
        set.Refs.AddRange(view.Surfaces);
        var missing = second ? seam.Second.Patch : seam.First.Patch;
        var retained = second ? seam.First.Patch : seam.Second.Patch;
        set.Patches.Add(retained, view.Patch(retained).Patch!);
        var typed = new List<MapDocumentFinding>();
        var prose = MapSeamValidator.Validate(seam, MapScopedSurfaces.CompleteView(set), typed);
        var finding = Assert.Single(typed);
        Assert.Equal(MapDocumentRefusal.PatchUnavailable, finding.Reason);
        Assert.Equal(missing, finding.Patch);
        Assert.Equal(seam.Id, finding.RecordId);
        Assert.Equal(second ? "Second.Patch" : "First.Patch", finding.FieldPath);
        Assert.Equal(new[] { finding.Message }, prose);
        typed.Clear();
        Assert.Empty(MapSeamValidator.Validate(seam, view, typed));
        Assert.Empty(typed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pair_surface_refusal_reuses_authored_target_mismatch(bool second)
    {
        var (view, original) = BoundaryFixtures.CoarseFineSeam(2, true, 0);
        var pairs = original.Pairs.ToArray();
        var pair = pairs[0];
        pairs[0] = second ? (pair.First, pair.Second with { SurfaceId = "other" })
            : (pair.First with { SurfaceId = "other" }, pair.Second);
        var typed = new List<MapDocumentFinding>();
        MapSeamValidator.Validate(original with { Pairs = pairs }, view, typed);
        Assert.NotEmpty(typed);
        var finding = typed[0];
        Assert.Equal(MapDocumentRefusal.AuthoredValueMismatch, finding.Reason);
        Assert.Equal(original.Id, finding.RecordId);
        Assert.Equal("other", finding.SurfaceId);
        Assert.Equal(0, finding.Index);
        Assert.Equal(second ? "Pairs[0].Second.SurfaceId" : "Pairs[0].First.SurfaceId", finding.FieldPath);
        typed.Clear();
        Assert.Empty(MapSeamValidator.Validate(original, view, typed));
        Assert.Empty(typed);
    }

    [Fact]
    public void Duplicate_pairs_keep_the_shared_address_and_pair_index()
    {
        var (view, seam) = BoundaryFixtures.CoarseFineSeam(2, true, 0);
        var typed = new List<MapDocumentFinding>();
        var repeated = seam with { Pairs = seam.Pairs.Append(seam.Pairs[0]).ToArray() };
        MapSeamValidator.Validate(repeated, view, typed);
        var finding = Assert.Single(typed);
        Assert.Equal(MapDocumentRefusal.IdentityDuplicate, finding.Reason);
        Assert.Equal(seam.Pairs.Count, finding.Index);
        Assert.Equal(seam.Pairs[0].First.Address, finding.Address);
        Assert.Equal(seam.Id, finding.RecordId);
        typed.Clear();
        Assert.Empty(MapSeamValidator.Validate(seam, view, typed));
        Assert.Empty(typed);
    }
}
