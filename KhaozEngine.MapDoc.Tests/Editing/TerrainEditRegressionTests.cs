using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class TerrainEditRegressionTests
{
    const MapNativeInvalidation Geometry = MapNativeInvalidation.Terrain | MapNativeInvalidation.Physics |
        MapNativeInvalidation.Nav | MapNativeInvalidation.Residency;
    static readonly MapPatchKey Bump = TerrainEditFixtures.BumpKey;

    static MapTerrainEditResult Prepare(MapSurfaceSet set, MapTerrainEdit edit) => MapTerrainEdits.Prepare(set, edit);
    static MapPatchKey Tile(long x, long z) => new("tiles", x, z);

    // Four full patches meeting at absolute corner (64, 64). Shared corners carry the lowest-key incident owner.
    static MapSurfaceSet FourPatches(Func<long, long, int> height)
    {
        var set = new MapSurfaceSet();
        set.Refs.Add(new("tiles", new(new(1, 1), new(1, 100), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0),
            MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, ""));
        MapPatchKey[] keys = { Tile(0, 0), Tile(1, 0), Tile(0, 1), Tile(1, 1) };
        foreach (MapPatchKey key in keys)
        {
            var heights = new int[65 * 65];
            for (int z = 0; z <= 64; z++)
                for (int x = 0; x <= 64; x++) heights[z * 65 + x] = height(key.SlotX * 64 + x, key.SlotZ * 64 + z);
            var patch = new MapSurfacePatch
            {
                Key = key,
                Width = 64,
                Depth = 64,
                Heights = heights,
                Cells = Enumerable.Repeat(new MapSurfaceCell(1, 0, MapOverlayCut.Full, 0,
                    MapCellFlags.None, MapCellTopology.Auto), 4096).ToArray(),
                Presence = Enumerable.Repeat(ulong.MaxValue, 64).ToArray(),
            };
            for (int z = 0; z <= 64; z++)
                for (int x = 0; x <= 64; x++)
                {
                    long ax = key.SlotX * 64 + x, az = key.SlotZ * 64 + z;
                    MapPatchKey owner = keys.Where(k => ax >= k.SlotX * 64 && ax <= k.SlotX * 64 + 64 &&
                        az >= k.SlotZ * 64 && az <= k.SlotZ * 64 + 64).Min();
                    if (owner != key) patch.CornerDependencies.Add(new(x, z, new(owner, MapLatticeAddress.Corner(ax, az))));
                }
            set.Patches.Add(key, patch);
        }
        return set;
    }

    [Fact]
    public void Smooth_ReadsAConsistentHaloWhereTwoNeighboursMeet()
    {
        // The review scenario: halo corner (-1, 0) of slot (1, 1) lies in slots (0, 0) and (0, 1).
        MapSurfaceSet flat = FourPatches((_, _) => 500);
        MapTerrainEditResult unchanged = Prepare(flat, new MapSmoothCorners(Tile(1, 1), 0, 0, 1, 1, 1));
        Assert.Equal(500, unchanged.Candidate.Patches[Tile(1, 1)].Height(0, 0));

        // x squared plus z averages to 37446 / 9 at (64, 64), which rounds to 4161 from 4160.
        MapSurfaceSet curved = FourPatches((x, z) => checked((int)(x * x + z)));
        string[] digest = TerrainEditFixtures.Digest(curved);
        const int Expected = 4161;
        MapTerrainEditResult result = Prepare(curved, new MapCompositeEdit(new MapTerrainEdit[]
        {
            new MapSmoothCorners(Tile(1, 1), 0, 0, 1, 1, 1),
            new MapSetCornerHeights(Tile(0, 0), 64, 64, 1, 1, new[] { Expected }),
            new MapSetCornerHeights(Tile(1, 0), 0, 64, 1, 1, new[] { Expected }),
            new MapSetCornerHeights(Tile(0, 1), 64, 0, 1, 1, new[] { Expected }),
        }));
        Assert.Equal(4160, curved.Patches[Tile(1, 1)].Height(0, 0));
        Assert.Equal(Expected, result.Candidate.Patches[Tile(1, 1)].Height(0, 0));
        Assert.Equal(Expected, result.Candidate.Patches[Tile(0, 0)].Height(64, 64));
        Assert.Equal(new[] { Tile(0, 0), Tile(1, 0), Tile(0, 1), Tile(1, 1) }, result.WriteSet.Patches);
        Assert.Equal(Geometry, result.Effects.Invalidates);
        Assert.Equal(digest, TerrainEditFixtures.Digest(curved));
    }

    [Fact]
    public void Smooth_RefusesAHaloCornerWhoseNeighboursDisagree()
    {
        MapSurfaceSet set = FourPatches((_, _) => 500);
        set.Patches[Tile(0, 1)].Heights[63] = 501;
        var before = TerrainEditFixtures.Digest(set);
        var error = Assert.Throws<MapDocumentException>(() =>
            Prepare(set, new MapSmoothCorners(Tile(1, 1), 0, 0, 1, 1, 1)));
        Assert.Equal(MapDocumentRefusal.GeometryVertexHeightMismatch, error.Reason);
        Assert.Equal(MapLatticeAddress.Corner(63, 64), error.Address);
        Assert.Equal(before, TerrainEditFixtures.Digest(set));
        set.Patches[Tile(0, 1)].Heights[63] = 500;
        Assert.Equal(500, Prepare(set, new MapSmoothCorners(Tile(1, 1), 0, 0, 1, 1, 1)).Candidate.Patches[Tile(1, 1)].Height(0, 0));
    }

    [Fact]
    public void SetPresence_TogglesSlotCellsAsGeometry()
    {
        MapSurfaceSet set = TerrainEditFixtures.Bump();
        string[] digest = TerrainEditFixtures.Digest(set);
        MapTerrainEditResult cleared = Prepare(set, new MapSetPresence(Bump, new[] { 0, 65 }, false));
        MapSurfacePatch patch = cleared.Candidate.Patches[Bump];
        Assert.False(patch.IsPresent(0, 0));
        Assert.False(patch.IsPresent(1, 1));
        Assert.True(patch.IsPresent(1, 0));
        Assert.Equal(Geometry, cleared.Effects.Invalidates);
        Assert.Equal(new[] { Bump }, cleared.WriteSet.Patches);
        Assert.Equal(digest, TerrainEditFixtures.Digest(set));
        MapTerrainEditResult restored = Prepare(cleared.Candidate, new MapSetPresence(Bump, new[] { 0, 65 }, true));
        Assert.Equal(digest, TerrainEditFixtures.Digest(restored.Candidate));
    }

    [Theory, InlineData(-1), InlineData(4096), InlineData(4), InlineData(256)]
    public void SetPresence_RefusesCellsOutsideThePatch(int cell)
    {
        MapSurfaceSet set = TerrainEditFixtures.Bump();
        string[] digest = TerrainEditFixtures.Digest(set);
        var error = Assert.Throws<MapDocumentException>(() => Prepare(set, new MapSetPresence(Bump, new[] { cell }, false)));
        Assert.Equal(cell is < 0 or >= 4096 ? MapDocumentRefusal.TerrainEditRegionOutOfBounds
            : MapDocumentRefusal.SurfaceAddressOutOfPatch, error.Reason);
        Assert.Equal(Bump, error.Patch);
        Assert.Equal(digest, TerrainEditFixtures.Digest(set));
        Assert.False(Prepare(set, new MapSetPresence(Bump, [3 * 64 + 3], false)).Candidate.Patches[Bump].IsPresent(3, 3));
    }

    [Theory, InlineData(-1, 0, 1, 1), InlineData(0, -1, 1, 1), InlineData(0, 0, 0, 1), InlineData(0, 0, 1, 0),
     InlineData(5, 0, 1, 1), InlineData(4, 0, 2, 1), InlineData(0, 4, 1, 2), InlineData(int.MaxValue, 0, 1, 1)]
    public void SetCornerHeights_RefusesARegionOutsideTheCornerRectangle(int x, int z, int width, int depth)
    {
        MapSurfaceSet set = TerrainEditFixtures.Bump();
        int count = Math.Max(0, width) * Math.Max(0, depth);
        var before = TerrainEditFixtures.Digest(set);
        var error = Assert.Throws<MapDocumentException>(() => Prepare(set, new MapSetCornerHeights(Bump, x, z, width, depth, new int[count])));
        Assert.Equal(MapDocumentRefusal.TerrainEditRegionOutOfBounds, error.Reason);
        Assert.Equal((x, z, width, depth), (error.CellX, error.CellZ, error.Width, error.Depth));
        Assert.Equal(before, TerrainEditFixtures.Digest(set));
        // The final corner at (Width, Depth) is accepted, with the smallest nonempty region.
        Assert.NotNull(Prepare(set, new MapSetCornerHeights(Bump, 4, 4, 1, 1, [0])));
    }

    [Theory, InlineData(3), InlineData(5)]
    public void SetCornerHeights_RefusesAValueCountThatDiffers(int count)
    {
        var set = TerrainEditFixtures.Bump();
        var before = TerrainEditFixtures.Digest(set);
        var error = Assert.Throws<MapDocumentException>(() =>
            Prepare(set, new MapSetCornerHeights(Bump, 0, 0, 2, 2, new int[count])));
        Assert.Equal(MapDocumentRefusal.SchemaArrayLengthMismatch, error.Reason);
        Assert.Equal("Values", error.FieldPath);
        Assert.Equal(count, error.ActualCount);
        Assert.Equal(4, error.ExpectedCount);
        Assert.Equal(before, TerrainEditFixtures.Digest(set));
        Assert.NotNull(Prepare(set, new MapSetCornerHeights(Bump, 0, 0, 2, 2, new int[4])));
    }

    [Theory, InlineData(-1, 0, 1, 1), InlineData(0, -1, 1, 1), InlineData(0, 0, 0, 1), InlineData(0, 0, 1, 0),
     InlineData(4, 0, 1, 1), InlineData(3, 0, 2, 1), InlineData(0, 3, 1, 2)]
    public void SetCells_RefusesARegionOutsideTheCellRectangle(int x, int z, int width, int depth)
    {
        MapSurfaceSet set = TerrainEditFixtures.Bump();
        MapSurfaceCell cell = set.Patches[Bump].Cells[0];
        int count = Math.Max(0, width) * Math.Max(0, depth);
        var before = TerrainEditFixtures.Digest(set);
        var error = Assert.Throws<MapDocumentException>(() => Prepare(set, new MapSetCells(Bump, x, z, width, depth, Enumerable.Repeat(cell, count).ToArray())));
        Assert.Equal(MapDocumentRefusal.TerrainEditRegionOutOfBounds, error.Reason);
        Assert.Equal((x, z, width, depth), (error.CellX, error.CellZ, error.Width, error.Depth));
        Assert.Equal(before, TerrainEditFixtures.Digest(set));
        Assert.NotNull(Prepare(set, new MapSetCells(Bump, 3, 3, 1, 1, [cell])));
    }

    [Theory, InlineData(1), InlineData(3)]
    public void SetCells_RefusesACellCountThatDiffers(int count)
    {
        MapSurfaceSet set = TerrainEditFixtures.Bump();
        MapSurfaceCell cell = set.Patches[Bump].Cells[0];
        var before = TerrainEditFixtures.Digest(set);
        var error = Assert.Throws<MapDocumentException>(() =>
            Prepare(set, new MapSetCells(Bump, 0, 0, 2, 1, Enumerable.Repeat(cell, count).ToArray())));
        Assert.Equal(MapDocumentRefusal.SchemaArrayLengthMismatch, error.Reason);
        Assert.Equal("Cells", error.FieldPath);
        Assert.Equal(count, error.ActualCount);
        Assert.Equal(2, error.ExpectedCount);
        Assert.Equal(before, TerrainEditFixtures.Digest(set));
        Assert.NotNull(Prepare(set, new MapSetCells(Bump, 0, 0, 2, 1, [cell, cell])));
    }

    [Fact]
    public void Reanchor_AloneInvalidatesExactlyResidency()
    {
        MapSurfaceSet set = TerrainEditFixtures.WithFarAnchors();
        var farWest = new MapPatchKey("far", -300, 0);
        var farEast = new MapPatchKey("far", 300, 0);
        MapTerrainEditResult result = Prepare(set, new MapReanchorRecords(new[] { new MapRecordAnchorMove("far-chain", farWest, farEast) }));
        Assert.Equal(MapNativeInvalidation.Residency, result.Effects.Invalidates);
        Assert.Equal(new MapRecordRef("far-chain", farEast),
            result.Candidate.AllRecords().OfType<MapWallStrip>().Single(w => w.Id == "far-wall").UpperChain);
    }
}
