using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.ServerStatus;
using Xunit;

namespace KhaozEngine.Tests.ServerStatus;

public class ServerStatusReadoutTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

    // A fresh snapshot (succeeded "just now" unless overridden) wrapping a report.
    private static ServerStatusSnapshot Fresh(ServerStatusReport report, DateTimeOffset? at = null)
    {
        DateTimeOffset t = at ?? Now;
        return new ServerStatusSnapshot
        {
            LastReport = report,
            LastSuccessUtc = t,
            LastAttemptUtc = t,
            ConsecutiveFailures = 0,
        };
    }

    [Fact]
    public void HealthySnapshot_ProducesFullRowSet_WithExpectedValuesAndRaw()
    {
        var report = new ServerStatusReport
        {
            Health = ServerHealth.Healthy,
            ServerVersion = "1.4.2",
            MinClientVersion = "1.4.0",
            LatestClientVersion = "1.4.5",
            LastHeartbeatUtc = Now.AddSeconds(-12),
            LastDeployUtc = Now.AddHours(-2),
            Motd = "Double XP weekend.",
        };
        ServerStatusSnapshot snapshot = Fresh(report);
        ServerStatusView view = ServerStatusEvaluator.Evaluate(snapshot, "1.4.5", Now);

        IReadOnlyList<ServerStatusReadoutRow> rows = ServerStatusReadout.Build(snapshot, view, "1.4.5", Now);

        Assert.Equal(11, rows.Count);
        Assert.Equal(ServerStatusReadoutKeys.All, rows.Select(r => r.Key).ToArray());

        Assert.Equal(new ServerStatusReadoutRow(ServerStatusReadoutKeys.Health, "Healthy", ServerHealth.Healthy), rows[0]);
        Assert.Equal(new ServerStatusReadoutRow(ServerStatusReadoutKeys.ServerVersion, "1.4.2", "1.4.2"), rows[1]);
        Assert.Equal(new ServerStatusReadoutRow(ServerStatusReadoutKeys.MinClientVersion, "1.4.0", "1.4.0"), rows[2]);
        Assert.Equal(new ServerStatusReadoutRow(ServerStatusReadoutKeys.LatestClientVersion, "1.4.5", "1.4.5"), rows[3]);
        Assert.Equal(new ServerStatusReadoutRow(ServerStatusReadoutKeys.ClientVersion, "1.4.5", "1.4.5"), rows[4]);
        Assert.Equal(new ServerStatusReadoutRow(ServerStatusReadoutKeys.LastHeartbeat, "12 s ago", report.LastHeartbeatUtc), rows[5]);
        Assert.Equal(new ServerStatusReadoutRow(ServerStatusReadoutKeys.LastDeploy, "2 h ago", report.LastDeployUtc), rows[6]);
        Assert.Equal(new ServerStatusReadoutRow(ServerStatusReadoutKeys.ExpectedBack, "", null), rows[7]);
        Assert.Equal(new ServerStatusReadoutRow(ServerStatusReadoutKeys.Staleness, "0 s ago", TimeSpan.Zero), rows[8]);
        Assert.Equal(new ServerStatusReadoutRow(ServerStatusReadoutKeys.State, "ServerOk", ServerStatusState.ServerOk), rows[9]);
        Assert.Equal(new ServerStatusReadoutRow(ServerStatusReadoutKeys.Motd, "Double XP weekend.", "Double XP weekend."), rows[10]);
    }

    [Fact]
    public void NeverPolled_ReportRowsEmpty_ClientVersionAndStateStillPresent()
    {
        ServerStatusView view = ServerStatusEvaluator.Evaluate(ServerStatusSnapshot.Empty, "2.0.0", Now);

        IReadOnlyList<ServerStatusReadoutRow> rows = ServerStatusReadout.Build(ServerStatusSnapshot.Empty, view, "2.0.0", Now);

        Assert.Equal(11, rows.Count);
        Assert.Equal(ServerStatusReadoutKeys.All, rows.Select(r => r.Key).ToArray());

        string[] reportDerivedKeys =
        {
            ServerStatusReadoutKeys.Health, ServerStatusReadoutKeys.ServerVersion,
            ServerStatusReadoutKeys.MinClientVersion, ServerStatusReadoutKeys.LatestClientVersion,
            ServerStatusReadoutKeys.LastHeartbeat, ServerStatusReadoutKeys.LastDeploy,
            ServerStatusReadoutKeys.ExpectedBack, ServerStatusReadoutKeys.Staleness, ServerStatusReadoutKeys.Motd,
        };
        foreach (string key in reportDerivedKeys)
        {
            ServerStatusReadoutRow row = rows.Single(r => r.Key == key);
            Assert.Equal("", row.Value);
            Assert.Null(row.Raw);
        }

        Assert.Equal("2.0.0", rows.Single(r => r.Key == ServerStatusReadoutKeys.ClientVersion).Value);

        ServerStatusReadoutRow stateRow = rows.Single(r => r.Key == ServerStatusReadoutKeys.State);
        Assert.Equal("StatusUnknown", stateRow.Value);
        Assert.Equal(ServerStatusState.StatusUnknown, stateRow.Raw);
    }

    [Fact]
    public void StaleReport_RetainsReportDetails_ButStateDegradesToUnknown()
    {
        var report = new ServerStatusReport
        {
            Health = ServerHealth.Healthy,
            ServerVersion = "1.0.0",
            Motd = "hi",
            ExpectedBackUtc = Now.AddMinutes(5),
        };
        // Succeeded 2 minutes ago. Default MaxStaleness is 90 s, so the poll is too old to trust.
        ServerStatusSnapshot snapshot = Fresh(report, Now.AddMinutes(-2));
        ServerStatusView view = ServerStatusEvaluator.Evaluate(snapshot, "1.0.0", Now);

        IReadOnlyList<ServerStatusReadoutRow> rows = ServerStatusReadout.Build(snapshot, view, "1.0.0", Now);

        // The overall trust verdict is unknown...
        Assert.Equal("StatusUnknown", rows.Single(r => r.Key == ServerStatusReadoutKeys.State).Value);
        // ...but the retained report's own facts still show, so a stale "back soon" note keeps rendering.
        Assert.Equal("Healthy", rows.Single(r => r.Key == ServerStatusReadoutKeys.Health).Value);
        Assert.Equal("1.0.0", rows.Single(r => r.Key == ServerStatusReadoutKeys.ServerVersion).Value);
        Assert.Equal("hi", rows.Single(r => r.Key == ServerStatusReadoutKeys.Motd).Value);
        Assert.Equal("in 5 min", rows.Single(r => r.Key == ServerStatusReadoutKeys.ExpectedBack).Value);
        Assert.Equal("2 min ago", rows.Single(r => r.Key == ServerStatusReadoutKeys.Staleness).Value);
    }

    [Fact]
    public void UnsetOptionalFields_EmitEmptyValues_RowCountStable()
    {
        var report = new ServerStatusReport
        {
            Health = ServerHealth.Healthy,
            ServerVersion = "1.0.0",
            // MinClientVersion / LatestClientVersion / Motd / ExpectedBackUtc all left at their defaults.
        };
        ServerStatusSnapshot snapshot = Fresh(report);
        ServerStatusView view = ServerStatusEvaluator.Evaluate(snapshot, "1.0.0", Now);

        IReadOnlyList<ServerStatusReadoutRow> rows = ServerStatusReadout.Build(snapshot, view, "1.0.0", Now);

        Assert.Equal(11, rows.Count);
        Assert.Equal("", rows.Single(r => r.Key == ServerStatusReadoutKeys.MinClientVersion).Value);
        Assert.Equal("", rows.Single(r => r.Key == ServerStatusReadoutKeys.LatestClientVersion).Value);
        Assert.Equal("", rows.Single(r => r.Key == ServerStatusReadoutKeys.Motd).Value);
        Assert.Null(rows.Single(r => r.Key == ServerStatusReadoutKeys.Motd).Raw);
        Assert.Equal("", rows.Single(r => r.Key == ServerStatusReadoutKeys.ExpectedBack).Value);
        Assert.Null(rows.Single(r => r.Key == ServerStatusReadoutKeys.ExpectedBack).Raw);
    }

    [Theory]
    [InlineData(0, "0 s ago")]
    [InlineData(1, "1 s ago")]
    [InlineData(59, "59 s ago")]
    [InlineData(60, "1 min ago")]
    [InlineData(3599, "59 min ago")]
    [InlineData(3600, "1 h ago")]
    [InlineData(86399, "23 h ago")]
    [InlineData(86400, "1 d ago")]
    [InlineData(172800, "2 d ago")]
    public void LastHeartbeat_FormatsAgoBoundaries(int elapsedSeconds, string expected)
    {
        var report = new ServerStatusReport { Health = ServerHealth.Healthy, LastHeartbeatUtc = Now.AddSeconds(-elapsedSeconds) };
        ServerStatusSnapshot snapshot = Fresh(report);
        ServerStatusView view = ServerStatusEvaluator.Evaluate(snapshot, "1.0.0", Now);

        IReadOnlyList<ServerStatusReadoutRow> rows = ServerStatusReadout.Build(snapshot, view, "1.0.0", Now);

        Assert.Equal(expected, rows.Single(r => r.Key == ServerStatusReadoutKeys.LastHeartbeat).Value);
    }

    [Fact]
    public void LastHeartbeat_InTheFuture_ClampsToZero()
    {
        // Clock skew: a heartbeat that appears to be in the future must not format as a negative duration.
        var report = new ServerStatusReport { Health = ServerHealth.Healthy, LastHeartbeatUtc = Now.AddSeconds(10) };
        ServerStatusSnapshot snapshot = Fresh(report);
        ServerStatusView view = ServerStatusEvaluator.Evaluate(snapshot, "1.0.0", Now);

        IReadOnlyList<ServerStatusReadoutRow> rows = ServerStatusReadout.Build(snapshot, view, "1.0.0", Now);

        Assert.Equal("0 s ago", rows.Single(r => r.Key == ServerStatusReadoutKeys.LastHeartbeat).Value);
    }

    [Theory]
    [InlineData(0, "in 0 s")]
    [InlineData(59, "in 59 s")]
    [InlineData(60, "in 1 min")]
    [InlineData(3600, "in 1 h")]
    [InlineData(86400, "in 1 d")]
    public void ExpectedBack_FormatsInBoundaries(int remainingSeconds, string expected)
    {
        var report = new ServerStatusReport { Health = ServerHealth.Restarting, ExpectedBackUtc = Now.AddSeconds(remainingSeconds) };
        ServerStatusSnapshot snapshot = Fresh(report);
        ServerStatusView view = ServerStatusEvaluator.Evaluate(snapshot, "1.0.0", Now);

        IReadOnlyList<ServerStatusReadoutRow> rows = ServerStatusReadout.Build(snapshot, view, "1.0.0", Now);

        Assert.Equal(expected, rows.Single(r => r.Key == ServerStatusReadoutKeys.ExpectedBack).Value);
    }

    [Fact]
    public void ExpectedBack_AlreadyDue_ClampsToZero()
    {
        var report = new ServerStatusReport { Health = ServerHealth.Restarting, ExpectedBackUtc = Now.AddSeconds(-30) };
        ServerStatusSnapshot snapshot = Fresh(report);
        ServerStatusView view = ServerStatusEvaluator.Evaluate(snapshot, "1.0.0", Now);

        IReadOnlyList<ServerStatusReadoutRow> rows = ServerStatusReadout.Build(snapshot, view, "1.0.0", Now);

        Assert.Equal("in 0 s", rows.Single(r => r.Key == ServerStatusReadoutKeys.ExpectedBack).Value);
    }

    [Fact]
    public void KeyConstants_MatchEmittedKeys_InDeclaredOrder_NoDuplicates()
    {
        var report = new ServerStatusReport { Health = ServerHealth.Healthy };
        ServerStatusSnapshot snapshot = Fresh(report);
        ServerStatusView view = ServerStatusEvaluator.Evaluate(snapshot, "1.0.0", Now);

        IReadOnlyList<ServerStatusReadoutRow> rows = ServerStatusReadout.Build(snapshot, view, "1.0.0", Now);

        Assert.Equal(ServerStatusReadoutKeys.All.Count, rows.Count);
        Assert.Equal(ServerStatusReadoutKeys.All, rows.Select(r => r.Key).ToArray());
        Assert.Equal(rows.Count, rows.Select(r => r.Key).Distinct().Count());
    }

    [Fact]
    public void Build_IsDeterministic_ForFixedInputs()
    {
        var report = new ServerStatusReport
        {
            Health = ServerHealth.Healthy,
            ServerVersion = "1.2.3",
            MinClientVersion = "1.2.0",
            LatestClientVersion = "1.2.5",
            LastHeartbeatUtc = Now.AddSeconds(-5),
            LastDeployUtc = Now.AddHours(-1),
            Motd = "steady state",
        };
        ServerStatusSnapshot snapshot = Fresh(report);
        ServerStatusView view = ServerStatusEvaluator.Evaluate(snapshot, "1.2.4", Now);

        IReadOnlyList<ServerStatusReadoutRow> rows1 = ServerStatusReadout.Build(snapshot, view, "1.2.4", Now);
        IReadOnlyList<ServerStatusReadoutRow> rows2 = ServerStatusReadout.Build(snapshot, view, "1.2.4", Now);

        Assert.Equal(rows1, rows2);
    }
}
