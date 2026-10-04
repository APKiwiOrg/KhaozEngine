using System;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Sharding;

/// <summary>
/// A host built with a cell origin keys, hands off, ghosts and frames its cells on the shifted grid. Cell size 100
/// and origin (64, -64) put cell (0, 0) at x [64, 164) and y [-64, 36), so the boundary with cell (1, 0) is x = 164
/// and the zero-origin boundary at x = 100 is in the middle of a cell.
/// </summary>
public class ShardHostCellOriginTests
{
    private static readonly Vector2 Origin = new(64f, -64f);

    private struct Pos : IComponent { public float X; public float Y; }

    private static ReplicationRegistry PosRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<Pos>(1,
            (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            br => new Pos { X = br.ReadSingle(), Y = br.ReadSingle() });
        return r;
    }

    private static bool PosAccessor(World world, Entity e, out float x, out float y)
    {
        if (world.TryGet(e, out Pos p)) { x = p.X; y = p.Y; return true; }
        x = y = 0f;
        return false;
    }

    private static ShardHost ShiftedHost(ReplicationRegistry registry, float margin) =>
        new(cellSize: 100f, tickSeconds: 0.1f, registry, interestCellSize: 100f,
            overlapMargin: margin, positionAccessor: PosAccessor, cellOrigin: Origin);

    private static void SpawnOwned(ShardHost host, int netId, float x, float y)
    {
        Entity e = host.SpawnAt(x, y, out CellSim cell);
        cell.World.Set(e, new NetId(netId));
        cell.World.Set(e, new Pos { X = x, Y = y });
    }

    private static void MoveOwner(ShardHost host, int netId, float x, float y)
    {
        Assert.True(host.TryGetOwner(netId, out CellSim owner, out Entity e));
        owner.World.Set(e, new Pos { X = x, Y = y });
    }

    private static CellCoord OwnerCoord(ShardHost host, int netId)
    {
        Assert.True(host.TryGetOwner(netId, out CellSim owner, out _));
        return owner.Coord;
    }

    [Fact]
    public void DefaultOrigin_IsTheWorldOrigin()
    {
        var host = new ShardHost(100f, 0.1f, PosRegistry());
        Assert.Equal(Vector2.Zero, host.CellOrigin);
        Assert.Equal(new CellCoord(1, 0), host.CoordFor(120f, 0f));
    }

    [Fact]
    public void Host_ExposesItsGrid_AndKeysOnIt()
    {
        ShardHost host = ShiftedHost(PosRegistry(), margin: 0f);
        Assert.Equal(Origin, host.CellOrigin);
        Assert.Equal(Origin, host.Grid.Origin);
        Assert.Equal(100f, host.Grid.CellSize);
        Assert.Equal(new CellCoord(0, 0), host.CoordFor(120f, 0f));
        Assert.Equal(new CellCoord(-1, 0), host.CellFor(0f, 0f).Coord);
    }

    [Fact]
    public void Handoff_HappensAtTheShiftedBoundary_NotAtTheZeroOriginOne()
    {
        ReplicationRegistry registry = PosRegistry();
        ShardHost host = ShiftedHost(registry, margin: 0f);
        SpawnOwned(host, 7, 90f, 0f);
        Assert.Equal(new CellCoord(0, 0), OwnerCoord(host, 7));

        foreach (float x in new[] { 110f, 140f, 163.5f })   // past x = 100 but short of the shifted edge at 164
        {
            MoveOwner(host, 7, x, 0f);
            host.ProcessHandoffs();
            Assert.Equal(1, host.OwnerCount(7));
            Assert.Equal(new CellCoord(0, 0), OwnerCoord(host, 7));
        }

        MoveOwner(host, 7, 170f, 0f);
        host.ProcessHandoffs();
        Assert.Equal(1, host.OwnerCount(7));
        Assert.Equal(new CellCoord(1, 0), OwnerCoord(host, 7));
        Assert.Equal(2, host.CellCount);
    }

