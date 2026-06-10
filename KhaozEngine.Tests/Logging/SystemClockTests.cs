using System;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Logging;

public class SystemClockTests
{
    [Fact]
    public void NowIsCloseToDateTimeOffsetNow()
    {
        var before = DateTimeOffset.Now;
        var now = SystemClock.Instance.Now;
        var after = DateTimeOffset.Now;
        Assert.True(now >= before.AddSeconds(-1) && now <= after.AddSeconds(1));
    }

    [Fact]
    public void InstanceIsSingleton()
    {
        Assert.Same(SystemClock.Instance, SystemClock.Instance);
    }
}
