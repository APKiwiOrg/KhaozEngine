using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class TerrainOwnershipRefusalTests
{
    [Theory]
    [InlineData("upsert", MapDocumentRefusal.IdentityDuplicate)]
    [InlineData("remove", MapDocumentRefusal.IdentityDuplicate)]
    [InlineData("conflict", MapDocumentRefusal.TerrainTopologyWritesConflict)]
    public void PatchWrites_SelectTheFirstDuplicateOrConflictBranch(string defect, MapDocumentRefusal reason)
    {
        var set = TerrainEditFixtures.Bump();
        var key = TerrainEditFixtures.BumpKey;
        var patch = set.Patches[key];
        var edit = defect switch
        {
            "upsert" => new MapReplaceTopology([patch, patch], [], [], []),
            "remove" => new MapReplaceTopology([], [key, key], [], []),
            _ => new MapReplaceTopology([patch], [key], [], [])
        };
        var before = TerrainEditFixtures.Digest(set);
        var error = Assert.Throws<MapDocumentException>(() => MapTerrainEdits.Prepare(set, edit));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(defect == "upsert" ? "UpsertPatches" : defect == "remove" ? "RemovePatches" : "UpsertPatches/RemovePatches", error.FieldPath);
        Assert.Equal(before, TerrainEditFixtures.Digest(set));
        Assert.NotNull(MapTerrainEdits.Prepare(set, new MapReplaceTopology([patch], [], [], [])));
    }

    [Theory]
    [InlineData("stale", MapDocumentRefusal.TerrainOwnerChangeStale)]
    [InlineData("address", MapDocumentRefusal.SurfaceSeamPositionMismatch)]
    [InlineData("duplicate", MapDocumentRefusal.IdentityDuplicate)]
    public void OwnerChange_ReportsItsSpecificRuleAndDoesNotMutateTheSource(string defect, MapDocumentRefusal reason)
    {
        var set = TerrainEditFixtures.Bump();
        var patch = TerrainEditFixtures.BumpKey;
        var self = new MapVertexOwner(patch, MapLatticeAddress.Corner(0, 0));
        var valid = new MapCornerOwnerChange(patch, 0, 0, null, self);
        var invalid = defect switch
        {
            "stale" => valid with { OldOwner = self },
            "address" => valid with { NewOwner = self with { Address = MapLatticeAddress.Corner(1, 0) } },
            _ => valid
        };
        var before = TerrainEditFixtures.Digest(set);
        var error = Assert.Throws<MapDocumentException>(() => MapTerrainEdits.Prepare(set,
            new MapReassignCornerOwner(defect == "duplicate" ? [invalid, invalid] : [invalid])));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(patch, error.Patch);
        Assert.Equal((0, 0), (error.CellX, error.CellZ));
        Assert.Equal(before, TerrainEditFixtures.Digest(set));
        Assert.NotNull(MapTerrainEdits.Prepare(set, new MapReassignCornerOwner([valid])));
    }
}
