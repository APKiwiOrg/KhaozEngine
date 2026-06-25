using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Sharding;

public class CellLinkTests
{
    private static CellMessage Msg(CellCoord source, CellCoord target, byte payload,
        CellMessageKind kind = CellMessageKind.GhostSync) =>
        new(source, target, kind, new[] { payload });

    [Fact]
    public void Drain_ReturnsMessagesForTargetAndKind_InFifoOrder()
    {
        ICellLink link = new InProcessCellLink();
        var target = new CellCoord(1, 0);
        link.Send(Msg(new CellCoord(0, 0), target, 1));
        link.Send(Msg(new CellCoord(2, 0), target, 2));

        var drained = link.Drain(target, CellMessageKind.GhostSync);

        Assert.Equal(2, drained.Count);
        Assert.Equal(1, drained[0].Payload[0]);
        Assert.Equal(2, drained[1].Payload[0]);
        Assert.Equal(new CellCoord(0, 0), drained[0].Source);
    }

    [Fact]
    public void Drain_ClearsThatKindFromTheTargetsQueue()
    {
        ICellLink link = new InProcessCellLink();
        var target = new CellCoord(1, 0);
        link.Send(Msg(new CellCoord(0, 0), target, 1));

        Assert.Single(link.Drain(target, CellMessageKind.GhostSync));
        Assert.Empty(link.Drain(target, CellMessageKind.GhostSync));   // drained once, now empty
    }

    [Fact]
    public void Drain_UnknownTarget_ReturnsEmpty()
    {
        ICellLink link = new InProcessCellLink();
        Assert.Empty(link.Drain(new CellCoord(5, 5), CellMessageKind.GhostSync));
    }

    [Fact]
    public void Messages_AreIsolatedPerTarget()
    {
        ICellLink link = new InProcessCellLink();
        var a = new CellCoord(0, 0);
        var b = new CellCoord(1, 0);
        link.Send(Msg(new CellCoord(9, 9), a, 10));
        link.Send(Msg(new CellCoord(9, 9), b, 20));

        Assert.Equal(10, link.Drain(a, CellMessageKind.GhostSync)[0].Payload[0]);
        Assert.Equal(20, link.Drain(b, CellMessageKind.GhostSync)[0].Payload[0]);
    }

    [Fact]
    public void Drain_IsKindScoped_LeavesOtherKindsQueued()
    {
        ICellLink link = new InProcessCellLink();
        var target = new CellCoord(1, 0);
        link.Send(Msg(new CellCoord(0, 0), target, 1, CellMessageKind.GhostSync));
        link.Send(Msg(new CellCoord(0, 0), target, 2, CellMessageKind.Migrate));
        link.Send(Msg(new CellCoord(0, 0), target, 3, CellMessageKind.MigrateAck));

        var migrates = link.Drain(target, CellMessageKind.Migrate);
        Assert.Single(migrates);
        Assert.Equal(2, migrates[0].Payload[0]);

        // The other kinds are untouched and still drainable.
        Assert.Equal(1, link.Drain(target, CellMessageKind.GhostSync)[0].Payload[0]);
        Assert.Equal(3, link.Drain(target, CellMessageKind.MigrateAck)[0].Payload[0]);
    }
}
