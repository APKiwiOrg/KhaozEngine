using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class LowerCellClassifierTests
{
    // LowerCellClassifierTests (MapDoc.Tests)
    static MapLowerCellClass Class(string variant, long cellX)
    {
        MapScopedSurfaces v = LegacyExteriorFixtures.View(variant);
        return MapLowerCellClassifier.Classify(v, LegacyExteriorFixtures.Footprint(v), cellX, 0).Class;
    }
    [Fact]
    public void Classifier_SeparatesPhysicalLegacyHoleUntaggedAndInvalidCells()
    {
        Assert.Equal(new[] { MapLowerCellClass.Physical, MapLowerCellClass.LegacyNonCapture, MapLowerCellClass.LegacyNonCapture, MapLowerCellClass.KnownHole },
            new long[] { 0, 1, 2, 3 }.Select(x => Class("tagged", x)));
        Assert.Equal((MapLowerCellClass.Physical, MapLowerCellClass.UntaggedLegacy, MapLowerCellClass.UntaggedLegacy), (Class("untagged", 0), Class("untagged", 1), Class("untagged", 2)));
        foreach (string variant in new[] { "cave", "upper-tag", "foreign-lattice" })
            Assert.Equal(MapLowerCellClass.InvalidRecipe, Class(variant, 1));
        MapScopedSurfaces tagged = LegacyExteriorFixtures.View("tagged");
        Assert.Equal(new MapLegacyCellTag(new("plane-0", 0, 0), 2, "kemap/legacy-exterior/1"), MapLowerCellClassifier.Classify(tagged, LegacyExteriorFixtures.Footprint(tagged), 2, 0).Legacy);
        Assert.Equal(new[] { 1, 2 }, LegacyExteriorFixtures.Compiled(LegacyExteriorFixtures.Row("tagged")).LegacyFallbackCells.Select(c => c.SlotCell));   // compiler and classifier share one rule
    }
    [Fact]
    public void LegacyExterior_RefinementCountsClassifiedCoverageWithoutFaces()
    {
        MapScopedSurfaces v = LegacyExteriorFixtures.View("tagged");
        MapSpaceFootprint f = LegacyExteriorFixtures.Footprint(v);
        MapCellRefinement physical = MapCommonRefinement.RefineFootprintCell(v, f, 0, new()), legacy = MapCommonRefinement.RefineFootprintCell(v, f, 1, new());
        Assert.Equal((MapRefinementStatus.Complete, 2, new MapExactValue(1, 1), new MapExactValue(0, 1)), (physical.Status, physical.Faces.Count, physical.LowerArea, physical.CompatibilityArea));
        Assert.Equal((MapRefinementStatus.Complete, 0, 0, new MapExactValue(0, 1), new MapExactValue(1, 1)), (legacy.Status, legacy.Faces.Count, legacy.Vertices.Count, legacy.LowerArea, legacy.CompatibilityArea));
        Assert.Equal(new[] { new MapLegacyCellTag(new("plane-0", 0, 0), 1, MapLegacyExteriorRecipe.PolicyId) }, legacy.CompatibilityCells);
        MapScopedSurfaces untagged = LegacyExteriorFixtures.View("untagged");
        MapCellRefinement bare = MapCommonRefinement.RefineFootprintCell(untagged, LegacyExteriorFixtures.Footprint(untagged), 1, new());
        Assert.Equal((MapRefinementStatus.Complete, new MapExactValue(0, 1), new MapExactValue(0, 1)), (bare.Status, bare.LowerArea, bare.CompatibilityArea));   // uncovered, so 12B reports missing bound
        foreach (string variant in new[] { "cave", "upper-tag", "foreign-lattice" })
        {
            MapScopedSurfaces bad = LegacyExteriorFixtures.View(variant);
            MapCellRefinement r = MapCommonRefinement.RefineFootprintCell(bad, LegacyExteriorFixtures.Footprint(bad), 1, new());
            Assert.Equal((MapRefinementStatus.Invalid, 0), (r.Status, r.CompatibilityCells.Count));
            Assert.Contains("legacy recipe", r.Detail);
        }
    }
}
