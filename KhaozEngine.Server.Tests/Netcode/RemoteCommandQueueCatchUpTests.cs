using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

/// <summary>
/// Catch-up cap: when a deep backlog accrues (a reconnect flush, a delivery burst, an ungated/hostile client), a
/// one-command-per-Dequeue host would replay the whole backlog one tick at a time - the avatar driven by minutes-old
/// input while live input waits behind it. With catch-up enabled the queue skips the stale commands and jumps the
/// watermark to the newest, so the host is at most <c>catchUpThreshold</c> commands behind live. Lossy by design
/// (latest-wins, for a movement stream), so it is opt-in: threshold 0 is the exact one-per-Dequeue order.
/// </summary>
public class RemoteCommandQueueCatchUpTests
{
    [Fact]
    public void Disabled_ByDefault_DrainsOnePerDequeue()
    {
        var q = new RemoteCommandQueue<int>(neutralCommand: -999);   // no catchUpThreshold => off
        for (int seq = 0; seq < 50; seq++) q.Store(0, seq, seq);
        Assert.Equal(0, q.Dequeue(0, out int a0)); Assert.Equal(0, a0);   // strictly oldest-first, one at a time
        Assert.Equal(1, q.Dequeue(0, out int a1)); Assert.Equal(1, a1);
    }

    [Fact]
    public void OverThreshold_SkipsStale_AndJumpsToNewest()
    {
        var q = new RemoteCommandQueue<int>(neutralCommand: -999, maxQueuedPerSlot: 256, maxSlots: 8,
            catchUpThreshold: 8);
        for (int seq = 0; seq < 100; seq++) q.Store(0, seq, seq * 10);   // depth 100 >> threshold

        // One Dequeue collapses the backlog: returns the newest command and advances the watermark past everything.
        Assert.Equal(990, q.Dequeue(0, out int ack));   // seq 99 -> value 990
        Assert.Equal(99, ack);
        Assert.Equal(-999, q.Dequeue(0, out int ack2)); // nothing stale left to crawl through
        Assert.Equal(99, ack2);
    }

    [Fact]
    public void AtOrUnderThreshold_DrainsNormally()
    {
        var q = new RemoteCommandQueue<int>(neutralCommand: -999, catchUpThreshold: 8);
        for (int seq = 0; seq < 8; seq++) q.Store(0, seq, seq);   // depth 8, not OVER the threshold
        Assert.Equal(0, q.Dequeue(0, out _));    // still oldest-first
        Assert.Equal(1, q.Dequeue(0, out _));
    }

    [Fact]
    public void AfterCollapse_StaleSeqsAreRejected_LiveInputFlows()
    {
        var q = new RemoteCommandQueue<int>(neutralCommand: -999, catchUpThreshold: 4);
        for (int seq = 0; seq <= 50; seq++) q.Store(0, seq, seq);
        q.Dequeue(0, out int ack);
        Assert.Equal(50, ack);                   // jumped to live
        q.Store(0, 25, 2500);                    // a straggler below the new watermark -> rejected
        q.Store(0, 51, 5100);                    // genuinely newer -> accepted
        Assert.Equal(5100, q.Dequeue(0, out int ack2));
        Assert.Equal(51, ack2);
    }

    [Fact]
    public void NegativeThreshold_Throws()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => new RemoteCommandQueue<int>(neutralCommand: 0, catchUpThreshold: -1));
    }
}
