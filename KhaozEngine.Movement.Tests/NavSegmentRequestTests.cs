using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Tests.Movement.NavAreaFootprintRowsTests;

namespace KhaozEngine.Tests.Movement;

public sealed class NavSegmentRequestTests
{
    [Theory]
    [InlineData(2.5f, 5.5f, 13.5f, 5.5f, 0.5f)]
    [InlineData(5.5f, 2.5f, 5.5f, 13.5f, 0.5f)]
    [InlineData(2.5f, 2.5f, 13.5f, 13.5f, 0f)]
    [InlineData(13.5f, 13.5f, 2.5f, 2.5f, 0f)]
    [InlineData(13.5f, 2.5f, 2.5f, 11.5f, 1.75f)]
    [InlineData(6.5f, 6.5f, 6.5f, 6.5f, 0.75f)]
    [InlineData(0.5f, 0.5f, 14.5f, 13.5f, 0.5f)]
    [InlineData(2.5f, 5.4999995f, 13.5f, 5.4999995f, 0.5f)]
    [InlineData(2.5f, 5.5000005f, 13.5f, 5.5000005f, 0.5f)]
    [InlineData(2.5f, 5.4999995f, 13.5f, 5.5000005f, 1.25f)]
    [InlineData(2.5f, 2.5f, 13.5f, 11.5f, 0.3f, 1048576f, 0.25f)]
    [InlineData(2.5f, 2.5f, 13.5f, 11.5f, 0.3f, 0f, 5.684341886080802e-14f)]
    [InlineData(2.5f, 2.5f, 15.499999f, 11.5f, 0.5f)]
    [InlineData(2.5f, 2.5f, 15.5f, 11.5f, 0.5f)]
    [InlineData(2.5f, 2.5f, 15.500001f, 11.5f, 0.5f)]
    public void MatchesAllowsSegment(float ax, float az, float bx, float bz,
        float radiusCells, float origin = 0f, float cellSize = 1f)
    {
        var options = Options(origin, -origin, cellSize);
        Vector3 from = new(origin + ax * cellSize, 0f, -origin + az * cellSize);
        Vector3 to = new(origin + bx * cellSize, 0f, -origin + bz * cellSize);
        var tuning = MoveTuning.Default with { CapsuleRadius = radiusCells * cellSize };
        Compare(Columns(options));
        for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
                Compare(Columns(options, (x, z)));

        void Compare(PhysicsNavColumns columns)
        {
            bool frozen = Rectangle(columns, tuning, from, to, UpperArea);
            var navigation = SegmentAdmissionFixture.Build(Footprint(columns, tuning).ExcludingAreas(UpperArea), tuning);
            AssertSchedules(navigation, from, to, frozen);
        }
    }

    [Theory]
    [InlineData(0f, 0f, true)]
    [InlineData(4f, 4f, false)]
    [InlineData(0f, 4f, false)]
    [InlineData(-2f, -2f, false)]
    public void StackedSurfacesUseEndpointHeights(float low, float high, bool expected)
    {
        var tuning = MoveTuning.Default with { CapsuleRadius = 0.3f };
        var columns = Columns(Options());
        var navigation = SegmentAdmissionFixture.Build(Footprint(columns, tuning).ExcludingAreas(UpperArea), tuning, 4f);
        Vector3 from = new(2.5f, low, 2.5f), to = new(13.5f, high, 11.5f);
        Assert.Equal(expected, Rectangle(columns, tuning, from, to, UpperArea));
        AssertSchedules(navigation, from, to, expected);
    }

    [Theory]
    [InlineData(NavLinkKind.Stair, true)]
    [InlineData(NavLinkKind.Hop, false)]
    [InlineData(NavLinkKind.Drop, false)]
    public void ADirectedStairIsTheOnlyCrossLayerSegment(NavLinkKind kind, bool expected)
    {
        var tuning = MoveTuning.Default with { CapsuleRadius = 0f };
        var columns = Columns(Options());
        var link = new NavLink(0, 2, 2, 1, 3, 2) { Kind = kind };
        var footprint = new NavAreaFootprint(columns, columns.Options, tuning, default, surfaceReach: 2d);
        GroundNavigation navigation = SegmentAdmissionFixture.Build(footprint, tuning, 4f, [link]);
        Vector3 from = new(2.5f, 0f, 2.5f), to = new(3.5f, 4f, 2.5f);
        AssertSchedules(navigation, from, to, expected);
        AssertSchedules(navigation, to, from, false);
    }

    [Fact]
    public void CornerCrossingUsesTheDiagonalExitInItsOwnDirection()
    {
        var navigation = SegmentAdmissionFixture.Flat(5, 5, 0f,
            allowsEdge: (x, z, nx, nz) => nx - x == 1 && nz - z == 1);
        Vector3 from = new(1.5f, 0f, 1.5f), to = new(3.5f, 0f, 3.5f);
        AssertSchedules(navigation, from, to, true);
        AssertSchedules(navigation, to, from, false);
    }

    internal static void AssertSchedules(GroundNavigation navigation, Vector3 from, Vector3 to, bool expected)
    {
        long expectedCells;
        using (var work = new NavFootprintDiagnostics())
        {
            Assert.Equal(expected, navigation.AllowsSegment(from, to));
            expectedCells = work.CandidateCells;
        }

        long? total = null;
        int[] quanta = [1, 6, 2, 3, 5, 1, 7];
        foreach (int schedule in new[] { 1, 6, 0, int.MaxValue })
        {
            var request = new NavSegmentRequest(navigation, from, to);
            using var work = new NavFootprintDiagnostics();
            Assert.Null(request.Result);
            for (int call = 0; call < 4096 && request.Result is null; call++)
            {
                int quota = schedule == 0 ? quanta[call % quanta.Length] : schedule;
                long before = request.WorkSpent;
                request.Advance(quota);
                Assert.InRange(request.WorkSpent - before, 1, Math.Max(quota, 6));
            }
            Assert.Equal(expected, request.Result);
            Assert.Equal(expectedCells, work.CandidateCells);
            total ??= request.WorkSpent;
            Assert.Equal(total.Value, request.WorkSpent);
        }
    }
}
