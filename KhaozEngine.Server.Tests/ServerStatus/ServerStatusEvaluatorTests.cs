using System;
using KhaozEngine.ServerStatus;
using Xunit;

namespace KhaozEngine.Tests.ServerStatus;

public class ServerStatusEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

    // A fresh snapshot (succeeded "just now") wrapping a report, so staleness never trips unless we age it.
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
    public void NoReportEver_IsStatusUnknown()
    {
        ServerStatusView view = ServerStatusEvaluator.Evaluate(ServerStatusSnapshot.Empty, "1.0.0", Now);
        Assert.Equal(ServerStatusState.StatusUnknown, view.State);
        Assert.Null(view.Report);
    }

    [Fact]
    public void ReportStalerThanWindow_IsStatusUnknown_ButRetainsMotdAndEta()
    {
        var report = new ServerStatusReport
        {
            Health = ServerHealth.Healthy,
            Motd = "hi",
            ExpectedBackUtc = Now.AddMinutes(5),
        };
        // Succeeded 2 minutes ago. Default MaxStaleness is 90 s, so this is too stale to trust.
        ServerStatusSnapshot snap = Fresh(report, Now.AddMinutes(-2));

        ServerStatusView view = ServerStatusEvaluator.Evaluate(snap, "1.0.0", Now);

        Assert.Equal(ServerStatusState.StatusUnknown, view.State);
        Assert.Equal("hi", view.Motd);                       // stale note still surfaced
        Assert.Equal(report.ExpectedBackUtc, view.ExpectedBackUtc);
    }

    [Fact]
    public void JustInsideStalenessWindow_IsTrusted()
    {
        ServerStatusSnapshot snap = Fresh(new ServerStatusReport { Health = ServerHealth.Healthy }, Now.AddSeconds(-89));
        ServerStatusView view = ServerStatusEvaluator.Evaluate(snap, "1.0.0", Now);
        Assert.Equal(ServerStatusState.ServerOk, view.State);
    }

    [Fact]
    public void FreshUnknownHealth_IsStatusUnknown()
    {
        ServerStatusView view = ServerStatusEvaluator.Evaluate(
            Fresh(new ServerStatusReport { Health = ServerHealth.Unknown }), "1.0.0", Now);
        Assert.Equal(ServerStatusState.StatusUnknown, view.State);
    }

    [Fact]
    public void Down_IsServerDown()
    {
        ServerStatusView view = ServerStatusEvaluator.Evaluate(
            Fresh(new ServerStatusReport { Health = ServerHealth.Down }), "1.0.0", Now);
        Assert.Equal(ServerStatusState.ServerDown, view.State);
    }

    [Fact]
    public void Restarting_IsServerRestarting_WithEta()
    {
        DateTimeOffset eta = Now.AddMinutes(4);
        ServerStatusView view = ServerStatusEvaluator.Evaluate(
            Fresh(new ServerStatusReport { Health = ServerHealth.Restarting, ExpectedBackUtc = eta }), "1.0.0", Now);

        Assert.Equal(ServerStatusState.ServerRestarting, view.State);
        Assert.Equal(eta, view.ExpectedBackUtc);
    }

    [Fact]
    public void HealthyWithNoVersionFloors_IsServerOk()
    {
        // min/latest empty = no gate, any client is fine.
        ServerStatusView view = ServerStatusEvaluator.Evaluate(
            Fresh(new ServerStatusReport { Health = ServerHealth.Healthy }), "0.0.1", Now);
        Assert.Equal(ServerStatusState.ServerOk, view.State);
    }

    [Fact]
    public void HealthyButClientBelowMin_IsUpdateRequired()
    {
        var report = new ServerStatusReport
        {
            Health = ServerHealth.Healthy,
            MinClientVersion = "1.4.0",
            LatestClientVersion = "1.4.2",
        };
        ServerStatusView view = ServerStatusEvaluator.Evaluate(Fresh(report), "1.3.9", Now);
        Assert.Equal(ServerStatusState.UpdateRequired, view.State);
    }

    [Fact]
    public void HealthyBelowLatestButAboveMin_IsUpdateAvailable()
    {
        var report = new ServerStatusReport
        {
            Health = ServerHealth.Healthy,
            MinClientVersion = "1.4.0",
            LatestClientVersion = "1.4.5",
        };
        ServerStatusView view = ServerStatusEvaluator.Evaluate(Fresh(report), "1.4.2", Now);
        Assert.Equal(ServerStatusState.UpdateAvailable, view.State);
    }

    [Fact]
    public void HealthyAtLatest_IsServerOk()
    {
        var report = new ServerStatusReport
        {
            Health = ServerHealth.Healthy,
            MinClientVersion = "1.4.0",
            LatestClientVersion = "1.4.5",
        };
        ServerStatusView view = ServerStatusEvaluator.Evaluate(Fresh(report), "1.4.5", Now);
        Assert.Equal(ServerStatusState.ServerOk, view.State);
    }

    [Fact]
    public void TransientHealthBeatsVersionGate_RestartingWinsOverBelowMin()
    {
        // Client is below min AND the server is mid-deploy. The "back soon" state is surfaced first.
        var report = new ServerStatusReport
        {
            Health = ServerHealth.Restarting,
            MinClientVersion = "9.9.9",
            ExpectedBackUtc = Now.AddMinutes(2),
        };
        ServerStatusView view = ServerStatusEvaluator.Evaluate(Fresh(report), "1.0.0", Now);
        Assert.Equal(ServerStatusState.ServerRestarting, view.State);
    }

    [Fact]
    public void VersionGate_UsesNumericSegments_Not_Lexicographic()
    {
        // 0.7.9 is below 0.7.10 numerically (a string compare would wrongly call "0.7.9" >= "0.7.10").
        var report = new ServerStatusReport
        {
            Health = ServerHealth.Healthy,
            MinClientVersion = "0.7.10",
        };
        Assert.Equal(ServerStatusState.UpdateRequired, ServerStatusEvaluator.Evaluate(Fresh(report), "0.7.9", Now).State);
        Assert.Equal(ServerStatusState.ServerOk, ServerStatusEvaluator.Evaluate(Fresh(report), "0.7.10", Now).State);
    }

    [Fact]
    public void CustomStalenessWindow_IsHonoured()
    {
        ServerStatusSnapshot snap = Fresh(new ServerStatusReport { Health = ServerHealth.Healthy }, Now.AddSeconds(-10));
        var opts = new ServerStatusEvaluationOptions { MaxStaleness = TimeSpan.FromSeconds(5) };
        ServerStatusView view = ServerStatusEvaluator.Evaluate(snap, "1.0.0", Now, opts);
        Assert.Equal(ServerStatusState.StatusUnknown, view.State);
    }
}
