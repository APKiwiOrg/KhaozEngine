using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

/// <summary>Ignored overlaps cannot contaminate later penetration results or strand query scratch.</summary>
public sealed class PenetrationEligibilityTests
{
    static readonly CapsuleShape Capsule = new(0.5f, 1f);
    static readonly BoxShape Box = new(new Vector3(0.5f));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void IgnoredOverlapReturnsZeroAndReusesScratchAcrossEligibleQueries(bool excluded, bool leased)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle[] hidden = [];
        if (excluded) hidden = [world.AddStatic(Box, Pose.Identity)];
        else world.AddDynamic(Box, Pose.Identity, DynamicBodyDescription.WithMass(1f));
        world.AddStatic(Box, Pose.At(new Vector3(10.75f, 0f, 0f)));
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics(hidden);
        QueryGate gate = ConcurrentReadGateTests.Gate(world);
        QueryScratch scratch;
        using (IPhysicsQueryLease? lease = leased ? world.AcquireQueryReadLease() : null)
        {
            // An empty query creates the managed overlap scratch without requiring native query memory.
            Assert.False(view.ComputePenetration(Capsule, Pose.At(new Vector3(100f)), out Vector3 empty));
            AssertZeroBits(empty);
            scratch = Assert.Single(gate.ScratchPool.Constructed());
            Assert.Equal(0, scratch.NativeCapacity);
            var overlapList = scratch.Overlaps.Idle;
            Assert.NotNull(overlapList);

            Assert.False(view.ComputePenetration(Capsule, Pose.Identity, out Vector3 ignored));
            AssertZeroBits(ignored);
            Assert.Same(overlapList, scratch.Overlaps.Idle);

            Assert.True(view.ComputePenetration(Capsule, Pose.At(new Vector3(10f, 0f, 0f)), out Vector3 hit));
            // The right box begins at x=10.25, leaving exactly 0.25 of capsule penetration.
            Assert.Equal(new Vector3(-0.25f, 0f, 0f), hit);
            Assert.True(scratch.NativeCapacity > 0);
            Assert.False(view.ComputePenetration(Capsule, Pose.Identity, out Vector3 afterHit));
            AssertZeroBits(afterHit);
            Assert.Same(scratch, Assert.Single(gate.ScratchPool.Constructed()));
            Assert.Same(overlapList, scratch.Overlaps.Idle);
            lease?.AssertCurrent();
            Assert.Equal(leased ? 1 : 0, gate.ActiveReaders);
        }
        Assert.Equal(0, gate.ActiveReaders);
        Assert.Equal(1, gate.ScratchPool.IdleCount);
        Assert.Empty(scratch.Overlaps.Idle!);
        view.Dispose();
        world.Dispose();
        Assert.Equal(1, scratch.Disposals);
        Assert.Equal(0, scratch.NativeCapacity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EqualDepthEligibleWallsKeepTheirTieWinnerAmongIgnoredOverlaps(bool reverse)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle hidden = world.AddStatic(new BoxShape(new Vector3(4f)), Pose.Identity);
        float first = reverse ? 0.75f : -0.75f;
        world.AddStatic(Box, Pose.At(new Vector3(first, 0f, 0f)));
        world.AddStatic(Box, Pose.At(new Vector3(-first, 0f, 0f)));
        world.AddDynamic(Box, Pose.Identity, DynamicBodyDescription.WithMass(1f));
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics([hidden]);
        using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
        Assert.True(view.ComputePenetration(Capsule, Pose.Identity, out Vector3 hit));
        Assert.Equal(new Vector3(reverse ? -0.25f : 0.25f, 0f, 0f), hit);
        lease.AssertCurrent();
    }

    [Fact]
    public void NestedPenetrationLeavesTheOuterOverlapLoanIntact()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Box, Pose.At(new Vector3(0.75f, 0f, 0f)));
        QueryGate gate = ConcurrentReadGateTests.Gate(world);
        QueryGate.Holder holder = gate.EnterRead();
        QueryScratch scratch = gate.Scratch(holder);
        var outer = scratch.Overlaps.Take();
        outer.Add(default);
        try
        {
            Assert.True(world.ComputePenetration(Capsule, Pose.Identity, out Vector3 hit));
            Assert.Equal(new Vector3(-0.25f, 0f, 0f), hit);
            Assert.Single(outer);
            Assert.NotSame(outer, scratch.Overlaps.Idle);
            Assert.Equal(1, gate.ActiveReaders);
            Assert.Same(scratch, Assert.Single(gate.ScratchPool.Constructed()));
        }
        finally
        {
            scratch.Overlaps.Restore(outer);
            gate.ExitRead(holder);
        }
        Assert.Same(outer, scratch.Overlaps.Idle);
        Assert.Empty(outer);
        Assert.Equal(0, gate.ActiveReaders);
        Assert.Equal(1, gate.ScratchPool.IdleCount);
    }

    static void AssertZeroBits(Vector3 value)
    {
        Assert.Equal(0, BitConverter.SingleToInt32Bits(value.X));
        Assert.Equal(0, BitConverter.SingleToInt32Bits(value.Y));
        Assert.Equal(0, BitConverter.SingleToInt32Bits(value.Z));
    }
}
