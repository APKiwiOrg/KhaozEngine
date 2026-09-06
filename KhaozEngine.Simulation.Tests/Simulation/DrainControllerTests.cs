using KhaozEngine.Simulation.Hosting;
using Xunit;

namespace KhaozEngine.Tests.Simulation;

public class DrainControllerTests
{
    [Fact]
    public void Countdown_completes_once_and_stays_terminal()
    {
        var drain = new DrainController();

        drain.Begin(1f);
        Assert.True(drain.HasBegun);
        Assert.True(drain.IsDraining);
        Assert.False(drain.IsComplete);

        drain.Advance(0.4f);
        Assert.True(drain.IsDraining);
        drain.Advance(0.6f);
        Assert.False(drain.IsDraining);
        Assert.True(drain.IsComplete);

        drain.Advance(10f);
        Assert.True(drain.IsComplete);
    }

    [Fact]
    public void Begin_restarts_an_active_or_completed_countdown()
    {
        var drain = new DrainController();
        drain.Begin(1f);
        drain.Advance(0.75f);

        drain.Begin(2f);
        drain.Advance(1.5f);
        Assert.True(drain.IsDraining);
        Assert.False(drain.IsComplete);
        drain.Advance(0.5f);
        Assert.True(drain.IsComplete);

        drain.Begin(0f);
        Assert.True(drain.IsDraining);
        Assert.False(drain.IsComplete);
        drain.Advance(0f);
        Assert.True(drain.IsComplete);
    }
}
