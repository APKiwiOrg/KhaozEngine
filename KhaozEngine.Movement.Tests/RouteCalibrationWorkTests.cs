using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Navigation;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Movement;

public sealed partial class RouteCalibrationWorkTests(ITestOutputHelper output)
{
    [Fact]
    public void CalibrationWorkloadsRetainTheirLiteralWorkMix()
    {
        // Each row lists only nonzero counters, in their declaration order. The share is in whole percent.
        (string Units, PathWorkKind Dominant, int Share)[] expected =
        [
            ("neighbors Snap=2,MemberBox=2,Member=24,CertificateLabel=1,Seed=4,Pop=4096,SearchProgress=1025," +
                "Neighbor=49152",
                PathWorkKind.Neighbor, 90),
            ("links Snap=2,MemberBox=2,Member=8,CertificateLabel=1,Seed=4,Pop=8,SearchProgress=3,Neighbor=48,Link=6144," +
                "Parent=2,Reverse=1,Decode=4,Allocate=2,Copy=2",
                PathWorkKind.Link, 98),
            ("certificate Snap=2,MemberBox=2,Member=1152,CertificateLabel=256,CertificateProgress=126976",
                PathWorkKind.CertificateProgress, 98),
            ("membership Snap=2,MemberBox=2,Member=36864",
                PathWorkKind.Member, 99),
            ("snap Snap=7938",
                PathWorkKind.Snap, 100),
            ("long-reconstruction Snap=2,MemberBox=2,Member=8,CertificateLabel=1,Seed=4,Pop=8196,SearchProgress=2050," +
                "Neighbor=98304,Parent=2049,Reverse=1024,Decode=4096,Allocate=2,Copy=2048",
                PathWorkKind.Neighbor, 83),
            ("columns Snap=2,MemberBox=2,Member=36,CertificateLabel=1,Seed=4,Pop=76,SearchProgress=20,Neighbor=864," +
                "Parent=19,Reverse=9,Decode=36,Allocate=2,Copy=18,EndpointStart=309,FootprintRow=9702," +
                "FootprintColumn=550368,EndpointLayer=515,SegmentStart=153,CellCrossing=3060,ValidateFeet=90,Candidate=119," +
                "SideOffsets=85,KeepAnchor=5,StraighteningAllocate=2,StraighteningCopy=1",
                PathWorkKind.FootprintColumn, 97),
            ("rows-mixed Snap=2,MemberBox=2,Member=36,CertificateLabel=1,Seed=4,Pop=236,SearchProgress=60,Neighbor=2784," +
                "Parent=59,Reverse=29,Decode=116,Allocate=2,Copy=58,EndpointStart=1029,FootprintRow=16932," +
                "FootprintColumn=39508,EndpointLayer=1715,SegmentStart=513,CellCrossing=30780,ValidateFeet=290," +
                "Candidate=399,SideOffsets=285,KeepAnchor=5,StraighteningAllocate=2,StraighteningCopy=1",
                PathWorkKind.FootprintColumn, 41),
            ("crossings-mixed Snap=2,MemberBox=2,Member=36,CertificateLabel=1,Seed=4,Pop=236,SearchProgress=60," +
                "Neighbor=2784,Parent=59,Reverse=29,Decode=116,Allocate=2,Copy=58,EndpointStart=1029,FootprintRow=1542," +
                "FootprintColumn=39508,EndpointLayer=1715,SegmentStart=513,CellCrossing=30780,ValidateFeet=290," +
                "Candidate=399,SideOffsets=285,KeepAnchor=5,StraighteningAllocate=2,StraighteningCopy=1",
                PathWorkKind.FootprintColumn, 49),
            ("endpoint-layers Layer=512,Snap=2,MemberBox=512,Member=9216,CertificateLabel=1,Seed=4,Pop=84," +
                "SearchProgress=22,Neighbor=960,Parent=21,Reverse=10,Decode=40,Allocate=2,Copy=20,EndpointStart=345," +
                "FootprintRow=516,FootprintColumn=5593,EndpointLayer=147200,SegmentStart=171,CellCrossing=3762," +
                "ValidateFeet=100,Candidate=133,SideOffsets=95,KeepAnchor=5,StraighteningAllocate=2,StraighteningCopy=1",
                PathWorkKind.EndpointLayer, 86),
            ("small Snap=2,MemberBox=2,Member=36,CertificateLabel=1,Seed=4,Pop=52,SearchProgress=14,Neighbor=576," +
                "Parent=13,Reverse=6,Decode=24,Allocate=2,Copy=12,EndpointStart=201,FootprintRow=498,FootprintColumn=3857," +
                "EndpointLayer=335,SegmentStart=99,CellCrossing=1386,ValidateFeet=60,Candidate=77,SideOffsets=55," +
                "KeepAnchor=5,StraighteningAllocate=2,StraighteningCopy=1",
                PathWorkKind.FootprintColumn, 52),
            ("median Snap=2,MemberBox=2,Member=36,CertificateLabel=1,Seed=4,Pop=332,SearchProgress=84,Neighbor=3936," +
                "Parent=83,Reverse=41,Decode=164,Allocate=2,Copy=82,EndpointStart=1461,FootprintRow=3648," +
                "FootprintColumn=127582,EndpointLayer=2435,SegmentStart=729,CellCrossing=61236,ValidateFeet=410," +
                "Candidate=567,SideOffsets=405,KeepAnchor=5,StraighteningAllocate=2,StraighteningCopy=1",
                PathWorkKind.FootprintColumn, 62),
            ("maximum Snap=2,MemberBox=2,Member=36,CertificateLabel=1,Seed=4,Pop=616,SearchProgress=155,Neighbor=7344," +
                "Parent=154,Reverse=77,Decode=306,Allocate=2,Copy=153,EndpointStart=2739,FootprintRow=6843," +
                "FootprintColumn=428267,EndpointLayer=4565,SegmentStart=1368,CellCrossing=212040,ValidateFeet=765," +
                "Candidate=1064,SideOffsets=760,KeepAnchor=5,StraighteningAllocate=2,StraighteningCopy=1",
                PathWorkKind.FootprintColumn, 64),
            ("unreachable-region-4096 Snap=2,MemberBox=2,Member=8,CertificateLabel=1,Seed=4,Pop=16384," +
                "SearchProgress=4097,Neighbor=196608,Parent=4096,Reverse=2048,Decode=8190,Allocate=2,Copy=4095",
                PathWorkKind.Neighbor, 83),
        ];

        var actual = new List<(string Units, PathWorkByKind Work)>();
        var fixtures = RouteCalibrationFixture.SearchRows().Concat(RouteCalibrationFixture.StraighteningRows())
            .Concat(RouteCalibrationFixture.WholeRoutes()).ToArray();
        Assert.Equal(expected.Length, fixtures.Length);
        Assert.Equal(OperationCounts.Length, fixtures.Length);
        for (int i = 0; i < fixtures.Length; i++)
        {
            RouteCalibrationFixture fixture = fixtures[i];
            using var pool = fixture.CreatePool();
            using var request = fixture.Begin(pool);
            var work = new PathWorkByKind();
            request.AdvanceForTests(new(int.MaxValue), work);
            Assert.Equal(PathPlanState.Ready, request.State);
            Assert.Equal(request.WorkSpent, work.Total);
            Assert.Equal(OperationCounts[i], $"{fixture.Name} {CalibrationWork.DescribeOperations(work)}");
            string units = $"{fixture.Name} {CalibrationWork.Describe(work)}";
            output.WriteLine(units);
            actual.Add((units, work));
        }

        Assert.Equal(expected.Select(row => row.Units), actual.Select(row => row.Units));
        for (int i = 0; i < expected.Length; i++)
        {
            var (_, dominant, share) = expected[i];
            var (units, work) = actual[i];
            Assert.Equal(work[dominant], Enum.GetValues<PathWorkKind>()
                .Where(kind => kind != PathWorkKind.Count).Max(kind => work[kind]));
            Assert.True(work[dominant] * 100 >= share * work.Total,
                $"{units}: {dominant} must account for at least {share}%");
        }
    }
}

internal static partial class CalibrationWork
{
    internal static string Describe(PathWorkByKind work)
        => string.Join(",", Enum.GetValues<PathWorkKind>()
            .Where(kind => kind != PathWorkKind.Count && work[kind] != 0)
            .Select(kind => $"{kind}={work[kind]}"));
}
