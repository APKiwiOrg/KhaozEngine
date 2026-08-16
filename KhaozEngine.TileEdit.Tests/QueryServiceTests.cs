using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.TileEdit;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileEdit;

/// <summary>Tests for the read side against <see cref="TileEditTestWorld.Build"/>'s small world. The ASCII maps
/// are pinned BY HAND over the 4 by 4 rect at the origin, rows north first, because the row order is the one
/// thing about these maps that cannot be checked by reading the code that produced them.</summary>
public class QueryServiceTests
{
    static readonly TileRect Corner = new(0, 0, 4, 4);

    sealed class Fixture : IDisposable
    {
        public TempDir Temp { get; } = new();
        public TileEditSession Session { get; }
        public QueryService Query { get; }
        public MutationService Mutate { get; }
        public long WallId { get; }
        public long TreeId { get; }

        public Fixture()
        {
            Session = TileEditTestWorld.NewSession(Temp.Sub("world"));
            Query = new QueryService(Session);
            Mutate = new MutationService(Session);
            (WallId, TreeId) = TileEditTestWorld.Build(Mutate);
        }

        public void Dispose() => Temp.Dispose();
    }

    [Fact]
    public void TileGet_DecodesEveryLayerAndTheDerivedCollision()
    {
        using var f = new Fixture();

        // (1, 1) carries the west-facing wall, so its own west edge is walled while the tile stays passable.
        TileInfo wall = f.Query.TileGet(1, 1, 0);
        Assert.Equal((ushort)1, wall.Underlay);
        Assert.Equal("grass", wall.UnderlayName);
        Assert.Equal((ushort)0, wall.Overlay);
        Assert.Null(wall.OverlayName);
        Assert.Equal("Full", wall.Shape);
        Assert.Equal("None", wall.Settings);
        Assert.Equal("WallW", wall.Collision);
        Assert.False(wall.Blocked);
        Assert.Equal("(0, 0)", wall.Region);
        // SW, SE, NW, NE of tile (1, 1): only its SW corner was lifted, to 300 cm.
        Assert.Equal(new short[] { 300, 0, 0, 0 }, wall.CornersCm);

        TileInfo overlay = f.Query.TileGet(1, 0, 0);
        Assert.Equal((ushort)2, overlay.Overlay);
        Assert.Equal("dirt", overlay.OverlayName);
        Assert.Equal("DiagonalHalf", overlay.Shape);
        Assert.Equal(1, overlay.Rotation);

        TileInfo blocked = f.Query.TileGet(2, 2, 0);
        Assert.Equal("Blocked", blocked.Settings);
        Assert.Equal("Blocked", blocked.Collision);
        Assert.True(blocked.Blocked);

        // Region (1, 1) was never created, so this tile is outside the authored world.
        TileInfo outside = f.Query.TileGet(100, 100, 0);
        Assert.Equal("missing", outside.Region);
        Assert.True(outside.Blocked);
    }

    [Fact]
    public void TilesGetRect_PinsEveryLayerNorthFirst()
    {
        using var f = new Fixture();

        // Row 0 is z = 3 (north), row 3 is z = 0 (south). Within a row, west to east.
        Assert.Equal(new[] { "1111", "1111", "1111", "2111" }, f.Query.TilesGetRect(Corner, 0, "underlay").Rows);
        Assert.Equal(new[] { "....", "....", "....", ".2.." }, f.Query.TilesGetRect(Corner, 0, "overlay").Rows);
        Assert.Equal(new[] { "....", "....", "....", ".d.." }, f.Query.TilesGetRect(Corner, 0, "shape").Rows);
        Assert.Equal(new[] { "....", "..b.", "....", "...." }, f.Query.TilesGetRect(Corner, 0, "settings").Rows);
        // (3, 3) is the tree and (2, 2) the blocked ground. The wall at (1, 1) puts a west edge on itself and
        // the mirrored east edge on (0, 1), which is the row that reads "||..".
        Assert.Equal(new[] { "...#", "..#.", "||..", "...." }, f.Query.TilesGetRect(Corner, 0, "collision").Rows);
        Assert.Equal(new[] { "...#", "..#.", "....", "...." }, f.Query.WalkableRect(Corner, 0).Rows);

        TileMapResult map = f.Query.TilesGetRect(Corner, 0, "collision");
        Assert.Equal(new RectInfo(0, 0, 4, 4), map.Rect);
        Assert.Equal("collision", map.Layer);
        Assert.Contains("blocked", map.Legend, StringComparison.Ordinal);
    }