    [Fact]
    public void Ghosting_MirrorsAcrossTheShiftedEdge_AndIgnoresTheZeroOriginOne()
    {
        ReplicationRegistry registry = PosRegistry();
        ShardHost host = ShiftedHost(registry, margin: 10f);
        CellSim east = host.CellFor(200f, 0f);   // (1, 0)
        CellSim west = host.CellFor(0f, 0f);     // (-1, 0)
        Assert.Equal(new CellCoord(1, 0), east.Coord);
        Assert.Equal(new CellCoord(-1, 0), west.Coord);

        SpawnOwned(host, 7, 158f, 0f);   // 6 from the shifted east edge at x = 164
        SpawnOwned(host, 8, 102f, 0f);   // 2 from x = 100, which is no edge on this grid
        SpawnOwned(host, 9, 68f, 0f);    // 4 from the shifted west edge at x = 64

        host.SyncGhosts();

        Assert.True(east.TryGetGhost(7, out Entity ghost));
        Assert.Equal(158f, east.World.Get<Pos>(ghost).X);
        Assert.Equal(new CellCoord(0, 0), east.World.Get<Ghost>(ghost).Source);
        Assert.False(west.TryGetGhost(7, out _));

        Assert.False(east.TryGetGhost(8, out _));
        Assert.False(west.TryGetGhost(8, out _));

        Assert.True(west.TryGetGhost(9, out _));
        Assert.False(east.TryGetGhost(9, out _));
    }

    [Fact]
    public void Ghost_FollowsABodyWalkingAcrossTheShiftedBoundary()
    {
        ReplicationRegistry registry = PosRegistry();
        ShardHost host = ShiftedHost(registry, margin: 10f);
        CellSim a = host.CellFor(114f, 0f);
        CellSim b = host.CellFor(200f, 0f);
        SpawnOwned(host, 7, 150f, 0f);

        host.ProcessHandoffs();
        host.SyncGhosts();
        Assert.False(b.TryGetGhost(7, out _));   // 14 from the edge, outside the margin

        MoveOwner(host, 7, 160f, 0f);
        host.ProcessHandoffs();
        host.SyncGhosts();
        Assert.Equal(a.Coord, OwnerCoord(host, 7));
        Assert.True(b.TryGetGhost(7, out _));    // owned by A, ghosted into B

        MoveOwner(host, 7, 168f, 0f);
        host.ProcessHandoffs();
        host.SyncGhosts();
        Assert.Equal(b.Coord, OwnerCoord(host, 7));
        Assert.Equal(1, host.OwnerCount(7));
        Assert.True(a.TryGetGhost(7, out Entity back));   // owned by B, ghosted back into A
        Assert.Equal(168f, a.World.Get<Pos>(back).X);
        Assert.False(b.TryGetGhost(7, out _));
    }

    [Fact]
    public void Frame_IsNearestTheShiftedCellCentre()
    {
        var host = new ShardHost(100f, 0.1f, PosRegistry(), 100f, overlapMargin: 0f,
            frameAnchoring: true, cellOrigin: Origin);
        var zeroOrigin = new ShardHost(100f, 0.1f, PosRegistry(), 100f, overlapMargin: 0f, frameAnchoring: true);

        // Cell (0, 0)'s centre is (114, -14) on the shifted grid, which rounds to frame (1, 0), while the zero-origin
        // centre (50, 50) rounds to the world origin.
        Assert.Equal(WorldFrame.Nearest(114f, -14f), host.FrameFor(new CellCoord(0, 0)));
        Assert.NotEqual(WorldFrame.Origin, host.FrameFor(new CellCoord(0, 0)));
        Assert.Equal(WorldFrame.Origin, zeroOrigin.FrameFor(new CellCoord(0, 0)));
        Assert.Equal(host.FrameFor(new CellCoord(0, 0)), host.CellFor(114f, -14f).Frame);
    }

    [Fact]
    public void Constructor_RejectsANonFiniteOrigin()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ShardHost(100f, 0.1f, PosRegistry(), 100f, overlapMargin: 0f, cellOrigin: new Vector2(float.NaN, 0f)));
    }
}
