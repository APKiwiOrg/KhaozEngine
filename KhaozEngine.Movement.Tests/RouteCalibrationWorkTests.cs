using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Navigation;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Movement;

public sealed class RouteCalibrationWorkTests(ITestOutputHelper output)
{
    [Fact]
    public void CalibrationWorkloadsRetainTheirLiteralWorkMix()
    {
        // Each row lists only nonzero counters, in their declaration order. The share is in whole percent.
        (string Units, PathWorkKind Dominant, int Share)[] expected =
        [
            ("neighbors", PathWorkKind.Neighbor, 89),
            ("links", PathWorkKind.Link, 98),
            ("certificate", PathWorkKind.CertificateProgress, 98),
            ("membership", PathWorkKind.Member, 99),
            ("snap", PathWorkKind.Snap, 100),
            ("long-reconstruction", PathWorkKind.Neighbor, 80),
            ("columns", PathWorkKind.FootprintColumn, 90),
            ("rows-mixed", PathWorkKind.FootprintColumn, 30),
            ("crossings-mixed", PathWorkKind.FootprintColumn, 30),
            ("endpoint-layers", PathWorkKind.EndpointLayer, 75),
            ("small", PathWorkKind.FootprintColumn, 30),
            ("median", PathWorkKind.FootprintColumn, 40),
            ("maximum", PathWorkKind.FootprintColumn, 40),
            ("unreachable-region-4096", PathWorkKind.Neighbor, 80),
        ];

        var actual = new List<(string Units, PathWorkByKind Work)>();
        var fixtures = RouteCalibrationFixture.SearchRows().Concat(RouteCalibrationFixture.StraighteningRows())
            .Concat(RouteCalibrationFixture.WholeRoutes()).ToArray();
        Assert.Equal(expected.Length, fixtures.Length);
        for (int i = 0; i < fixtures.Length; i++)
        {
            RouteCalibrationFixture fixture = fixtures[i];
            using var pool = fixture.CreatePool();
            using var request = fixture.Begin(pool);
            var work = new PathWorkByKind();
            request.AdvanceForTests(new(int.MaxValue), work);
            Assert.Equal(PathPlanState.Ready, request.State);
            Assert.Equal(request.WorkSpent, work.Total);
            string units = $"{fixture.Name} {CalibrationWork.Describe(work)}";
            output.WriteLine(units);
            actual.Add((units, work));
        }

        Assert.Equal(expected.Select(row => row.Units), actual.Select(row => row.Units));
        for (int i = 0; i < expected.Length; i++)
        {
            var (_, dominant, share) = expected[i];
            var (units, work) = actual[i];
            Assert.True(work[dominant] * 100 >= share * work.Total,
                $"{units}: {dominant} must account for at least {share}%");
        }
    }
}

internal static class CalibrationWork
{
    internal static string Describe(PathWorkByKind work)
        => string.Join(",", Enum.GetValues<PathWorkKind>()
            .Where(kind => kind != PathWorkKind.Count && work[kind] != 0)
            .Select(kind => $"{kind}={work[kind]}"));
}
