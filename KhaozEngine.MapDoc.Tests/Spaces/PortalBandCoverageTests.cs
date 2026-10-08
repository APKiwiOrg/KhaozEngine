using System.Collections.Generic;
using System.Linq;
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
    }
    [Fact]
    public void PortalBandCoverage_EachFaceIsEmittedOnce()
    {
        var faces = PortalBandFixtures.CompileAllStrips(PortalBandFixtures.Build("lintel")).SelectMany(s => s.Faces).ToList();
        Assert.Equal(faces.Count, faces.Select(f => f.Key).Distinct().Count());
        Assert.Equal(8, faces.Count(f => f.Key.OwnerId == "lintel"));   // 2 segments x 2 triangles x 2 sides = 8 faces
    }
}
