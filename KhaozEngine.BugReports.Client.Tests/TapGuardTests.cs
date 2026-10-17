using System;
using System.Collections.Generic;
using KhaozEngine.BugReports.Client.Internal;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Client;

public sealed class TapGuardTests
{
    [Fact]
    public void A_body_that_returns_runs_and_reports_true()
    {
        var warnings = new List<string>();
        int trips = 0;
        var guard = new TapGuard("clip tap", () => trips++, (message, _) => warnings.Add(message));

        int runs = 0;
        Assert.True(guard.Run(() => runs++));
        Assert.True(guard.Run(() => runs++));

        Assert.Equal(2, runs);
        Assert.False(guard.Tripped);
        Assert.Equal(0, trips);
        Assert.Empty(warnings);
    }

    [Fact]
    public void A_fault_is_reported_once_and_the_guard_stays_tripped()
    {
        var warnings = new List<(string Message, Exception Error)>();
        int trips = 0;
        var guard = new TapGuard("clip tap", () => trips++, (message, error) => warnings.Add((message, error)));
        var fault = new InvalidOperationException("device lost");

        Assert.False(guard.Run(() => throw fault));
        Assert.True(guard.Tripped);
        Assert.Single(warnings);
        Assert.Contains("clip tap", warnings[0].Message);
        Assert.Same(fault, warnings[0].Error);
        Assert.Equal(1, trips);

        bool ran = false;
        Assert.False(guard.Run(() => ran = true));
        Assert.False(ran);
        Assert.Single(warnings);
        Assert.Equal(1, trips);
    }

    [Fact]
    public void A_throwing_onTrip_does_not_escape_Run()
    {
        int trips = 0;
        var guard = new TapGuard("clip tap",
            () =>
            {
                trips++;
                throw new InvalidOperationException("trip broke");
            },
            (_, _) => throw new InvalidOperationException("warn broke"));

        Assert.False(guard.Run(() => throw new InvalidOperationException("device lost")));
        Assert.True(guard.Tripped);
        Assert.Equal(1, trips);

        bool ran = false;
        Assert.False(guard.Run(() => ran = true));
        Assert.False(ran);
        Assert.Equal(1, trips);
    }
}
