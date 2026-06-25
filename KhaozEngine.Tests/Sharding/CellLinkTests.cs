using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Sharding;

public class CellLinkTests
{
    private static CellMessage Msg(CellCoord source, CellCoord target, byte payload) =>
        new(source, target, CellMessageKind.GhostSync, new[] { payload });

    [Fact]
    public void Drain_ReturnsMessagesForTarget_InFifoOrder()
    {
        ICellLink link = new InProcessCellLink();
        var target = new CellCoord(1, 0);
        link.Send(Msg(new CellCoord(0, 0), target, 1));
        link.Send(Msg(new CellCoord(2, 0), target, 2));

        var drained = link.Drain(target);

        Assert.Equal(2, drained.Count);
        Assert.Equal(1, drained[0].Payload[0]);
        Assert.Equal(2, drained[1].Payload[0]);
        Assert.Equal(new CellCoord(0, 0), drained[0].Source);
    }

    [Fact]
    public void Drain_ClearsTheTargetsQueue()
    {
        ICellLink link = new InProcessCellLink();
        var target = new CellCoord(1, 0);
        link.Send(Msg(new CellCoord(0, 0), target, 1));

        Assert.Single(link.Drain(target));
        Assert.Empty(link.Drain(target));   // drained once, now empty
    }

    [Fact]
    public void Drain_UnknownTarget_ReturnsEmpty()
    {
        ICellLink link = new InProcessCellLink();
        Assert.Empty(link.Drain(new CellCoord(5, 5)));
    }

    [Fact]
    public void Messages_AreIsolatedPerTarget()
    {
        ICellLink link = new InProcessCellLink();
        var a = new CellCoord(0, 0);
        var b = new CellCoord(1, 0);
        link.Send(Msg(new CellCoord(9, 9), a, 10));
        link.Send(Msg(new CellCoord(9, 9), b, 20));

        Assert.Equal(10, link.Drain(a)[0].Payload[0]);
        Assert.Equal(20, link.Drain(b)[0].Payload[0]);
    }
}
