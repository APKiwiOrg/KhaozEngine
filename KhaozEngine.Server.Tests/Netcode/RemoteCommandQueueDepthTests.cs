using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

// Depth is the host's per-slot input backlog: the buffered, not-yet-dequeued count. A steady one-per-tick sender
// sits at 0 or 1, and a value that climbs and stays there is input the host applies that many ticks late.
public class RemoteCommandQueueDepthTests
{
    [Fact]
    public void Depth_counts_buffered_commands_and_is_zero_for_an_unknown_slot()
    {
        var q = new RemoteCommandQueue<int>(neutralCommand: -1);
        Assert.Equal(0, q.Depth(7));
        q.Store(0, 0, 10);
        q.Store(0, 1, 20);
        Assert.Equal(2, q.Depth(0));
        q.Dequeue(0, out _);
        Assert.Equal(1, q.Depth(0));
        q.Dequeue(0, out _);
        Assert.Equal(0, q.Depth(0));
    }
}
