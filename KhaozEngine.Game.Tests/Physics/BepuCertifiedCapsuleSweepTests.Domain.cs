using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class BepuCertifiedCapsuleSweepTests
{
    [Fact]
    public void StoredCoordinateDomainIncludesTheBoundaryAndRefusesTheNextFloat()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        const float limit = 1_000_000f;
        float centre = limit - 0.5f;
        Assert.Equal(limit, centre + 0.5f);
        StaticHandle inside = world.AddStatic(Box, Pose.At(new(centre, 0, 0)));
        Clear(Read(world), 4);
        world.RemoveStatic(inside);
        float outside = MathF.BitIncrement(centre);
        Assert.True(outside + 0.5f > limit);
        world.AddStatic(Box, Pose.At(new(outside, 0, 0)));
        Refused(Read(world));
    }

    [Fact]
    public void SelectionCannotHideAnOutOfDomainTreeBound()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle far = world.AddStatic(new SphereShape(0.5f), Pose.At(new(2_000_000, 0, 0)));
        using var view = world.CreateQueryViewExcludingStatics([far]);
        Refused(Read(view));
    }
}
