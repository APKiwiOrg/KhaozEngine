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
    [InlineData(0, "6E4C8E09AA583E91/DE4E57320ED16394")]
    [InlineData(1, "6E4C8E09AA583E91/C06A9DF8068DB617")]
    [InlineData(2, "6E4C8E09AA583E91/068B5E557E5D0EE1")]
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
    [InlineData(int.MaxValue, "65135DF7E8BB2CEE/AD2ACA7747985764")]
    [InlineData(64, "65135DF7E8BB2CEE/EDCAA17D2D4C3E66")]
    [InlineData(17, "65135DF7E8BB2CEE/4A50DAED6A5A40FD")]
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
