using System;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class BepuCertifiedCapsuleSweepTests
{
    [Fact]
    public void SelectedViewCanExcludeAnUnprovedBoundFamily()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle sphere = world.AddStatic(new SphereShape(0.5f), Pose.At(new(100, 0, 0)));
        world.AddStatic(Box, Pose.Identity);
        Refused(Read(world));
        using var view = world.CreateQueryViewExcludingStatics([sphere]);
        Contains(Read(view), 1.25);
    }

    [Fact]
    public void StaticsFilterCanExcludeUnprovedDynamicGeometry()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddDynamic(new SphereShape(0.5f), Pose.At(new(100, 0, 0)), new DynamicBodyDescription(0));
        world.AddStatic(Box, Pose.Identity);
        Refused(Read(world));
        Contains(Read(world, filter: QueryFilter.StaticsOnly), 1.25);
    }

    [Fact]
    public void DynamicsFilterCanExcludeUnprovedStaticGeometry()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new SphereShape(0.5f), Pose.At(new(100, 0, 0)));
        world.AddDynamic(Box, Pose.Identity, new DynamicBodyDescription(0));
        Refused(Read(world));
        Contains(Read(world, filter: QueryFilter.DynamicsOnly), 1.25);
    }

    [Fact]
    public void ExclusionSnapshotDoesNotExcludeAReplacementHandle()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle old = world.AddStatic(Box, Pose.Identity);
        using var view = world.CreateQueryViewExcludingStatics([old]);
        Clear(Read(view), 4);
        world.RemoveStatic(old);
        StaticHandle replacement = world.AddStatic(Box, Pose.Identity);
        Assert.NotEqual(old, replacement);
        Contains(Read(view), 1.25);
    }

    [Fact]
    public void ANewWallIsSeenByAnExistingView()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        Clear(Read(view), 4);
        world.AddStatic(Box, Pose.Identity);
        Contains(Read(view), 1.25);
    }

    [Fact]
    public void RemovingAWallUpdatesTheExistingView()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle wall = world.AddStatic(Box, Pose.Identity);
        using var view = world.CreateQueryViewExcludingStatics([]);
        Contains(Read(view), 1.25);
        world.RemoveStatic(wall);
        Clear(Read(view), 4);
    }

    [Fact]
    public void ASteppedKinematicUsesItsCurrentInstalledPose()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        DynamicBodyHandle body = world.AddDynamic(Box, Pose.Identity,
            new DynamicBodyDescription(0) { LinearVelocity = Vector3.UnitX });
        Contains(Read(world), 1.25);
        world.Step(1f / 30f);
        Pose pose = world.GetDynamicPose(body);
        Assert.True(pose.Position.X > 0);
        Vector3 queryCentre = pose.Position - new Vector3(2, 0, 0);
        Contains(Read(world, pose: Pose.At(queryCentre)), (double)pose.Position.X - 0.75 - queryCentre.X);
    }

    [Fact]
    public void VelocityMutationAndBodyRemovalKeepTheQueryCurrent()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        DynamicBodyHandle body = world.AddDynamic(Box, Pose.Identity, new DynamicBodyDescription(0));
        Contains(Read(world), 1.25);
        world.SetDynamicVelocity(body, Vector3.UnitX, Vector3.Zero);
        Contains(Read(world), 1.25);
        world.Step(1f / 30f);
        Pose pose = world.GetDynamicPose(body);
        Assert.True(pose.Position.X > 0);
        Vector3 queryCentre = pose.Position - new Vector3(2, 0, 0);
        Contains(Read(world, pose: Pose.At(queryCentre)), (double)pose.Position.X - 0.75 - queryCentre.X);
        world.RemoveDynamic(body);
        Clear(Read(world), 4);
    }

    [Fact]
    public void RebaseRechecksTheActualTranslatedBounds()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Box, Pose.At(new(54, 1.5f, -98)));
        using var view = world.CreateQueryViewExcludingStatics([]);
        Contains(Read(view, pose: Pose.At(new(52, 1.5f, -98))), 1.25);
        world.Rebase(new(54, 1.5f, -98));
        Assert.Equal(new Vector3(54, 1.5f, -98), view.Origin);
        Contains(Read(view), 1.25);
    }

    [Fact]
    public void NoOpRebaseStillCompletesItsGenerationTransition()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Box, Pose.Identity);
        Contains(Read(world), 1.25);
        world.Rebase(world.Origin);
        Contains(Read(world), 1.25);
    }

    [Fact]
    public void RefusedLeasedMutationDoesNotInvalidateUnchangedEvidence()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var sweep = Assert.IsAssignableFrom<IPhysicsCapsuleSweep>(world);
        using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
        long generation = lease.GeometryGeneration;
        Assert.Throws<InvalidOperationException>(() => world.AddStatic(Box, Pose.Identity));
        lease.AssertCurrent();
        Assert.Equal(generation, lease.GeometryGeneration);
        Clear(sweep.SweepCapsuleCertified(Capsule, Pose.At(new(-2, 0, 0)), new(4, 0, 0)), 4);
    }

    [Fact]
    public void PartialConstraintFailureCannotBeHealedByLaterRegistration()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        Clear(Read(world), 4);
        int before;
        using (IPhysicsQueryLease lease = world.AcquireQueryReadLease())
        {
            before = Simulation(world).Bodies.ActiveSet.Count;
            lease.AssertCurrent();
        }
        ConstraintDescription broken = ConstraintDescription.BallSocketJoint(
            ConstraintAttachment.AtWorld(Vector3.Zero), ConstraintAttachment.OnBody(new DynamicBodyHandle(99999)),
            Vector3.Zero, Vector3.Zero);
        Assert.Throws<ArgumentException>(() => world.AddConstraint(broken));
        using (IPhysicsQueryLease lease = world.AcquireQueryReadLease())
        {
            Assert.Equal(before + 1, Simulation(world).Bodies.ActiveSet.Count);
            lease.AssertCurrent();
        }
        Refused(Read(world));
        world.AddStatic(Box, Pose.Identity);
        Refused(Read(world));
    }

    [Fact]
    public void UnhandledGenerationCannotBeHealedByLaterRegistration()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        Clear(Read(world), 4);
        // Owned-world injection through the actual mutation gate models a future missed bookkeeping hook.
        MethodInfo? enter = typeof(BepuPhysicsWorld).GetMethod("EnterMutation", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(enter);
        object? operation = enter.Invoke(world, [false, true]);
        Assert.NotNull(operation);
        Assert.IsAssignableFrom<IDisposable>(operation).Dispose();
        Refused(Read(world));
        world.AddStatic(Box, Pose.Identity);
        Refused(Read(world));
    }

    [Fact]
    public void ConstraintTargetAndRemovalKeepCompleteRecords()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Box, Pose.Identity);
        DynamicBodyHandle body = world.AddDynamic(Box, Pose.At(new(20, 0, 0)), new DynamicBodyDescription(1));
        Contains(Read(world, filter: QueryFilter.StaticsOnly), 1.25);
        ConstraintDescription description = ConstraintDescription.HingeJoint(
            ConstraintAttachment.OnBody(body), ConstraintAttachment.AtWorld(new Vector3(20, 0, 0)),
            Vector3.Zero, Vector3.Zero, Vector3.UnitY, Vector3.UnitY)
            with
        { Motor = ConstraintMotor.HingeVelocity, MotorTarget = 0 };
        ConstraintHandle joint = world.AddConstraint(description);
        Contains(Read(world, filter: QueryFilter.StaticsOnly), 1.25);
        world.SetConstraintTarget(joint, 0.5f);
        Contains(Read(world, filter: QueryFilter.StaticsOnly), 1.25);
        world.RemoveConstraint(joint);
        Contains(Read(world, filter: QueryFilter.StaticsOnly), 1.25);
        world.AddConstraint(description);
        world.RemoveDynamic(body);
        Contains(Read(world), 1.25);
    }

    [Fact]
    public void DisposedReceiverCannotUseItsCapturedCapability()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var view = world.CreateQueryViewExcludingStatics([]);
        var sweep = Assert.IsAssignableFrom<IPhysicsCapsuleSweep>(view);
        view.Dispose();
        Assert.Throws<ObjectDisposedException>(() => sweep.SweepCapsuleCertified(Capsule, Pose.Identity, Vector3.UnitX));
        Clear(Read(world), 4);
    }

    [Fact]
    public void FiniteOverBudgetTreeCannotPublishAPrefix()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var sweep = Assert.IsAssignableFrom<IPhysicsCapsuleSweep>(world);
        var small = new BoxShape(new Vector3(0.03125f));
        for (int i = 0; i < 4097; i++)
            world.AddStatic(small, Pose.At(new Vector3(i % 17, i / 17 % 17, i / 289) * 0.5f));
        using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
        var simulation = Simulation(world);
        Assert.Equal(4097, simulation.BroadPhase.StaticTree.LeafCount);
        Vector3 centre = new(-1), delta = new(10);
        Assert.True(CapsuleSweepAperture.TryCreate(centre, 0.25f, 0.5f, delta, out Vector3 min, out Vector3 max));
        Assert.False(CapsuleTreePreflight.TryValidate(in simulation.BroadPhase.StaticTree, min, max,
            8192, 4096, 128, out _, out _, out _));
        // Never call the raw overlap enumerator on this tree after the bounded preflight refused.
        Refused(sweep.SweepCapsuleCertified(Capsule, Pose.At(centre), delta));
        lease.AssertCurrent();
    }
}
