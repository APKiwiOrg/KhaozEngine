using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class PortalBandCoverageTests
{
    // PortalBandCoverageTests
    [Theory, InlineData("header", null), InlineData("header-missing", "gap"), InlineData("header-overlap", "overlap"), InlineData("riser", null),
     InlineData("lintel", null), InlineData("arch", null), InlineData("arch-mismatch", "vertex sequence"), InlineData("outside-air", "air interval"),
     InlineData("duplicate-side", "duplicate"), InlineData("cave-open-edge", "gap")]
    public void PortalBandCoverage_HeadersRisersAndLintels(string variant, string? finding)
    {
        IReadOnlyList<string> findings = MapSpaceCoverageValidator.Validate(CaveFixtures.View(PortalBandFixtures.Build(variant)));
        if (finding is null) Assert.Empty(findings); else Assert.Contains(findings, f => f.Contains(finding));
        var typed = new List<MapDocumentFinding>();
        Assert.Equal(findings, MapSpaceCoverageValidator.Validate(CaveFixtures.View(PortalBandFixtures.Build(variant)), new(), typed));
        if (finding is null) Assert.Empty(typed);
        else
        {
            MapDocumentRefusal reason = finding switch
            {
                "gap" => MapDocumentRefusal.SpaceBoundaryGap,
                "overlap" => MapDocumentRefusal.SpaceBoundaryOverlap,
                "vertex sequence" => MapDocumentRefusal.SchemaArrayLengthMismatch,
                "air interval" => MapDocumentRefusal.SpacePortalAirIntervalInvalid,
                _ => MapDocumentRefusal.IdentityDuplicate,
            };
            Assert.Contains(typed, f => f.Reason == reason && f.RecordId is not null);
            if (variant == "arch-mismatch")
            {
                var mismatch = Assert.Single(typed, f => f.Reason == reason);
                Assert.Equal("Upper.Points", mismatch.FieldPath);
                Assert.Equal(3L, mismatch.ActualCount);
                Assert.Equal(2L, mismatch.ExpectedCount);
            }
            var control = new List<MapDocumentFinding>();
            Assert.Empty(MapSpaceCoverageValidator.Validate(CaveFixtures.View(PortalBandFixtures.Build(
                variant == "arch-mismatch" ? "arch" : "header")), new(), control));
            Assert.Empty(control);
        }
    }
    [Fact]
    public void PortalBandCoverage_EachFaceIsEmittedOnce()
    {
        var faces = PortalBandFixtures.CompileAllStrips(PortalBandFixtures.Build("lintel")).SelectMany(s => s.Faces).ToList();
        Assert.Equal(faces.Count, faces.Select(f => f.Key).Distinct().Count());
        Assert.Equal(8, faces.Count(f => f.Key.OwnerId == "lintel"));   // 2 segments x 2 triangles x 2 sides = 8 faces
    }
}
