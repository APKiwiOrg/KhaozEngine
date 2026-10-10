using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Storage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SpaceMembershipTests
{
    // SpaceMembershipTests
    static (string?, MapMembershipStatus) Space(MapDocument doc, float x, float y, float z) { var m = CaveFixtures.Query(doc, x, y, z); return (m.SpaceId, m.Status); }
    static IReadOnlyList<string> Validate(MapDocument doc) => MapSpaceCoverageValidator.Validate(CaveFixtures.View(doc));

    [Fact]
    public void NativeCaveRepresentationContract()
    {
        Assert.Empty(Validate(CaveFixtures.RampChamberShaft()));
        Assert.Empty(Validate(CaveFixtures.ShaftJoinedToLowerChamber()));
        MapDocument cave = CaveFixtures.RampChamberShaft();
        Assert.Equal(("chamber", MapMembershipStatus.Resolved), Space(cave, 2.5f, 5.5f, 4.5f));
        Assert.Equal(("outside", MapMembershipStatus.Resolved), Space(cave, 2.5f, 10.5f, 4.5f));
        Assert.Equal(((string?)null, MapMembershipStatus.Outside), Space(cave, 2.5f, 9.5f, 4.5f));   // inside the rock roof
        Assert.Equal(("deep", MapMembershipStatus.Resolved), Space(cave, 2.5f, -19f, 6.5f));
        Assert.Equal(("outside", MapMembershipStatus.Resolved), Space(cave, 3.5f, 9.5f, 2.5f));
    }
    [Fact]
    public void ExactPortalAndOpeningPlanes_FollowTheOwnershipTieRules()
    {
        MapDocument cave = CaveFixtures.RampChamberShaft();
        Assert.Equal("outside", Space(cave, 4.0f, 8.5f, 3.0f).Item1);      // on the mouth plane, To owns it
        Assert.Equal("chamber", Space(cave, 3.5f, 4.0f, 5.5f).Item1);      // on the shaft-top plane, the space above owns it
        Assert.Equal("shaft", Space(cave, 3.5f, 3.99f, 5.5f).Item1);
        Assert.Equal("shaft", Space(cave, 3.5f, -15.0f, 5.5f).Item1);
        Assert.Equal("deep", Space(cave, 3.5f, -15.01f, 5.5f).Item1);
    }
    [Fact]
    public void ShaftOpenings_HaveKnownBoundsWithoutFabricatedFloors()
    {
        MapDocument cave = CaveFixtures.RampChamberShaft();
        int[] shaftCells = { 5 * 64 + 3, 5 * 64 + 4, 6 * 64 + 3, 6 * 64 + 4 };
        foreach (string surface in new[] { "cave-floor", "deep-ceiling" })
            Assert.DoesNotContain(CaveFixtures.Compile(cave, surface).Faces, f => shaftCells.Contains(f.Key.Primitive));
        MapDocument open = CaveFixtures.WithoutOpening("shaft-top");                         // the shaft footprint and shaft-link still name shaft-top on purpose
        IReadOnlyList<string> found = Validate(open);
        IReadOnlyList<string> dangling = MapTopologyReferenceValidator.Validate(open.Surfaces.Refs, open.Surfaces.Patches.Values);
        Assert.Contains(dangling, f => f.Contains("shaft-top"));
        Assert.Subset(found.ToHashSet(), dangling.ToHashSet());
        foreach (int cell in shaftCells)
        {
            Assert.Contains(found, f => f.Contains("missing bound") && f.EndsWith($"cell {cell}", StringComparison.Ordinal));
            Assert.Contains(found, f => f.Contains("missing geometry") && f.Contains($"cell {cell}"));
        }
        Assert.Equal(MapMembershipStatus.MissingGeometry, CaveFixtures.Query(open, 3.5f, 5.0f, 5.5f).Status);
    }
    [Fact]
    public void UnloadedAmbiguousAndOverBudgetQueriesRefuse()
    {
        MapDocument cave = CaveFixtures.RampChamberShaft();
        var filtered = new FilteredSurfaceSource(MapDocumentSurfaceSource.Capture(cave), new MapPatchKey("cave-ceiling", 0, 0));
        Assert.Equal(MapMembershipStatus.MissingGeometry, CaveFixtures.Query(filtered, 2.5f, 5.5f, 4.5f).Status);
        Assert.Equal(MapMembershipStatus.CapacityExceeded, CaveFixtures.Query(cave, 2.5f, 5.5f, 4.5f, new MapQueryLimits(MaxCandidatePatches: 1)).Status);
        MapDocument peer = CaveFixtures.WithPeerChamber(null);
        Assert.Contains(Validate(peer), f => f.Contains("ambiguous"));
        Assert.Equal(MapMembershipStatus.Ambiguous, CaveFixtures.Query(peer, 2.5f, 5.5f, 4.5f).Status);
        Assert.Equal("chamber", CaveFixtures.Query(CaveFixtures.WithPeerChamber("chamber"), 2.5f, 5.5f, 4.5f).SpaceId);
    }
    [Fact]
    public void TinyLocalCoordinate_IsNotRepresentableNeverSnapped()
        => Assert.Equal(MapMembershipStatus.NotRepresentable, CaveFixtures.Query(CaveFixtures.RampChamberShaft(), 1e-30f, 10.5f, 4.5f).Status);
    [Fact]
    public void ImportedIndoorMask_IsANestedDomainWithAnExplicitSpan()
    {
        MapDocument row = CaveFixtures.IndoorRow();
        Assert.Equal(new[] { "world", "indoor-0", "indoor" }, CaveFixtures.Query(row, 0.5f, 1.0f, -0.5f).DomainKeys);
        Assert.Equal(new[] { "world" }, CaveFixtures.Query(row, 0.5f, 4.6f, -0.5f).DomainKeys);
        Assert.Equal(new[] { "world" }, CaveFixtures.Query(row, 1.5f, 1.0f, -0.5f).DomainKeys);
    }
    [Fact]
    public void MixedResolutionBounds_ValidateOnTheCommonRefinement()
    {
        MapDocument converted = MixedResolutionFixtures.Converted();
        Assert.Empty(Validate(converted));
        Assert.Equal("room", CaveFixtures.Query(converted, 1.5f, 5.0f, 1.5f).SpaceId);           // over the fine floor, under the 9 m coarse roof
        Assert.Empty(MapSpaceCoverageValidator.Validate(MixedResolutionFixtures.FinePeakUnderCoarseRoof(899)));
        Assert.Contains(MapSpaceCoverageValidator.Validate(MixedResolutionFixtures.FinePeakUnderCoarseRoof(900)), f => f.Contains("separation"));
        Assert.Empty(MapSpaceCoverageValidator.Validate(MixedResolutionFixtures.HalfUnderThird(far: false).View));
        Assert.Empty(MapSpaceCoverageValidator.Validate(MixedResolutionFixtures.HalfUnderThird(far: true).View));
    }
    [Theory, InlineData(1199, null), InlineData(1200, "(1/2, 1/3)")]
    public void RidgeUnderValley_SeparationIsCheckedAtCrossingsNeitherLatticeOwns(int ridge, string? vertex)
    {
        IReadOnlyList<string> findings = MapSpaceCoverageValidator.Validate(MixedResolutionFixtures.RidgeUnderValley(ridge, 1800));
        if (vertex is null) Assert.Empty(findings);
        else Assert.Contains(findings, f => f.Contains("separation") && f.Contains(vertex));
    }
    [Fact]
    public void MixedResolutionWalls_StillRequireMatchingSubdivisions()
    {
        Assert.Empty(MapSpaceCoverageValidator.Validate(MixedResolutionFixtures.ClosedRidgeCave(subdivided: true)));
        Assert.Contains(MapSpaceCoverageValidator.Validate(MixedResolutionFixtures.ClosedRidgeCave(subdivided: false)), f => f.Contains("vertex sequence"));
    }
    [Fact]
    public void MixedRefinementOutcomes_BecomeExplicitFindings()
    {
        var f = MixedResolutionFixtures.SixtyFourthUnderMetre();
        Assert.Empty(MapSpaceCoverageValidator.Validate(f.View));
        Assert.Contains(MapSpaceCoverageValidator.Validate(f.View, new MapRefinementLimits(MaxPairChecks: 16383)), x => x.Contains("refinement capacity"));
        Assert.Contains(MapSpaceCoverageValidator.Validate(MapScopedSurfaces.CompleteView(AcquisitionBoundFixtures.AdversarialUnits().Surfaces)),
            x => x.Contains("not representable") && x.Contains("strange-cells"));
    }
    [Fact]
    public void BoundaryTouchLedge_ResolvesMembershipOverTheMixedBounds()
    {
        MapScopedSurfaces s = ScopeFixtures.Acquire(MapDocumentSurfaceSource.Capture(AcquisitionBoundFixtures.BoundaryTouch()), AcquisitionBoundFixtures.TouchScope);
        MapMembershipResult m = new MapSpaceMembership(s).Query(new MapFramePoint(WorldFrame.Origin, new(31.8f, 1f, 0.2f)));
        Assert.Equal(("ledge", MapMembershipStatus.Resolved), (m.SpaceId, m.Status));
        Assert.Same(s.ReadWitness, m.Witness);
    }
    [Fact]
    public void ConvertedMixedPorch_ValidatesWithItsCoarseHalfFloor()
    {
        MapConversionResult r = MapFinePatchConversion.Convert(ConversionFixtures.MixedPorch(), ConversionFixtures.PorchRoofRequest(width: 3));
        Assert.Empty(MapSpaceCoverageValidator.Validate(MapScopedSurfaces.CompleteView(r.Candidate)));
    }
    [Fact]
    public void FarAnchoredSpaces_ResolveThroughTheAcquiredAnchor()
    {
        string dir = AnchorFixtures.FarAnchors();
        MapSurfaceScope scope = ScopeFixtures.Around(AnchorFixtures.FarFrame, 2, 2, half: 1.5f);
        MapScopedSurfaces s = ScopeFixtures.Acquire(MapStoredSurfaceSource.Open(dir), scope);
        var membership = new MapSpaceMembership(s);
        MapMembershipResult outside = membership.Query(new(AnchorFixtures.FarFrame, new(0.5f, 10.5f, 1.5f)));
        MapMembershipResult annex = membership.Query(new(AnchorFixtures.FarFrame, new(2.5f, 12f, 1.5f)));
        Assert.Equal(("outside", "annex"), (outside.SpaceId, annex.SpaceId));
        Assert.Equal(new[] { "outside" }, outside.DomainKeys);
        Assert.Same(s.ReadWitness, outside.Witness);
        Assert.Same(s.ReadWitness, annex.Witness);
        SurfaceStorageFixtures.DeletePayload(dir, new("ground", 0, 0));
        MapMembershipResult missing = new MapSpaceMembership(ScopeFixtures.Acquire(MapStoredSurfaceSource.Open(dir), scope)).Query(new(AnchorFixtures.FarFrame, new(0.5f, 10.5f, 1.5f)));
        Assert.Equal(MapMembershipStatus.MissingGeometry, missing.Status);
        Assert.Contains("outside", missing.Detail);
    }
    [Fact]
    public void LegacyExterior_ValidatorAndMembershipShareTheClassification()
    {
        static MapMembershipResult At(MapDocument doc, float x, float y = 5f) => CaveFixtures.Query(doc, x, y, -0.5f);
        MapDocument tagged = LegacyExteriorFixtures.Row("tagged");
        Assert.Empty(Validate(tagged));
        Assert.Equal(("world", (MapLegacyCellTag?)null), (At(tagged, 0.5f).SpaceId, At(tagged, 0.5f).LowerCompatibility));
        Assert.Equal(new MapLegacyCellTag(new("plane-0", 0, 0), 1, MapLegacyExteriorRecipe.PolicyId), At(tagged, 1.5f).LowerCompatibility);
        Assert.Equal((MapMembershipStatus.Resolved, MapMembershipStatus.Outside), (At(tagged, 1.5f, 3.0f).Status, At(tagged, 1.5f, 2.99f).Status));   // bilinear lower 3 m, inclusive
        MapDocument hole = LegacyExteriorFixtures.Row("hole"), untagged = LegacyExteriorFixtures.Row("untagged");
        Assert.Contains(Validate(hole), f => f.Contains("missing bound") && f.Contains("cell 3"));
        Assert.Equal(MapMembershipStatus.MissingGeometry, At(hole, 3.5f).Status);                                            // a physical hole is never filled
        Assert.Contains(Validate(untagged), f => f.Contains("missing bound") && f.Contains("cell 1"));
        Assert.Equal(MapMembershipStatus.MissingGeometry, At(untagged, 1.5f).Status);                                        // no tag, no fallback
        foreach (string variant in new[] { "cave", "upper-tag", "foreign-lattice" })
        {
            MapDocument bad = LegacyExteriorFixtures.Row(variant);
            Assert.Contains(Validate(bad), f => f.Contains("legacy recipe"));
            Assert.Equal(MapMembershipStatus.Invalid, At(bad, 1.5f).Status);
        }
    }
}
