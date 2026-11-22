using System;
using System.Linq;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using Xunit;
using Phase = KhaozEngine.Navigation.GridPathPlanner.SearchPhase;

namespace KhaozEngine.Tests.Movement;

public sealed partial class RouteCalibrationWorkTests
{
    // Each row lists the nonzero operation counts, in work kind declaration order.
    static readonly string[] OperationCounts =
    [
        "neighbors Snap=1,MemberBox=1,Member=6,CertificateLabel=1,Seed=1,Pop=1024,SearchProgress=1025,Neighbor=8192",
        "links Snap=1,MemberBox=1,Member=2,CertificateLabel=1,Seed=1,Pop=2,SearchProgress=3,Neighbor=8,Link=2048," +
            "Parent=2,Reverse=1,Decode=2,Allocate=1,Copy=2",
        "certificate Snap=1,MemberBox=1,Member=288,CertificateLabel=256,CertificateProgress=126976",
        "membership Snap=1,MemberBox=1,Member=9216",
        "snap Snap=3969",
        "long-reconstruction Snap=1,MemberBox=1,Member=2,CertificateLabel=1,Seed=1,Pop=2049,SearchProgress=2050," +
            "Neighbor=16384,Parent=2049,Reverse=1024,Decode=2048,Allocate=1,Copy=2048",
        "columns Snap=1,MemberBox=1,Member=9,CertificateLabel=1,Seed=1,Pop=19,SearchProgress=20,Neighbor=144," +
            "Parent=19,Reverse=9,Decode=18,Allocate=1,Copy=18,EndpointStart=103,FootprintRow=3234,FootprintColumn=78624," +
            "EndpointLayer=103,SegmentStart=51,CellCrossing=510,ValidateFeet=18,Candidate=17,SideOffsets=17,KeepAnchor=1," +
            "StraighteningAllocate=1,StraighteningCopy=1",
        "rows-mixed Snap=1,MemberBox=1,Member=9,CertificateLabel=1,Seed=1,Pop=59,SearchProgress=60,Neighbor=464," +
            "Parent=59,Reverse=29,Decode=58,Allocate=1,Copy=58,EndpointStart=343,FootprintRow=5644,FootprintColumn=5644," +
            "EndpointLayer=343,SegmentStart=171,CellCrossing=5130,ValidateFeet=58,Candidate=57,SideOffsets=57," +
            "KeepAnchor=1,StraighteningAllocate=1,StraighteningCopy=1",
        "crossings-mixed Snap=1,MemberBox=1,Member=9,CertificateLabel=1,Seed=1,Pop=59,SearchProgress=60,Neighbor=464," +
            "Parent=59,Reverse=29,Decode=58,Allocate=1,Copy=58,EndpointStart=343,FootprintRow=514,FootprintColumn=5644," +
            "EndpointLayer=343,SegmentStart=171,CellCrossing=5130,ValidateFeet=58,Candidate=57,SideOffsets=57," +
            "KeepAnchor=1,StraighteningAllocate=1,StraighteningCopy=1",
        "endpoint-layers Layer=256,Snap=1,MemberBox=256,Member=2304,CertificateLabel=1,Seed=1,Pop=21," +
            "SearchProgress=22,Neighbor=160,Parent=21,Reverse=10,Decode=20,Allocate=1,Copy=20,EndpointStart=115," +
            "FootprintRow=172,FootprintColumn=799,EndpointLayer=29440,SegmentStart=57,CellCrossing=627,ValidateFeet=20," +
            "Candidate=19,SideOffsets=19,KeepAnchor=1,StraighteningAllocate=1,StraighteningCopy=1",
        "small Snap=1,MemberBox=1,Member=9,CertificateLabel=1,Seed=1,Pop=13,SearchProgress=14,Neighbor=96,Parent=13," +
            "Reverse=6,Decode=12,Allocate=1,Copy=12,EndpointStart=67,FootprintRow=166,FootprintColumn=551," +
            "EndpointLayer=67,SegmentStart=33,CellCrossing=231,ValidateFeet=12,Candidate=11,SideOffsets=11,KeepAnchor=1," +
            "StraighteningAllocate=1,StraighteningCopy=1",
        "median Snap=1,MemberBox=1,Member=9,CertificateLabel=1,Seed=1,Pop=83,SearchProgress=84,Neighbor=656," +
            "Parent=83,Reverse=41,Decode=82,Allocate=1,Copy=82,EndpointStart=487,FootprintRow=1216,FootprintColumn=18226," +
            "EndpointLayer=487,SegmentStart=243,CellCrossing=10206,ValidateFeet=82,Candidate=81,SideOffsets=81," +
            "KeepAnchor=1,StraighteningAllocate=1,StraighteningCopy=1",
        "maximum Snap=1,MemberBox=1,Member=9,CertificateLabel=1,Seed=1,Pop=154,SearchProgress=155,Neighbor=1224," +
            "Parent=154,Reverse=77,Decode=153,Allocate=1,Copy=153,EndpointStart=913,FootprintRow=2281," +
            "FootprintColumn=61181,EndpointLayer=913,SegmentStart=456,CellCrossing=35340,ValidateFeet=153,Candidate=152," +
            "SideOffsets=152,KeepAnchor=1,StraighteningAllocate=1,StraighteningCopy=1",
        "unreachable-region-4096 Snap=1,MemberBox=1,Member=2,CertificateLabel=1,Seed=1,Pop=4096,SearchProgress=4097," +
            "Neighbor=32768,Parent=4096,Reverse=2048,Decode=4095,Allocate=1,Copy=4095",
    ];
}

internal static partial class CalibrationWork
{
    internal static string DescribeOperations(PathWorkByKind work)
        => string.Join(",", Enum.GetValues<PathWorkKind>()
            .Where(kind => kind != PathWorkKind.Count && work[kind] != 0)
            .Select(kind => $"{kind}={Operations(work, kind)}"));

    internal static long Operations(PathWorkByKind work, PathWorkKind kind)
    {
        long count = Math.DivRem(work[kind], WeightOf(kind), out long remainder);
        Assert.Equal(0, remainder);
        return count;
    }

    internal static int WeightOf(PathWorkKind kind) => kind switch
    {
        >= PathWorkKind.Layer and <= PathWorkKind.Copy => PathWorkWeights.For((Phase)kind),
        >= PathWorkKind.EndpointStart and <= PathWorkKind.CellCrossing
            => NavSegmentRequest.WeightOf((NavSegmentOperation)(kind - PathWorkKind.EndpointStart)),
        PathWorkKind.ValidateFeet or PathWorkKind.KeepAnchor => PathWorkWeights.StraighteningFeet,
        PathWorkKind.Candidate => PathWorkWeights.StraighteningCandidate,
        PathWorkKind.SideOffsets => PathWorkWeights.StraighteningOffsets,
        PathWorkKind.StraighteningAllocate => PathWorkWeights.StraighteningAllocate,
        PathWorkKind.StraighteningCopy => PathWorkWeights.StraighteningCopy,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
