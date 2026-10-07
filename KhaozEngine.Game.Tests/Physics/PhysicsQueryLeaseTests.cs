using System;
using System.Numerics;
using System.Threading;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class PhysicsQueryLeaseTests
{
    static readonly BoxShape Box = new(new Vector3(0.5f));
    static readonly CapsuleShape Capsule = new(0.3f, 0.9f);

    [Theory]
    [InlineData("AddStatic")]
    [InlineData("RemoveStatic")]
    [InlineData("AddDynamic")]
    [InlineData("RemoveDynamic")]
    [InlineData("SetDynamicVelocity")]
    [InlineData("AddConstraint")]
    [InlineData("RemoveConstraint")]
    [InlineData("SetConstraintTarget")]
    [InlineData("Step")]
    [InlineData("Rebase")]
    [InlineData("Dispose")]
    public void ReadLeaseRefusesEveryMutationBeforeItChangesTheWorld(string operation)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var reference = new BepuPhysicsWorld(Vector3.Zero);
        Scene actual = AddScene(world);
        Scene expected = AddScene(reference);
        using (IPhysicsQueryLease lease = Acquire(world))
        {
            long generation = lease.GeometryGeneration;
            Assert.Throws<InvalidOperationException>(() => Mutate(world, actual, operation));
            lease.AssertCurrent();
            Assert.Equal(generation, lease.GeometryGeneration);
            Assert.Equal(Vector3.Zero, world.Origin);
            Assert.True(world.Raycast(Vector3.Zero, Vector3.UnitX, 8f, out RayHit hit));
            Assert.Equal(actual.Solid, hit.Body);
            Assert.Equal(reference.GetDynamicPose(expected.Body), world.GetDynamicPose(actual.Body));
        }

        world.Step(1f / 30f);
        reference.Step(1f / 30f);
        Assert.Equal(reference.GetDynamicPose(expected.Body), world.GetDynamicPose(actual.Body));
        world.GetDynamicVelocity(actual.Body, out Vector3 linear, out Vector3 angular);
        reference.GetDynamicVelocity(expected.Body, out Vector3 expectedLinear, out Vector3 expectedAngular);
        Assert.Equal(expectedLinear, linear);
        Assert.Equal(expectedAngular, angular);
        Assert.Equal(reference.AddStatic(Box, Pose.At(new Vector3(5, 0, 0))),
            world.AddStatic(Box, Pose.At(new Vector3(5, 0, 0))));
    }

    [Fact]
    public void LeaseKeepsTheExactSourceOriginAndGenerationUntilRelease()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.Rebase(new Vector3(128f, 32f, -128f));
        long first;
        using (IPhysicsQueryLease lease = Acquire(world))
        {
            Assert.Same(world, lease.SourceWorld);
            Assert.Equal(new Vector3(128f, 32f, -128f), lease.Origin);
            first = lease.GeometryGeneration;
            lease.AssertCurrent();
        }
        world.AddStatic(Box, Pose.Identity);
        using IPhysicsQueryLease next = Acquire(world);
        Assert.True(next.GeometryGeneration > first);
    }

    [Fact]
    public void AViewLeasePinsItsOwnerAndPreventsViewDisposal()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle hidden = world.AddStatic(Box, Pose.At(new Vector3(2f, 0f, 0f)));
        StaticHandle visible = world.AddStatic(Box, Pose.At(new Vector3(5f, 0f, 0f)));
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics(new[] { hidden });
        using (IPhysicsQueryLease lease = Acquire(view))
        {
            Assert.Same(world, lease.SourceWorld);
            Assert.Throws<InvalidOperationException>(() => world.RemoveStatic(visible));
            Assert.Throws<InvalidOperationException>(view.Dispose);
            Assert.True(view.Raycast(Vector3.Zero, Vector3.UnitX, 8f, out RayHit hit));
            Assert.Equal(visible, hit.Body);
            Assert.True(view.SweepCapsule(Capsule, Pose.Identity, Vector3.UnitX, 8f, out _));
            Assert.False(view.ComputePenetration(Capsule, Pose.At(new Vector3(2f, 0f, 0f)), out _));
        }
        view.Dispose();
        Assert.Throws<ObjectDisposedException>(() => Acquire(view));
        world.RemoveStatic(visible);
        Assert.True(world.Raycast(Vector3.Zero, Vector3.UnitX, 8f, out RayHit remaining));
        Assert.Equal(hidden, remaining.Body);
    }

    [Fact]
    public void NestedLeaseRefusalDoesNotReleaseTheOriginalLease()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using IPhysicsQueryLease lease = Acquire(world);
        Assert.Throws<InvalidOperationException>(() => Acquire(world));
        Assert.Throws<InvalidOperationException>(() => world.AddStatic(Box, Pose.Identity));
        lease.AssertCurrent();
    }

    [Fact]
    public void DisposedLeaseCannotCertifyAQueryAndAllowsANewLease()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        IPhysicsQueryLease lease = Acquire(world);
        lease.Dispose();
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(lease.AssertCurrent);
        world.AddStatic(Box, Pose.Identity);
        using IPhysicsQueryLease next = Acquire(world);
        next.AssertCurrent();
    }

    [Fact]
    public void QueryExceptionDoesNotLeakTheGateOrEndTheLease()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using (IPhysicsQueryLease lease = Acquire(world))
        {
            Assert.Throws<ArgumentException>(() => world.GetDynamicPose(new DynamicBodyHandle(int.MaxValue)));
            lease.AssertCurrent();
            Assert.Throws<InvalidOperationException>(() => world.AddStatic(Box, Pose.Identity));
        }
        StaticHandle solid = world.AddStatic(Box, Pose.At(new Vector3(2f, 0f, 0f)));
        Assert.True(world.Raycast(Vector3.Zero, Vector3.UnitX, 8f, out RayHit hit));
        Assert.Equal(solid, hit.Body);
    }

    [Fact]
    public void WrongThreadCannotUseOrReleaseTheLease()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using IPhysicsQueryLease lease = Acquire(world);
        Exception? readError = null;
        Exception? disposeError = null;
        var thread = new Thread(() =>
        {
            readError = Record.Exception(lease.AssertCurrent);
            disposeError = Record.Exception(lease.Dispose);
        }) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "Wrong-thread checks must not wait on the owned gate.");
        Assert.IsType<InvalidOperationException>(readError);
        Assert.IsType<InvalidOperationException>(disposeError);
        lease.AssertCurrent();
        Assert.Throws<InvalidOperationException>(() => world.Step(1f / 30f));
    }

    [Fact]
    public void DisposedOwnerCannotGrantALease()
    {
        var world = new BepuPhysicsWorld(Vector3.Zero);
        world.Dispose();
        Assert.Throws<ObjectDisposedException>(() => Acquire(world));
    }

    static Scene AddScene(IPhysicsWorld world)
    {
        StaticHandle solid = world.AddStatic(Box, Pose.At(new Vector3(2f, 0f, 0f)));
        Vector3 pivot = new(20f, 5f, 0f);
        DynamicBodyHandle body = world.AddDynamic(Box, Pose.At(pivot),
            new DynamicBodyDescription(1f) { SleepThreshold = 0f });
        ConstraintDescription description = ConstraintDescription.HingeJoint(
            ConstraintAttachment.OnBody(body), ConstraintAttachment.AtWorld(pivot),
            Vector3.Zero, Vector3.Zero, Vector3.UnitZ, Vector3.UnitZ).WithHingeServo(0f);
        return new Scene(solid, body, world.AddConstraint(description), description);
    }

    static void Mutate(IPhysicsWorld world, Scene scene, string operation)
    {
        switch (operation)
        {
            case "AddStatic": world.AddStatic(Box, Pose.Identity); break;
            case "RemoveStatic": world.RemoveStatic(scene.Solid); break;
            case "AddDynamic": world.AddDynamic(Box, Pose.Identity, new DynamicBodyDescription(1f)); break;
            case "RemoveDynamic": world.RemoveDynamic(scene.Body); break;
            case "SetDynamicVelocity": world.SetDynamicVelocity(scene.Body, Vector3.UnitX, Vector3.UnitY); break;
            case "AddConstraint": world.AddConstraint(scene.Description); break;
            case "RemoveConstraint": world.RemoveConstraint(scene.Joint); break;
            case "SetConstraintTarget": world.SetConstraintTarget(scene.Joint, 0.8f); break;
            case "Step": world.Step(1f / 30f); break;
            case "Rebase": world.Rebase(new Vector3(128f, 0f, 0f)); break;
            case "Dispose": world.Dispose(); break;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    readonly record struct Scene(StaticHandle Solid, DynamicBodyHandle Body,
        ConstraintHandle Joint, ConstraintDescription Description);

    static IPhysicsQueryLease Acquire(IPhysicsWorld source) =>
        Assert.IsAssignableFrom<IPhysicsQueryLeaseSource>(source).AcquireQueryReadLease();
}