    [Fact]
    public void TilesGetRect_ShowsVoidWhereTheWorldHasNoRegion()
    {
        using var f = new Fixture();

        // A rect straddling the east edge of region (0, 0): the two columns past it have no region at all.
        IReadOnlyList<string> rows = f.Query.TilesGetRect(new TileRect(62, 0, 4, 2), 0, "collision").Rows;

        Assert.Equal(new[] { "..vv", "..vv" }, rows);
    }

    [Fact]
    public void TilesGetRect_RefusesAnUnknownLayerAndAnEmptyRect()
    {
        using var f = new Fixture();

        ArgumentException layer = Assert.Throws<ArgumentException>(() => f.Query.TilesGetRect(Corner, 0, "heights"));
        Assert.Contains("underlay", layer.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => f.Query.TilesGetRect(new TileRect(0, 0, 0, 4), 0, "underlay"));
    }

    [Fact]
    public void HeightGetRect_ReadsTheCornerLatticeNorthFirst()
    {
        using var f = new Fixture();

        HeightMapResult heights = f.Query.HeightGetRect(new TileRect(0, 0, 2, 2), 0);

        Assert.Equal(new RectInfo(0, 0, 2, 2), heights.CornerRect);
        Assert.Equal(2, heights.Rows.Count);
        Assert.Equal(new short[] { 200, 300 }, heights.Rows[0]);
        Assert.Equal(new short[] { 0, 100 }, heights.Rows[1]);
    }

    [Fact]
    public void CollisionAt_ReportsTheFlagsAndTheStepsAWallForbids()
    {
        using var f = new Fixture();

        CollisionInfo walled = f.Query.CollisionAt(1, 1, 0);

        Assert.Equal("WallW", walled.Flags);
        Assert.False(walled.Blocked);
        Assert.False(walled.CanStepWest);
        Assert.True(walled.CanStepEast);
        Assert.True(walled.CanStepNorth);
        Assert.True(walled.CanStepSouth);

        CollisionInfo open = f.Query.CollisionAt(5, 5, 0);
        Assert.Equal("None", open.Flags);
        Assert.True(open.CanStepNorth && open.CanStepEast && open.CanStepSouth && open.CanStepWest);
    }

    [Fact]
    public void IsWalkable_AppliesTheAgentFootprint()
    {
        using var f = new Fixture();

        Assert.True(f.Query.IsWalkable(0, 0, 0).Walkable);
        Assert.False(f.Query.IsWalkable(3, 3, 0).Walkable);
        // A 2x2 agent anchored at (2, 2) covers the blocked tile AND the tree.
        Assert.False(f.Query.IsWalkable(2, 2, 0, agentSize: 2).Walkable);
        Assert.True(f.Query.IsWalkable(6, 6, 0, agentSize: 2).Walkable);
        Assert.Equal(2, f.Query.IsWalkable(6, 6, 0, agentSize: 2).AgentSize);
    }

    [Fact]
    public void Path_WalksToTheGoalAndReportsAnUnreachableOne()
    {
        using var f = new Fixture();

        PathResult reached = f.Query.Path(0, 0, 3, 0, 0);

        Assert.True(reached.Reached);
        Assert.Equal(3, reached.Length);
        Assert.Equal(new TileStep(3, 0), reached.Steps[^1]);

        // Region (1, 1) does not exist, so nothing over there can be walked to.
        PathResult unreachable = f.Query.Path(0, 0, 100, 100, 0);
        Assert.False(unreachable.Reached);
    }

