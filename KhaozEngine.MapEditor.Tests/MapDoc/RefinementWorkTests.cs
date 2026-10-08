using KhaozEngine.MapDoc.Spaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class RefinementWorkTests
{
    // RefinementWorkTests (MapEditor.Tests, internal work counters)
    [Fact]
    public void PairBudget_RefusesBeforeAnyComparisonAndCountsBoundingBoxPairsSeparately()
    {
        var lower = RefinementFixtures.UnitSquareGrid16("lo", 0);
        var upper = RefinementFixtures.UnitSquareGrid16("up", 100);
        var refused = new MapRefinementWork();
        MapCommonRefinement.Refine(lower, upper, RefinementFixtures.Origin00, RefinementFixtures.One11, new(MaxPairChecks: 8192), refused);
        Assert.Equal((262_144L, 0L, 0L, 0L), (refused.ChargedPairs, refused.BoundingBoxComparisons, refused.PositiveBoundingBoxPairs, refused.PositiveIntersections));
        var done = new MapRefinementWork();
        MapCommonRefinement.Refine(RefinementFixtures.UnitSquareGrid2("lo", 0), RefinementFixtures.UnitSquareGrid2("up", 100), RefinementFixtures.Origin00, RefinementFixtures.One11, new(MaxPairChecks: 64), done);
        Assert.Equal((64L, 64L, 16L, 8L), (done.ChargedPairs, done.BoundingBoxComparisons, done.PositiveBoundingBoxPairs, done.PositiveIntersections));
        var tee = new MapRefinementWork();
        MapCommonRefinement.Refine(RefinementFixtures.UnitSquareSwNe("lo", 0), RefinementFixtures.FanFromSouthMidpoint("up", 300), RefinementFixtures.Origin00, RefinementFixtures.One11, new(), tee);
        Assert.Equal((6L, 6L, 6L, 5L), (tee.ChargedPairs, tee.BoundingBoxComparisons, tee.PositiveBoundingBoxPairs, tee.PositiveIntersections));
    }
    [Fact]
    public void BoundFaces_CountInRangeFacesFromBytesBeforeAnyCompile()
    {
        var f = MixedResolutionFixtures.SixtyFourthUnderMetre();
        var refused = new MapBoundFaceWork();
        MapCellRefinement r = MapCommonRefinement.RefineFootprintCell(f.View, f.Footprint, 0, new MapRefinementLimits(MaxSourceFacesPerBound: 8191), refused);
        Assert.Equal((MapRefinementStatus.CapacityExceeded, 8192L, 0, 0L), (r.Status, refused.BoundFacesCounted, refused.Compiles, refused.CompiledFaces));
        Assert.Contains("source faces", r.Detail);
        var done = new MapBoundFaceWork();
        Assert.Equal(MapRefinementStatus.Complete, MapCommonRefinement.RefineFootprintCell(f.View, f.Footprint, 0, new MapRefinementLimits(), done).Status);
        Assert.Equal((1, 8194L, 8194L, 2, 8194L), (done.ContextsPrepared, done.BoundFacesCounted, done.ContextFacesCounted, done.Compiles, done.CompiledFaces));
    }
    [Fact]
    public void BoundFaceContext_CompilesEachBoundPatchOnceAndOverBudgetDemandCompilesNothing()
    {
        var room = BoundFaceContextFixtures.EightByEight();
        var work = new MapBoundFaceWork();
        MapFootprintPreparation prepared = MapCommonRefinement.PrepareFootprints(room.View, new[] { room.Footprint }, new MapRefinementLimits(), work);
        Assert.Empty(prepared.CellOutcomes);
        foreach (int cell in room.Footprint.SlotCells)
            Assert.Equal(MapRefinementStatus.Complete, MapCommonRefinement.RefineFootprintCell(prepared.Context!, room.Footprint, cell, new MapRefinementLimits()).Status);
        Assert.Equal((1, 640L, 2, 640L, 0), (work.ContextsPrepared, work.ContextFacesCounted, work.Compiles, work.CompiledFaces, room.View.PatchClones));   // 64 cells, two patches, one compile each
        var refused = new MapBoundFaceWork();
        MapFootprintPreparation none = MapCommonRefinement.PrepareFootprints(room.View, new[] { room.Footprint }, new MapRefinementLimits(MaxContextFaces: 639), refused);
        Assert.Equal(((MapBoundFaceContext?)null, 640L, 640L, 0, 0L), (none.Context, refused.BoundFacesCounted, refused.ContextFacesCounted, refused.Compiles, refused.CompiledFaces));   // 640 demanded, one over
        Assert.Contains("context faces", none.Refusal);
        Assert.Equal((256, 262_144), (new MapRefinementLimits().MaxContextPatches, new MapRefinementLimits().MaxContextFaces));   // proposed defaults, values only
    }
}
