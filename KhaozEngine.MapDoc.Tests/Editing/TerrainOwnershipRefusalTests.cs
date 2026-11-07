using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class TerrainOwnershipRefusalTests
{
    [Theory]
    [InlineData("regions")]
    [InlineData("heights")]
    [InlineData("cells")]
    public void RefusedCount_RetainsTheProducerValueThatFailed(string field)
    {
        var set = TerrainEditFixtures.Bump();
        var key = TerrainEditFixtures.BumpKey;
        var cell = set.Patches[key].Cells[0];
        var before = TerrainEditFixtures.Digest(set);
        var error = Assert.Throws<MapDocumentException>(() =>
        {
            if (field == "regions")
                MapFinePatchConversion.Convert(set, new(key.SurfaceId,
                    new TerrainEditFixtures.ChangingCountList<MapCellRect>([new(1, 1, 2, 2)]), 2, "fine", false));
            else MapTerrainEdits.Prepare(set, field == "heights"
                ? new MapSetCornerHeights(key, 0, 0, 1, 1, new TerrainEditFixtures.ChangingCountList<int>([0]))
                : new MapSetCells(key, 0, 0, 1, 1, new TerrainEditFixtures.ChangingCountList<MapSurfaceCell>([cell])));
        });
        Assert.Equal(field == "regions" ? MapDocumentRefusal.TerrainConversionRegionsMissing
            : MapDocumentRefusal.SchemaArrayLengthMismatch, error.Reason);
        Assert.Equal(0, error.ActualCount);
        Assert.Equal(before, TerrainEditFixtures.Digest(set));
        if (field == "regions") Assert.NotEmpty(MapFinePatchConversion.Convert(set,
            new(key.SurfaceId, [new(1, 1, 2, 2)], 2, "fine", false)).Cells);
        else Assert.NotNull(MapTerrainEdits.Prepare(set, field == "heights"
            ? new MapSetCornerHeights(key, 0, 0, 1, 1, [0]) : new MapSetCells(key, 0, 0, 1, 1, [cell])));
    }

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
    [Theory]
    [InlineData("upsert", MapDocumentRefusal.IdentityDuplicate)]
    [InlineData("remove", MapDocumentRefusal.IdentityDuplicate)]
    [InlineData("conflict", MapDocumentRefusal.TerrainTopologyWritesConflict)]
    public void SurfaceWrites_SelectTheFirstDuplicateOrConflictBranch(string defect, MapDocumentRefusal reason)
    {
        var set = TerrainEditFixtures.Bump();
        var surface = set.Refs.Single();
        var edit = defect switch
        {
            "upsert" => new MapReplaceTopology([], [], [surface, surface], []),
            "remove" => new MapReplaceTopology([], [], [], [surface.Id, surface.Id]),
            _ => new MapReplaceTopology([], [], [surface], [surface.Id])
        };
        var before = TerrainEditFixtures.Digest(set);
        var error = Assert.Throws<MapDocumentException>(() => MapTerrainEdits.Prepare(set, edit));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(defect == "upsert" ? "UpsertSurfaces" : defect == "remove" ? "RemoveSurfaces" : "UpsertSurfaces/RemoveSurfaces", error.FieldPath);
        Assert.Equal(before, TerrainEditFixtures.Digest(set));
        Assert.Equal(surface, Assert.Single(set.Refs));
        Assert.NotNull(MapTerrainEdits.Prepare(set, new MapReplaceTopology([], [], [surface], [])));
    }

    [Fact]
    public void ExistingCornerOwners_RefuseAmbiguityAndAcceptAnExplicitSharedOwner()
    {
        var set = TerrainEditFixtures.Bump();
        var key = TerrainEditFixtures.BumpKey;
        var supplied = set.Patches[key];
        var left = supplied.Clone();
        left.Key = left.Key with { SlotX = -1 };
        left.CellMinX = 60;
        set.Patches.Add(left.Key, left);
        var before = TerrainEditFixtures.Digest(set);
        var error = Assert.Throws<MapDocumentException>(() => MapTerrainEdits.Prepare(set,
            new MapReplaceTopology([supplied], [], [], [])));
        Assert.Equal(MapDocumentRefusal.TerrainCornerOwnerAmbiguous, error.Reason);
        Assert.Equal(key.SurfaceId, error.SurfaceId);
        Assert.Equal(MapLatticeAddress.Corner(0, 0), error.Address);
        Assert.Equal(before, TerrainEditFixtures.Digest(set));
        // These patches share only x = 0. Each of its five corners now has one explicit owner.
        for (int z = 0; z <= left.Depth; z++)
            left.CornerDependencies.Add(new(left.Width, z, new(key, MapLatticeAddress.Corner(0, z))));
        Assert.NotNull(MapTerrainEdits.Prepare(set, new MapReplaceTopology([supplied], [], [], [])));
    }
}
