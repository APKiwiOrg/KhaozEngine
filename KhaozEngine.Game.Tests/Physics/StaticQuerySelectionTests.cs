using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class StaticQuerySelectionTests
{
    static readonly BoxShape UnitBox = new(new Vector3(0.5f));
    static readonly CapsuleShape Capsule = new(0.3f, 0.9f);

    [Fact]
    public void ExcludedNearestRayStillFindsAllowedStatic()
    {
        using IPhysicsWorld owner = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle near = owner.AddStatic(UnitBox, Pose.At(new Vector3(2f, 0f, 0f)));
        StaticHandle far = owner.AddStatic(UnitBox, Pose.At(new Vector3(5f, 0f, 0f)));
        using IPhysicsWorldQueryView view = owner.CreateQueryViewExcludingStatics(new[] { near });

        Assert.True(view.Raycast(Vector3.Zero, Vector3.UnitX, 8f, out RayHit selected));
        Assert.Equal(far, selected.Body);
        Assert.InRange(selected.Distance, 4.49997f, 4.50003f);
        Assert.True(owner.Raycast(Vector3.Zero, Vector3.UnitX, 8f, out RayHit complete));
        Assert.Equal(near, complete.Body);
        Assert.InRange(complete.Distance, 1.49997f, 1.50003f);
    }

    [Fact]
    public void ExcludedNearestSweepStillFindsAllowedStatic()
    {
        using IPhysicsWorld owner = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle near = owner.AddStatic(UnitBox, Pose.At(new Vector3(2f, 0f, 0f)));
        StaticHandle far = owner.AddStatic(UnitBox, Pose.At(new Vector3(5f, 0f, 0f)));
        using IPhysicsWorldQueryView view = owner.CreateQueryViewExcludingStatics(new[] { near });

        Assert.True(view.SweepCapsule(Capsule, Pose.Identity, Vector3.UnitX, 8f, out SweepHit selected));
        Assert.Equal(far, selected.Body);
        Assert.InRange(selected.Distance, 4.19f, 4.21f);
        Assert.True(owner.SweepCapsule(Capsule, Pose.Identity, Vector3.UnitX, 8f, out SweepHit complete));
        Assert.Equal(near, complete.Body);
        Assert.InRange(complete.Distance, 1.19f, 1.21f);
    }

    [Fact]
    public void ExcludedDeepestOverlapStillFindsAllowedWall()
    {
        using IPhysicsWorld owner = new BepuPhysicsWorld(Vector3.Zero);
        using IPhysicsWorld wallOnly = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle floor = owner.AddStatic(new BoxShape(new Vector3(4f, 0.25f, 4f)),
            Pose.At(new Vector3(0f, 0.25f, 0f)));
        var wall = new BoxShape(new Vector3(0.25f, 2f, 4f));
        Pose wallPose = Pose.At(new Vector3(0.4f, 1f, 0f));
        owner.AddStatic(wall, wallPose);
        wallOnly.AddStatic(wall, wallPose);
        using IPhysicsWorldQueryView view = owner.CreateQueryViewExcludingStatics(new[] { floor });
        Pose capsulePose = Pose.At(new Vector3(0f, 0.75f, 0f));

        Assert.True(wallOnly.ComputePenetration(Capsule, capsulePose, out Vector3 reference));
        Assert.True(reference.X < -0.1f);
        Assert.True(view.ComputePenetration(Capsule, capsulePose, out Vector3 selected));
        Assert.InRange(Vector3.Distance(reference, selected), 0f, 0.00003f);
        Assert.True(owner.ComputePenetration(Capsule, capsulePose, out Vector3 complete));
        Assert.True(Vector3.Distance(reference, complete) > 0.1f);
    }

    [Fact]
    public void CompoundRootExclusionAppliesToChildren()
    {
        using IPhysicsWorld owner = new BepuPhysicsWorld(Vector3.Zero);
        var compound = new CompoundShape(new[]
        {
            new CompoundChild(UnitBox, Pose.At(new Vector3(2f, 0f, -2f))),
            new CompoundChild(UnitBox, Pose.At(new Vector3(2f, 0f, 2f))),
        });
        StaticHandle root = owner.AddStatic(compound, Pose.Identity);
        StaticHandle left = owner.AddStatic(UnitBox, Pose.At(new Vector3(5f, 0f, -2f)));
        StaticHandle right = owner.AddStatic(UnitBox, Pose.At(new Vector3(5f, 0f, 2f)));
        using IPhysicsWorldQueryView view = owner.CreateQueryViewExcludingStatics(new[] { root });

        foreach (var (z, allowed) in new[] { (-2f, left), (2f, right) })
        {
            Vector3 start = new(0f, 0f, z);
            Assert.True(view.Raycast(start, Vector3.UnitX, 8f, out RayHit ray));
            Assert.Equal(allowed, ray.Body);
            Assert.True(view.SweepCapsule(Capsule, Pose.At(start), Vector3.UnitX, 8f, out SweepHit sweep));
            Assert.Equal(allowed, sweep.Body);
            Assert.True(owner.Raycast(start, Vector3.UnitX, 8f, out RayHit complete));
            Assert.Equal(root, complete.Body);
            Pose overlap = Pose.At(new Vector3(2f, 0f, z));
            Assert.True(owner.ComputePenetration(Capsule, overlap, out _));
            Assert.False(view.ComputePenetration(Capsule, overlap, out _));
        }
    }

    [Fact]
    public void ExclusionsRetainQueryMobility()
    {
        using IPhysicsWorld owner = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle excluded = owner.AddStatic(UnitBox, Pose.At(new Vector3(2f, 0f, 0f)));
        owner.AddDynamic(UnitBox, Pose.At(new Vector3(3f, 0f, 0f)), DynamicBodyDescription.WithMass(1f));
        StaticHandle allowed = owner.AddStatic(UnitBox, Pose.At(new Vector3(5f, 0f, 0f)));
        using IPhysicsWorldQueryView view = owner.CreateQueryViewExcludingStatics(new[] { excluded });

        foreach (QueryFilter filter in new[] { QueryFilter.All, QueryFilter.StaticsOnly, QueryFilter.DynamicsOnly })
        {
            Assert.True(view.Raycast(Vector3.Zero, Vector3.UnitX, 8f, out RayHit ray, filter));
            Assert.True(view.SweepCapsule(Capsule, Pose.Identity, Vector3.UnitX, 8f, out SweepHit sweep, filter));
            if (filter.Mobility == QueryMobility.Statics)
            {
                Assert.Equal(allowed, ray.Body);
                Assert.Equal(allowed, sweep.Body);
                Assert.InRange(ray.Distance, 4.49997f, 4.50003f);
            }
            else
            {
                Assert.Null(ray.Body);
                Assert.Null(sweep.Body);
                Assert.InRange(ray.Distance, 2.49997f, 2.50003f);
                Assert.InRange(sweep.Distance, 2.19f, 2.21f);
            }
        }

        Assert.False(view.ComputePenetration(Capsule, Pose.At(new Vector3(3f, 0f, 0f)), out _));
        Assert.True(owner.Raycast(Vector3.Zero, Vector3.UnitX, 8f, out RayHit complete));
        Assert.Equal(excluded, complete.Body);
    }
}
