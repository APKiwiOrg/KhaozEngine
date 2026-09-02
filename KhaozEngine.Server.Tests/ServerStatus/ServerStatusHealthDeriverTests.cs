using System;
using KhaozEngine.ServerStatus;
using Xunit;

namespace KhaozEngine.Tests.ServerStatus;

/// <summary>
/// Pins the server-side precedence: never-seen beats deploy window beats heartbeat freshness. The last test
/// is the reason this lives beside <see cref="ServerStatusEvaluator"/> rather than in a status endpoint: the
/// two halves have to agree about what Restarting and Down mean, and they drift when they are written twice.
/// </summary>
public class ServerStatusHealthDeriverTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private static readonly ServerStatusHealthOptions Options = new()
    {
        HeartbeatStaleAfter = TimeSpan.FromSeconds(60),
        DeployGrace = TimeSpan.FromSeconds(120),
    };

    private static ServerHeartbeat Beat(TimeSpan age) => new(Now - age, "1.2.3");

    [Fact]
    public void NoHeartbeatEverIsUnknown()
    {
        // Nothing to reason from, and an active deploy window does not change that: a server that has never
        // beaten has never been seen up, so claiming it is merely restarting would be inventing history.
        ServerDeployWindow window = new(LastDeployUtc: Now - TimeSpan.FromSeconds(10), ExpectedBackUtc: null);
        Assert.Equal(ServerHealth.Unknown, ServerStatusHealthDeriver.Derive(null, window, Now, Options));
        Assert.Equal(ServerHealth.Unknown, ServerStatusHealthDeriver.Derive(null, default, Now, Options));
    }

    [Fact]
    public void FreshHeartbeatIsHealthy()
    {
        Assert.Equal(
            ServerHealth.Healthy,
            ServerStatusHealthDeriver.Derive(Beat(TimeSpan.FromSeconds(5)), default, Now, Options));
    }

    [Fact]
    public void StaleHeartbeatIsDown()
    {
        Assert.Equal(
            ServerHealth.Down,
            ServerStatusHealthDeriver.Derive(Beat(TimeSpan.FromSeconds(90)), default, Now, Options));
    }

    [Fact]
    public void HeartbeatExactlyAtTheStalenessBoundIsStillHealthy()
    {
        Assert.Equal(
            ServerHealth.Healthy,
            ServerStatusHealthDeriver.Derive(Beat(TimeSpan.FromSeconds(60)), default, Now, Options));
    }

    [Fact]
    public void ExpectedBackInTheFutureBeatsAFreshHeartbeat()
    {
        // CI/CD declared the window on purpose, so it wins over an old process that is still beating.
        ServerDeployWindow window = new(LastDeployUtc: null, ExpectedBackUtc: Now + TimeSpan.FromMinutes(5));
        Assert.Equal(
            ServerHealth.Restarting,
            ServerStatusHealthDeriver.Derive(Beat(TimeSpan.FromSeconds(5)), window, Now, Options));
    }

    [Fact]
    public void ExpectedBackInThePastNoLongerExplainsAnything()
    {
        ServerDeployWindow window = new(LastDeployUtc: null, ExpectedBackUtc: Now - TimeSpan.FromMinutes(5));
        Assert.Equal(
            ServerHealth.Down,
            ServerStatusHealthDeriver.Derive(Beat(TimeSpan.FromSeconds(90)), window, Now, Options));
        Assert.Equal(
            ServerHealth.Healthy,
            ServerStatusHealthDeriver.Derive(Beat(TimeSpan.FromSeconds(5)), window, Now, Options));
    }

    [Theory]
    [InlineData(10, ServerHealth.Restarting)]
    [InlineData(120, ServerHealth.Restarting)]
    [InlineData(121, ServerHealth.Down)]
    public void DeployGraceCoversAStaleHeartbeatUntilItElapses(int deployedSecondsAgo, ServerHealth expected)
    {
        ServerDeployWindow window = new(
            LastDeployUtc: Now - TimeSpan.FromSeconds(deployedSecondsAgo),
            ExpectedBackUtc: null);
        Assert.Equal(
            expected,
            ServerStatusHealthDeriver.Derive(Beat(TimeSpan.FromSeconds(90)), window, Now, Options));
    }

    [Fact]
    public void DefaultOptionsAreSixtyAndOneTwenty()
    {
        ServerStatusHealthOptions defaults = new();
        Assert.Equal(TimeSpan.FromSeconds(60), defaults.HeartbeatStaleAfter);
        Assert.Equal(TimeSpan.FromSeconds(120), defaults.DeployGrace);

        // Omitting the argument uses those same defaults rather than a second set written into the deriver.
        Assert.Equal(
            ServerHealth.Down,
            ServerStatusHealthDeriver.Derive(Beat(TimeSpan.FromSeconds(90)), default, Now));
    }

    [Fact]
    public void DerivedHealthCarriesThroughTheClientEvaluatorUnchanged()
    {
        ServerDeployWindow window = new(LastDeployUtc: null, ExpectedBackUtc: Now + TimeSpan.FromMinutes(5));
        ServerHealth health = ServerStatusHealthDeriver.Derive(Beat(TimeSpan.FromSeconds(5)), window, Now, Options);

        ServerStatusReport report = new()
        {
            Health = health,
            ExpectedBackUtc = window.ExpectedBackUtc,
        };
        ServerStatusView view = ServerStatusEvaluator.Evaluate(
            new ServerStatusSnapshot { LastReport = report, LastSuccessUtc = Now },
            localClientVersion: "1.0.0",
            nowUtc: Now);

        Assert.Equal(ServerStatusState.ServerRestarting, view.State);
    }
}
