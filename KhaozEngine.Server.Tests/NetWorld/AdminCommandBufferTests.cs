using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class AdminCommandBufferTests
{
    [Fact]
    public void Drain_ReturnsEveryEnqueuedCommand_EvenFromManyThreads()
    {
        var buf = new AdminCommandBuffer();
        Parallel.For(0, 200, i =>
            buf.Enqueue(new AdminCommand { Kind = AdminCommandKind.Teleport, Position = new Vector3(i, 0, 0) }));

        var seen = new List<AdminCommand>();
        buf.Drain(seen.Add);

        Assert.Equal(200, seen.Count);
    }

    [Fact]
    public void Online_ReturnsLastPublishedSnapshot()
    {
        var buf = new AdminCommandBuffer();
        Assert.Empty(buf.Online);
        var snap = new[] { new OnlinePlayer(0, "a", "A", Vector3.Zero, true, 0f, 1) };
        buf.Publish(snap);
        Assert.Single(buf.Online);
        Assert.Equal("a", buf.Online[0].AccountId);
    }
}
