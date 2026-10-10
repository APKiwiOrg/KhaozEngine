using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Storage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SupportQueryTests
{
    // SupportQueryTests
    static MapSupportResult Select(MapDocument doc, MapSupportRequest r, MapQueryLimits? limits = null) => SupportFixtures.Select(doc, r, limits);
    static MapSupportRequest Req(float x, float y, float z, float up, float down) => SupportFixtures.Request(x, y, z, up, down);

    [Fact]
    public void StackedCaveFloorsAndCeilings_PreserveGeometryAndSupportSelection()
    {
        MapDocument doc = SupportFixtures.EightStacked();
        MapSupportResult a = Select(doc, Req(0.5f, 10.2f, 0.5f, 0.5f, 1f));
        Assert.Equal((MapSupportStatus.Supported, "floor-1", 10.0f, "level-1"), (a.Status, a.Face!.Value.OwnerId, a.WorldY, a.SpaceId));
        Assert.Equal("floor-0", Select(doc, Req(0.5f, 4.9f, 0.5f, 1f, 5f)).Face!.Value.OwnerId);
        Assert.Equal("floor-0", Select(doc, Req(0.5f, 1.0f, 0.5f, 20f, 1f)).Face!.Value.OwnerId);        // floor-1 is in range but bounds another space
        Assert.Equal(MapSupportStatus.NoSupport, Select(doc, Req(0.5f, 7.0f, 0.5f, 1f, 1f)).Status);       // inside the rock between levels
        Assert.Equal("floor-3", Select(doc, Req(0.5f, 30.2f, 0.5f, 0.5f, 1f) with { SpaceId = "level-3" }).Face!.Value.OwnerId);
        MapSupportResult over = Select(doc, Req(0.5f, 70.2f, 0.5f, 0.5f, 100f), new MapQueryLimits(MaxSupportIntersections: 2));
        Assert.Equal((MapSupportStatus.CapacityExceeded, (MapFaceKey?)null), (over.Status, over.Face));
    }
    [Fact]
    public void EqualHeightIndependentOwnersRefuseEvenWhenOneIsCurrent()
    {
        MapDocument slabs = SupportFixtures.EqualSlabs(secondIsPaint: false);
        MapSupportRequest req = Req(0.5f, 0.2f, 0.5f, 0.5f, 1f) with { CurrentSupport = SupportFixtures.FirstFace(slabs, "slab-a") };
        Assert.Equal(MapSupportStatus.Ambiguous, Select(slabs, req).Status);
        MapSupportCandidateSet set = new MapSupportQuery(SupportFixtures.Acquire(slabs, req.Point)).EnumerateCandidates(req, new MapSupportCandidate[1]);
        Assert.Equal((MapSupportStatus.CapacityExceeded, 1, 2), (set.Status, set.Count, set.RequiredCapacity));
        Assert.Equal("slab-a", Select(SupportFixtures.EqualSlabs(secondIsPaint: true), req).Face!.Value.OwnerId);
    }
    [Fact]
    public void CurrentLowerOwnerDoesNotBlockALegalHigherStep()
    {
        MapDocument step = SupportFixtures.Step();
        MapSupportResult r = Select(step, Req(1.5f, 0.35f, 0.5f, 0.5f, 1f) with { CurrentSupport = SupportFixtures.FirstFace(step, "low") });
        Assert.Equal(("step", 0.3f), (r.Face!.Value.OwnerId, r.WorldY));
    }
    [Fact]
    public void KnownHolesAreNoSupport_UndeclaredHolesAreMissingGeometry_LinksReachLowerFloors()
    {
        MapDocument cave = CaveFixtures.RampChamberShaft();
        Assert.Equal(MapSupportStatus.NoSupport, Select(cave, Req(3.5f, 3.0f, 5.5f, 0.5f, 5f)).Status);
        MapSupportResult deep = Select(cave, Req(3.5f, 3.0f, 5.5f, 0.5f, 30f));
        Assert.Equal((MapSupportStatus.Supported, "deep-floor", -20.0f, "deep"), (deep.Status, deep.Face!.Value.OwnerId, deep.WorldY, deep.SpaceId));
        Assert.Equal(new MapRecordRef("shaft-link", new("cave-floor", 0, 0)), deep.Via);
        Assert.Equal(MapSupportStatus.MissingGeometry, Select(CaveFixtures.WithoutOpening("shaft-top"), Req(3.5f, 5.0f, 5.5f, 0.5f, 1f)).Status);
    }
    [Fact]
    public void LegacyFallbackIsTaggedNonCapture()
    {
        MapDocument doc = SupportFixtures.LegacyFallback();
        Assert.Empty(MapSpaceCoverageValidator.Validate(CaveFixtures.View(doc)));                                         // the fixture passes the same validator
        MapSupportResult r = Select(doc, Req(1.5f, 5.0f, -0.5f, 0.5f, 10f));
        Assert.Equal((MapSupportStatus.LegacyFallback, 3.0f, false, (MapFaceKey?)null, (Vector3?)null), (r.Status, r.WorldY, r.IsCaptureSupport, r.Face, r.Normal));
        Assert.Equal(new MapLegacyCellTag(new("plane-0", 0, 0), 1, MapLegacyExteriorRecipe.PolicyId), r.Compatibility);
        Assert.Equal(MapSupportStatus.NoSupport, Select(doc, Req(1.5f, 5.0f, -0.5f, 0.5f, 1f)).Status);                   // 3 m lies below the 4 m to 5.5 m interval
        MapSupportRequest req = Req(1.5f, 5.0f, -0.5f, 0.5f, 10f);
        MapSupportCandidateSet set = new MapSupportQuery(SupportFixtures.Acquire(doc, req.Point)).EnumerateCandidates(req, new MapSupportCandidate[4]);
        Assert.Equal((MapSupportStatus.LegacyFallback, 0, 0), (set.Status, set.Count, set.RequiredCapacity));            // never a capture candidate
    }
    [Fact]
    public void LegacyExterior_ValidatorMembershipAndSupportAgreeAndRefuseOutsideTheRecipe()
    {
        static (MapMembershipStatus, MapSupportStatus) At(MapDocument doc, float x) =>
            (CaveFixtures.Query(doc, x, 5f, -0.5f).Status, Select(doc, Req(x, 5f, -0.5f, 0.5f, 10f)).Status);
        static (MapMembershipStatus, MapSupportStatus) From(IMapSurfaceSource source)
        {
            MapScopedSurfaces s = ScopeFixtures.Acquire(source, ScopeFixtures.Around(WorldFrame.Origin, 1.5f, -0.5f, half: 2));
            return (new MapSpaceMembership(s).Query(new(WorldFrame.Origin, new(1.5f, 5f, -0.5f))).Status, new MapSupportQuery(s).Select(Req(1.5f, 5f, -0.5f, 0.5f, 10f)).Status);
        }
        MapDocument tagged = LegacyExteriorFixtures.Row("tagged");
        Assert.Empty(MapSpaceCoverageValidator.Validate(CaveFixtures.View(tagged)));
        Assert.Equal(new[] { (MapMembershipStatus.Resolved, MapSupportStatus.Supported), (MapMembershipStatus.Resolved, MapSupportStatus.LegacyFallback), (MapMembershipStatus.Resolved, MapSupportStatus.LegacyFallback) },
            new[] { 0.5f, 1.5f, 2.5f }.Select(x => At(tagged, x)));
        Assert.Equal(4.5f, Select(tagged, Req(2.5f, 5f, -0.5f, 0.5f, 10f)).WorldY);
        Assert.Equal(CaveFixtures.Query(tagged, 2.5f, 5f, -0.5f).LowerCompatibility, Select(tagged, Req(2.5f, 5f, -0.5f, 0.5f, 10f)).Compatibility);
        Assert.Equal((MapMembershipStatus.MissingGeometry, MapSupportStatus.MissingGeometry), At(LegacyExteriorFixtures.Row("hole"), 3.5f));
        Assert.Equal((MapMembershipStatus.MissingGeometry, MapSupportStatus.MissingGeometry), At(LegacyExteriorFixtures.Row("untagged"), 1.5f));
        foreach (string variant in new[] { "cave", "upper-tag", "foreign-lattice" })
            Assert.Equal((MapMembershipStatus.Invalid, MapSupportStatus.Invalid), At(LegacyExteriorFixtures.Row(variant), 1.5f));
        string dir = SurfaceStorageFixtures.SaveToTemp(tagged);
        var key = new MapPatchKey("plane-0", 0, 0);
        Assert.Equal((MapMembershipStatus.Resolved, MapSupportStatus.LegacyFallback), From(MapStoredSurfaceSource.Open(dir)));
        Assert.Equal((MapMembershipStatus.MissingGeometry, MapSupportStatus.MissingGeometry), From(new FilteredSurfaceSource(MapStoredSurfaceSource.Open(dir), key)));   // unloaded
        SurfaceStorageFixtures.FlipPayloadByte(dir, key);
        Assert.Equal((MapMembershipStatus.MissingGeometry, MapSupportStatus.MissingGeometry), From(MapStoredSurfaceSource.Open(dir)));                                // corrupt
        SurfaceStorageFixtures.DeletePayload(dir, key);
        Assert.Equal((MapMembershipStatus.MissingGeometry, MapSupportStatus.MissingGeometry), From(MapStoredSurfaceSource.Open(dir)));                                // missing
        var untaggedDigest = MapSurfaceSemantics.PatchDigest(LegacyExteriorFixtures.Row("untagged").Surfaces.Patches[key]);
        Assert.NotEqual(untaggedDigest, MapSurfaceSemantics.PatchDigest(tagged.Surfaces.Patches[key]));                   // the policy tag is semantic identity
    }
    [Fact]
    public void LegacyFallback_UsesCellRelativeExactFractionsWithoutAWorldFloatRoundTrip()
    {
        MapSupportRequest near = Req(1.1f, 5f, -0.5f, 0.5f, 10f);
        MapSupportRequest far = near with { Point = new MapFramePoint(new WorldFrame(250, 0), new(1.1f, 5f, -0.5f)) };     // anchor x 32,000 m
        MapSupportResult a = Select(LegacyExteriorFixtures.Row("tagged"), near), b = Select(LegacyExteriorFixtures.Row("tagged", slotX: 500), far);
        Assert.Equal((MapSupportStatus.LegacyFallback, MapSupportStatus.LegacyFallback), (a.Status, b.Status));
        Assert.Equal(BitConverter.SingleToInt32Bits(a.WorldY), BitConverter.SingleToInt32Bits(b.WorldY));
        Assert.Equal(BitConverter.SingleToInt32Bits(MapLegacyBilinear.Evaluate(100, 300, 200, 600, 1.1f - 1f, 0.5f)), BitConverter.SingleToInt32Bits(a.WorldY));
        Assert.NotEqual(BitConverter.SingleToInt32Bits(MapLegacyBilinear.Evaluate(100, 300, 200, 600, 32001.1f - 32001f, 0.5f)), BitConverter.SingleToInt32Bits(b.WorldY));   // a world-float round trip samples fx 0.099609375
    }
    [Fact]
    public void CanonicalEntranceSeamAndAperture_Agree()
    {
        MapDocument cave = CaveFixtures.RampChamberShaft();
        Assert.Empty(MapSeamValidator.Validate(CaveFixtures.Record<MapSurfaceSeam>(cave, "rim-seam"), CaveFixtures.View(cave)));
        MapCompiledPatch outer = CaveFixtures.Compile(cave, "outer"), floor = CaveFixtures.Compile(cave, "cave-floor");
        for (int x = 2; x <= 6; x++)
            Assert.Equal(CompilerFixtures.ExactAt(outer, "outer", MapLatticeAddress.Corner(x, 2)), CompilerFixtures.ExactAt(floor, "cave-floor", MapLatticeAddress.Corner(x, 2)));
        Assert.DoesNotContain(outer.Faces, f => f.Key.Primitive >= 2 * 64 + 2 && f.Key.Primitive <= 2 * 64 + 5);
        Assert.Empty(outer.LegacyFallbackCells);
        Assert.Equal("cave-floor", Select(cave, Req(3.5f, 9.5f, 2.5f, 0.5f, 1f)).Face!.Value.OwnerId);
    }
    [Fact]
    public void TinyLocalCoordinate_SupportIsNotRepresentable()
        => Assert.Equal(MapSupportStatus.NotRepresentable, Select(CaveFixtures.RampChamberShaft(), Req(1e-30f, 10.5f, 4.5f, 0.5f, 1f)).Status);
}
