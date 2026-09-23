using System;
using System.Threading;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>Pins that this host raised its <see cref="ThreadPoolFloor"/> at module load, so a dropped hook fails
/// here rather than as a starved loopback or MCP test on a small runner.</summary>
public class ThreadPoolFloorHookTests
{
    [Fact]
    public void The_host_runs_with_the_raised_pool_floor()
    {
        ThreadPool.GetMinThreads(out int workers, out int completionPorts);
        int floor = Math.Max(Environment.ProcessorCount * 4, 32);
        Assert.True(workers >= floor, $"min workers {workers}, expected at least {floor}");
        Assert.True(completionPorts >= floor, $"min completion ports {completionPorts}, expected at least {floor}");
    }
}