    [Fact]
    public void ObjectQueries_ListFindAndDescribeFootprints()
    {
        using var f = new Fixture();
        f.Mutate.ObjectSetTags(f.TreeId, new[] { "forest" });
        long bench = f.Mutate.ObjectPlace("bench", 8, 8, 0, rotation: 1).ObjectId;

        IReadOnlyList<ObjectInfo> inRect = f.Query.ObjectsInRect(Corner, 0);
        Assert.Equal(new[] { f.WallId, f.TreeId }, inRect.Select(o => o.Id).ToArray());

        ObjectInfo tree = f.Query.ObjectGet(f.TreeId);
        Assert.Equal("tree", tree.ArchetypeId);
        Assert.Equal(new RectInfo(3, 3, 1, 1), tree.Footprint);
        Assert.Equal(new[] { "forest" }, tree.Tags);

        // A 1x2 bench turned one quarter turn covers 2 by 1.
        Assert.Equal(new RectInfo(8, 8, 2, 1), f.Query.ObjectGet(bench).Footprint);

        Assert.Equal(new[] { f.TreeId }, f.Query.ObjectFind(archetypeId: "tree").Select(o => o.Id).ToArray());
        Assert.Equal(new[] { f.TreeId }, f.Query.ObjectFind(tag: "forest").Select(o => o.Id).ToArray());
        Assert.Equal(3, f.Query.ObjectFind().Count);
        Assert.Throws<TileWorldException>(() => f.Query.ObjectGet(9999));
    }

    [Fact]
    public void MarkerAndRegionLists_ReportWhatTheWorldHolds()
    {
        using var f = new Fixture();

        MarkerInfo marker = Assert.Single(f.Query.MarkerList());
        Assert.Equal("spawn", marker.Name);
        Assert.Equal(new[] { "start" }, marker.Tags);

        RegionInfo region = Assert.Single(f.Query.RegionList());
        Assert.Equal(0, region.Rx);
        Assert.Equal(new RectInfo(0, 0, 64, 64), region.Rect);
        Assert.Equal(2, region.ObjectCount);
        Assert.Equal(1, region.MarkerCount);
    }

    [Fact]
    public void CatalogList_ReturnsOneKindAndRefusesAnother()
    {
        using var f = new Fixture();

        CatalogListResult materials = f.Query.CatalogList("materials");
        Assert.Empty(materials.Archetypes);
        Assert.Equal(new ushort[] { 1, 2, 3 }, materials.Materials.Select(m => m.Id).ToArray());
        Assert.Equal("Water", materials.Materials.Single(m => m.Id == 3).Kind);

        CatalogListResult archetypes = f.Query.CatalogList("ARCHETYPES");
        Assert.Empty(archetypes.Materials);
        ArchetypeInfo bench = archetypes.Archetypes.Single(a => a.Id == "bench");
        Assert.Equal(1, bench.SizeX);
        Assert.Equal(2, bench.SizeZ);
        Assert.Equal("Solid", bench.CollisionKind);

        Assert.Throws<ArgumentException>(() => f.Query.CatalogList("props"));
    }

    [Fact]
    public void PrefabList_FindsWhatPrefabExtractWrote()
    {
        using var f = new Fixture();
        f.Mutate.PrefabExtract(new TileRect(0, 0, 2, 2), 0, 1, "prefabs/hut.json");

        IReadOnlyList<PrefabFileInfo> prefabs = f.Query.PrefabList("prefabs");

        PrefabFileInfo hut = Assert.Single(prefabs);
        Assert.Equal("hut", hut.Name);
        Assert.True(hut.SizeBytes > 0);
        Assert.Throws<TileWorldException>(() => f.Query.PrefabList("nowhere"));
    }
}
