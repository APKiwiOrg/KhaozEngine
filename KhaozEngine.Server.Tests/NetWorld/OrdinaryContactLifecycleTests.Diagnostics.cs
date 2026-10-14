using System.Numerics;
using KhaozEngine.Netcode;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public partial class OrdinaryContactLifecycleTests
{
    [Fact]
    public void ShardedSpawnRemainsSeatedAcrossTheFirstIdleTick()
    {
        using var heads = new OrdinaryNetworkHeads(true, pump: false);
        for (int i = 0; i < 6; i++) { heads.Sharded!.Poll(); heads.Client.Poll(); }
        Assert.Equal(0.75f, heads.State.Position.Y);
        var basis = heads.State;
        heads.Sharded!.Tick(0.1f);
        var actual = heads.State;
        using var source = heads.Cells[new(0, 0)].Simulator();
        using var read = source.BeginExplicitRead(basis);
        var expected = source.Step(basis, MoveCommand.Idle, 0.1f);
        OrdinaryContactHeadTests.AssertSame(OrdinaryContactHeadTests.CellPublication(expected.Move), actual.Move);
        Assert.Equal(0.75f, actual.Position.Y);
    }
}
