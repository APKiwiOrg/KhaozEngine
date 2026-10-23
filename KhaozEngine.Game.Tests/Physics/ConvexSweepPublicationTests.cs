using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public sealed class ConvexSweepPublicationTests
{
    static readonly ConvexQueryShape Shape = new(ConvexQueryKind.ConeCylinder, 0.25f, 0.25f, 0.5f, 0.75f);

    [Fact]
    public void CoincidentLeavesKeepRawSweepIdentityAndRejectForeignTickets()
    {
        using var fixture = new ConvexPlanarFixture(new(0.03125f, 1, 1), Pose.At(new(0.78125f, 0.625f, 0)));
        var request = new ConvexQueryRequest(Shape, Pose.Identity, Vector3.UnitX, ConvexQueryOperation.Sweep);
        var identities = new ConvexContactIdentity[] { new(false, 17, 2, 3), new(false, 17, 4, 3), new(true, 19, 2, 3), new(true, 21, 2, 3) };
        var owners = Array.ConvertAll(identities, id => new ConvexPlanarDispatch(fixture.Leaf, id));
        SweepHit? dynamicHit = null;
        for (int i = 0; i < owners.Length; i++)
        {
            var work = new ConvexQueryWork();
            Assert.True(owners[i].TryEvaluate(request, ref work, new(), out var ticket));
            Assert.True(owners[i].TryPublishSweep(ticket, out var result));
            Assert.Equal(ConvexSweepStatus.Hit, result.Status);
            Assert.Equal(identities[i], result.Identity);
            Assert.NotNull(result.Hit);
            Assert.True(MathF.Max(0, result.Hit.Value.Distance - 0.001f) < result.ClearThroughDistance);
            Assert.False(owners[i].TryPublish(ticket, out var wrongOperation));
            Assert.Equal(default, wrongOperation);
            for (int other = 0; other < owners.Length; other++)
                if (other != i)
                {
                    Assert.False(owners[other].TryPublishSweep(ticket, out var refused));
                    Assert.Equal(default, refused);
                }
            if (identities[i].Dynamic)
            {
                if (dynamicHit is not null) Assert.Equal(dynamicHit, result.Hit);
                dynamicHit = result.Hit;
            }
        }
    }

    [Fact]
    public void ClearAndUnresolvedHaveNoRawIdentityAndDistanceTicketsCannotBecomeSweeps()
    {
        using var fixture = new ConvexPlanarFixture(new(0.125f), Pose.At(new(1, 0.625f, 0)));
        var owner = new ConvexPlanarDispatch(fixture.Leaf, new(true, 19, 4, 6));
        var request = new ConvexQueryRequest(Shape, Pose.Identity, -Vector3.UnitX, ConvexQueryOperation.Sweep);
        var work = new ConvexQueryWork();
        Assert.True(owner.TryEvaluate(request, ref work, new(), out var ticket));
        Assert.True(owner.TryPublishSweep(ticket, out var result));
        Assert.Equal(ConvexSweepStatus.Clear, result.Status);
        Assert.Equal(default, result.Identity);
        Assert.Null(result.Hit);
        Assert.True(result.ClearThroughDistance >= 1);
        Assert.False(owner.TryPublishSweep(default, out var refused));
        Assert.Equal(default, refused);
        work = new();
        Assert.True(owner.TryEvaluate(request with { Operation = ConvexQueryOperation.Distance, Displacement = Vector3.Zero }, ref work, new(), out ticket));
        Assert.False(owner.TryPublishSweep(ticket, out refused));
        Assert.Equal(default, refused);
    }

    [Fact]
    public void SkinShortenedFloatPlacementHasIndependentPositiveSeparation()
    {
        using var fixture = new ConvexPlanarFixture(new(0.03125f, 1, 1), Pose.At(new(0.78125f, 0.625f, 0)));
        var owner = new ConvexPlanarDispatch(fixture.Leaf, new(false, 17, 2, 3));
        var pose = Pose.At(new(-1024, 0, 0));
        var request = new ConvexQueryRequest(Shape, pose, new(2048, 0, 0), ConvexQueryOperation.Sweep);
        var work = new ConvexQueryWork();
        Assert.True(owner.TryEvaluate(request, ref work, new(), out var ticket));
        Assert.True(owner.TryPublishSweep(ticket, out var result));
        Assert.NotNull(result.Hit);
        float advance = MathF.Max(0, result.Hit.Value.Distance - 0.001f);
        Vector3 placed = pose.Position + Vector3.Normalize(request.Displacement) * advance;
        var oracle = ShortBodyShellOracle.Entry(Shape, Pose.At(placed), fixture.Oracle, Vector3.Zero);
        Assert.True(oracle.IsComplete && oracle.Lower > 0);
        Assert.True(advance < result.ClearThroughDistance);
    }
}
