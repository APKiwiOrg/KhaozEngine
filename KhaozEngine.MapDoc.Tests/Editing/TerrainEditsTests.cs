using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class TerrainEditsTests
{
    static MapTerrainEditResult Prepare(MapSurfaceSet set, MapTerrainEdit edit) => MapTerrainEdits.Prepare(set, edit);
    static MapReplaceTopology Remove(MapPatchKey key) => new(Array.Empty<MapSurfacePatch>(), new[] { key }, Array.Empty<MapSurfaceRef>(), Array.Empty<string>());

    [Theory, InlineData(1, 1), InlineData(2, 0)]
    public void Smooth_KeepsJ21Semantics(int iterations, int expected)
    {
        MapSurfaceSet bump = TerrainEditFixtures.Bump();
        var digest = TerrainEditFixtures.Digest(bump);
        MapSurfacePatch p = Prepare(bump, new MapSmoothCorners(TerrainEditFixtures.BumpKey, 2, 2, 1, 1, iterations)).Candidate.Patches[TerrainEditFixtures.BumpKey];
        Assert.Equal(expected, p.Height(2, 2));                           // 9/9 = 1, then 1/9 rounds to 0
        Assert.All(TerrainEditFixtures.Ring(2, 2), c => Assert.Equal(0, p.Height(c.X, c.Z)));
        Assert.Equal(digest, TerrainEditFixtures.Digest(bump));
    }
    [Theory, InlineData(0, false), InlineData(65, false), InlineData(16, true), InlineData(17, true), InlineData(64, true)]
    public void Smooth_AcceptsOneToSixtyFourPasses(int iterations, bool ok)
    {
        var edit = new MapSmoothCorners(TerrainEditFixtures.BumpKey, 2, 2, 1, 1, iterations);
        if (ok) Assert.NotNull(Prepare(TerrainEditFixtures.Bump(), edit));
        else Assert.Throws<ArgumentOutOfRangeException>(() => Prepare(TerrainEditFixtures.Bump(), edit));
    }
    [Fact]
    public void Smooth_RefusesAMissingHalo()
        => Assert.Contains("halo", Assert.Throws<MapDocumentException>(() => Prepare(TerrainEditFixtures.Bump(), new MapSmoothCorners(TerrainEditFixtures.BumpKey, 0, 0, 1, 1, 1))).Message);
    [Theory, InlineData("heights", MapNativeInvalidation.Terrain | MapNativeInvalidation.Physics | MapNativeInvalidation.Nav | MapNativeInvalidation.Residency),
     InlineData("ids", MapNativeInvalidation.Terrain | MapNativeInvalidation.Material),
     InlineData("cut", MapNativeInvalidation.Terrain | MapNativeInvalidation.Physics | MapNativeInvalidation.Nav | MapNativeInvalidation.Material | MapNativeInvalidation.Residency)]
    public void Effects_InvalidationFlagsMatchTheEditKind(string kind, MapNativeInvalidation expected)
        => Assert.Equal(expected, Prepare(TerrainEditFixtures.Bump(), TerrainEditFixtures.Edit(kind)).Effects.Invalidates);
    [Fact]
    public void CornerOwner_AddingALowerKeyPatchKeepsTheOwner_DeletingRequiresReassignment()
    {
        MapSurfaceSet set = SurfaceStorageFixtures.ThreeSurfaces().Surfaces;
        MapTerrainEditResult added = Prepare(set, new MapReplaceTopology(new[] { TerrainEditFixtures.GroundBelowSlot() }, Array.Empty<MapPatchKey>(), Array.Empty<MapSurfaceRef>(), Array.Empty<string>()));
        Assert.Contains(new MapCornerDependency(4, 4, new MapVertexOwner(new("ground", 0, 0), MapLatticeAddress.Corner(64, 0))),
            added.Candidate.Patches[new("ground", 0, -1)].CornerDependencies);
        Assert.Contains("owner", Assert.Throws<MapDocumentException>(() => Prepare(added.Candidate, Remove(new("ground", 0, 0)))).Message);
        MapTerrainEditResult done = Prepare(added.Candidate, new MapCompositeEdit(new MapTerrainEdit[] { TerrainEditFixtures.ReassignGroundColumn(added.Candidate), Remove(new("ground", 0, 0)) }));
        MapScopedSurfaces view = MapScopedSurfaces.CompleteView(done.Candidate);
        Assert.All(done.Candidate.Patches.Values, p => Assert.Empty(MapSeamValidator.ValidateCornerDependencies(p, view)));
        Assert.Contains(done.Effects.DigestChanges, c => c.Key.Contains("ground"));
    }
    [Fact]
    public void RemovingAnAnchorPatchRequiresReanchoringEveryReferrer()
    {
        MapSurfaceSet set = TerrainEditFixtures.WithFarAnchors();
        var farWest = new MapPatchKey("far", -300, 0);
        var farEast = new MapPatchKey("far", 300, 0);
        string m = Assert.Throws<MapDocumentException>(() => Prepare(set, Remove(farWest))).Message;
        Assert.Contains("anchor", m);
        Assert.Contains("far-chain", m);
        MapTerrainEditResult r = Prepare(set, new MapCompositeEdit(new MapTerrainEdit[] { new MapReanchorRecords(new[] { new MapRecordAnchorMove("far-chain", farWest, farEast) }), Remove(farWest) }));
        Assert.Equal(new MapRecordRef("far-chain", farEast), r.Candidate.AllRecords().OfType<MapWallStrip>().Single(w => w.Id == "far-wall").UpperChain);
        Assert.Empty(MapTopologyReferenceValidator.Validate(r.Candidate.Refs, r.Candidate.Patches.Values));
        Assert.True(r.Effects.Invalidates.HasFlag(MapNativeInvalidation.Residency));
    }
}
