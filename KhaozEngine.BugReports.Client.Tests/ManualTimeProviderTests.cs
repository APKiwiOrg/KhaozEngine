using System;
using System.Threading;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Client;

/// <summary>The fake clock the flow facts wait on and advance.</summary>
public sealed class ManualTimeProviderTests
{
    [Fact]
    public void A_counted_timer_is_already_armed_from_its_creation_time()
    {
        var time = new ManualTimeProvider();
        int fired = 0;
        // A fact that sees the timer counted moves the clock at once, before the creating thread goes on.
        time.TimerVisible = () => time.Advance(TimeSpan.FromSeconds(1));

        using ITimer timer = time.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        time.TimerVisible = null;
        time.Advance(TimeSpan.FromSeconds(4));

        Assert.Equal(1, fired);
    }
}
