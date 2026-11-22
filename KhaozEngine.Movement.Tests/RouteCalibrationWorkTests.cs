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
            ("neighbors Snap=2,MemberBox=2,Member=12,CertificateLabel=1,Seed=4,Pop=4096,SearchProgress=1025," +
                "Neighbor=49152",
                PathWorkKind.Neighbor, 90),
            ("links Snap=2,MemberBox=2,Member=4,CertificateLabel=1,Seed=4,Pop=8,SearchProgress=3,Neighbor=48,Link=12288," +
                "Parent=2,Reverse=1,Decode=4,Allocate=2,Copy=2",
                PathWorkKind.Link, 99),
            ("certificate Snap=2,MemberBox=2,Member=576,CertificateLabel=256,CertificateProgress=126976",
                PathWorkKind.CertificateProgress, 99),
            ("membership Snap=2,MemberBox=2,Member=18432",
                PathWorkKind.Member, 99),
            ("snap Snap=7938",
                PathWorkKind.Snap, 100),
            ("long-reconstruction Snap=2,MemberBox=2,Member=4,CertificateLabel=1,Seed=4,Pop=8196,SearchProgress=2050," +
                "Neighbor=98304,Parent=2049,Reverse=1024,Decode=4096,Allocate=2,Copy=2048",
                PathWorkKind.Neighbor, 83),
            ("columns Snap=2,MemberBox=2,Member=18,CertificateLabel=1,Seed=4,Pop=76,SearchProgress=20,Neighbor=864," +
                "Parent=19,Reverse=9,Decode=36,Allocate=2,Copy=18,EndpointStart=103,FootprintRow=6468," +
                "FootprintColumn=235872,EndpointLayer=206,SegmentStart=51,CellCrossing=1530,ValidateFeet=36,Candidate=51," +
                "SideOffsets=34,KeepAnchor=2,StraighteningAllocate=2,StraighteningCopy=1",
                PathWorkKind.FootprintColumn, 96),
            ("rows-mixed Snap=2,MemberBox=2,Member=18,CertificateLabel=1,Seed=4,Pop=236,SearchProgress=60,Neighbor=2784," +
                "Parent=59,Reverse=29,Decode=116,Allocate=2,Copy=58,EndpointStart=343,FootprintRow=11288," +
                "FootprintColumn=16932,EndpointLayer=686,SegmentStart=171,CellCrossing=15390,ValidateFeet=116," +
                "Candidate=171,SideOffsets=114,KeepAnchor=2,StraighteningAllocate=2,StraighteningCopy=1",
                PathWorkKind.FootprintColumn, 34),
            ("crossings-mixed Snap=2,MemberBox=2,Member=18,CertificateLabel=1,Seed=4,Pop=236,SearchProgress=60," +
                "Neighbor=2784,Parent=59,Reverse=29,Decode=116,Allocate=2,Copy=58,EndpointStart=343,FootprintRow=1028," +
                "FootprintColumn=16932,EndpointLayer=686,SegmentStart=171,CellCrossing=15390,ValidateFeet=116," +
                "Candidate=171,SideOffsets=114,KeepAnchor=2,StraighteningAllocate=2,StraighteningCopy=1",
                PathWorkKind.FootprintColumn, 44),
            ("endpoint-layers Layer=512,Snap=2,MemberBox=512,Member=4608,CertificateLabel=1,Seed=4,Pop=84," +
                "SearchProgress=22,Neighbor=960,Parent=21,Reverse=10,Decode=40,Allocate=2,Copy=20,EndpointStart=115," +
                "FootprintRow=344,FootprintColumn=2397,EndpointLayer=58880,SegmentStart=57,CellCrossing=1881," +
                "ValidateFeet=40,Candidate=57,SideOffsets=38,KeepAnchor=2,StraighteningAllocate=2,StraighteningCopy=1",
                PathWorkKind.EndpointLayer, 83),
            ("small Snap=2,MemberBox=2,Member=18,CertificateLabel=1,Seed=4,Pop=52,SearchProgress=14,Neighbor=576," +
                "Parent=13,Reverse=6,Decode=24,Allocate=2,Copy=12,EndpointStart=67,FootprintRow=332,FootprintColumn=1653," +
                "EndpointLayer=134,SegmentStart=33,CellCrossing=693,ValidateFeet=24,Candidate=33,SideOffsets=22," +
                "KeepAnchor=2,StraighteningAllocate=2,StraighteningCopy=1",
                PathWorkKind.FootprintColumn, 44),
            ("median Snap=2,MemberBox=2,Member=18,CertificateLabel=1,Seed=4,Pop=332,SearchProgress=84,Neighbor=3936," +
                "Parent=83,Reverse=41,Decode=164,Allocate=2,Copy=82,EndpointStart=487,FootprintRow=2432," +
                "FootprintColumn=54678,EndpointLayer=974,SegmentStart=243,CellCrossing=30618,ValidateFeet=164," +
                "Candidate=243,SideOffsets=162,KeepAnchor=2,StraighteningAllocate=2,StraighteningCopy=1",
                PathWorkKind.FootprintColumn, 57),
            ("maximum Snap=2,MemberBox=2,Member=18,CertificateLabel=1,Seed=4,Pop=616,SearchProgress=155,Neighbor=7344," +
                "Parent=154,Reverse=77,Decode=306,Allocate=2,Copy=153,EndpointStart=913,FootprintRow=4562," +
                "FootprintColumn=183543,EndpointLayer=1826,SegmentStart=456,CellCrossing=106020,ValidateFeet=306," +
                "Candidate=456,SideOffsets=304,KeepAnchor=2,StraighteningAllocate=2,StraighteningCopy=1",
                PathWorkKind.FootprintColumn, 59),
            ("unreachable-region-4096 Snap=2,MemberBox=2,Member=4,CertificateLabel=1,Seed=4,Pop=16384," +
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
