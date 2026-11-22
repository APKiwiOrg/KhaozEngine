using System.Linq;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Shared;
using Xunit;
using static KhaozEngine.Tests.Movement.SegmentAdmissionFixture;

namespace KhaozEngine.Tests.Movement;

public sealed class RoutePublicationDigestTests
{
    [Theory]
    [InlineData(0, "?")]
    [InlineData(1, "?")]
    [InlineData(2, "?")]
    public void StraightenedRequestsPublishIdenticalRouteBitsAndCalls(int schedule, string expected)
    {
        var fixtures = RouteCalibrationFixture.StraighteningRows()
            .Concat(RouteCalibrationFixture.WholeRoutes().Take(3));
        var digest = new PathPublicationDigest();
        foreach (var fixture in fixtures)
        {
            using var pool = fixture.CreatePool();
            using var request = fixture.Begin(pool);
            for (int call = 0; call < 20_000; call++)
            {
                request.Advance(new(PathPublicationDigest.Quota(schedule, call)));
                if (request.Result is not { } route) continue;
                digest.Append(route, call + 1);
                break;
            }
        }

        Assert.Equal(7, digest.Count);
        Assert.Equal(expected, digest.Hex);
    }
}

public sealed partial class MoveToRangePlanningTests
{
    [Theory]
    [InlineData(int.MaxValue, "?")]
    [InlineData(64, "?")]
    [InlineData(17, "?")]
    public void WalkingMoverAdoptsEveryRouteOnTheSameCall(int quota, string expected)
    {
        var navigation = Flat(10, 3, Tuning.CapsuleRadius);
        using var pool = navigation.CreatePlanningWorkspaces(1);
        using var context = new GroundMoveContext((_, _) => 0f);
        var mover = new MoveToRange(navigation, Follow(pool, quota), Straight);
        var digest = new PathPublicationDigest();
        MoveState body = At(0.5f);
        NavPath? previous = null;
        bool arrived = false;
        for (int call = 1; call <= 256; call++)
        {
            var steering = Tick(mover, body, Target(8.75f), context);
            if (mover.ActivePathForTests is { } route && !ReferenceEquals(previous, route))
            {
                digest.Append(route, call);
                previous = route;
            }
            if (steering.Status == RangeMoveStatus.InRange)
            {
                arrived = true;
                break;
            }
            body = context.Step(body, steering.WorldDirection, false, Dt, Tuning);
        }
        mover.Reset();

        Assert.True(arrived);
        Assert.True(digest.Count > 0);
        Assert.Equal(expected, digest.Hex);
    }
}
