using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class MovementEnvironmentContextTests
{
    static readonly BoxShape Box = new(new Vector3(0.5f));

    [Fact]
    public void PreparationRunsBeforeTheGateAndPinningHoldsTheRealPhysicsFence()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        var environment = new EnvironmentAcquisitionFixture(view);
        environment.OnPrepare = () => world.AddStatic(Box, Pose.At(new Vector3(8f)));
        environment.OnPin = () => Assert.Throws<InvalidOperationException>(() => world.AddStatic(Box, Pose.Identity));
        environment.OnDispose = () => Assert.Throws<InvalidOperationException>(() => world.Step(0.01f));
        var result = environment.Acquire();
        using IDisposable? heldLease = result.Lease;
        Assert.Equal(MovementAvailability.Known, result.Status);
        Assert.NotNull(result.Lease);
        Assert.True(environment.Prepared);
        Assert.True(environment.Pinned);
        Assert.Equal(1, environment.WitnessReads);
        Assert.Throws<InvalidOperationException>(() => world.Step(0.01f));
        result.Lease.Dispose();
        Assert.True(environment.Disposed);
        world.AddStatic(Box, Pose.Identity);
    }

    [Theory]
    [InlineData(MovementAvailability.Unresolved)]
    [InlineData(MovementAvailability.Stale)]
    [InlineData(MovementAvailability.Invalid)]
    [InlineData(MovementAvailability.CapacityExceeded)]
    public void AFailedPinDisposesItsPartialPinBeforeReleasingPhysics(MovementAvailability refusal)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        var environment = new EnvironmentAcquisitionFixture(view) { PinAvailability = refusal };
        environment.OnDispose = () => Assert.Throws<InvalidOperationException>(() => world.Step(0.01f));
        var result = environment.Acquire();
        using IDisposable? heldLease = result.Lease;
        Assert.Equal(refusal, result.Status);
        Assert.Null(result.Lease);
        Assert.True(environment.Disposed);
        world.AddStatic(Box, Pose.Identity);
    }

    [Theory]
    [InlineData("preparation-identity")]
    [InlineData("witness-identity")]
    [InlineData("geometry-generation")]
    [InlineData("frame")]
    [InlineData("scope")]
    [InlineData("support-envelope")]
    [InlineData("stale-pin")]
    public void MismatchedOrIncompleteBindingsCannotProduceALease(string fault)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        var environment = new EnvironmentAcquisitionFixture(view) { Fault = fault };
        var result = environment.Acquire();
        using IDisposable? heldLease = result.Lease;
        Assert.NotEqual(MovementAvailability.Known, result.Status);
        Assert.Null(result.Lease);
        if (environment.Pinned) Assert.True(environment.Disposed);
        world.AddStatic(Box, Pose.Identity);
    }

    [Fact]
    public void DistinctViewsThatCompareEqualStillFailExactViewBinding()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var raw = world.CreateQueryViewExcludingStatics([]);
        var selected = new EquatablePhysicsView(raw);
        var substituted = new EquatablePhysicsView(raw);
        Assert.True(selected.Equals(substituted));
        Assert.False(ReferenceEquals(selected, substituted));
        var environment = new EnvironmentAcquisitionFixture(selected) { PinView = substituted };
        var result = environment.Acquire();
        using IDisposable? heldLease = result.Lease;
        Assert.NotEqual(MovementAvailability.Known, result.Status);
        Assert.Null(result.Lease);
        world.AddStatic(Box, Pose.Identity);
    }

    [Fact]
    public void DistinctSourcesThatCompareEqualStillFailExactSourceBinding()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var raw = world.CreateQueryViewExcludingStatics([]);
        var expectedSource = new EquatablePhysicsView(raw);
        var wrongSource = new EquatablePhysicsView(raw);
        var selected = new EquatablePhysicsView(raw)
        {
            ReportedSource = expectedSource,
            LeaseSource = wrongSource
        };
        Assert.True(expectedSource.Equals(wrongSource));
        var environment = new EnvironmentAcquisitionFixture(selected);
        var result = environment.Acquire();
        using IDisposable? heldLease = result.Lease;
        Assert.NotEqual(MovementAvailability.Known, result.Status);
        Assert.Null(result.Lease);
        world.AddStatic(Box, Pose.Identity);
    }

    [Fact]
    public void ThrowingValidationStillDisposesPinAndReleasesPhysics()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        var environment = new EnvironmentAcquisitionFixture(view) { Fault = "throw-validation" };
        environment.OnDispose = () => Assert.Throws<InvalidOperationException>(() => world.Step(0.01f));
        Assert.Throws<EnvironmentAcquisitionFixture.FixtureException>(() => environment.Acquire());
        Assert.True(environment.Disposed);
        world.AddStatic(Box, Pose.Identity);
    }

    [Fact]
    public void ThrowingPartialPinCleanupStillReleasesPhysics()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        var environment = new EnvironmentAcquisitionFixture(view)
        {
            PinAvailability = MovementAvailability.Unresolved,
            ThrowOnDispose = true
        };
        Assert.Throws<EnvironmentAcquisitionFixture.FixtureException>(() => environment.Acquire());
        Assert.True(environment.Disposed);
        world.AddStatic(Box, Pose.Identity);
    }

    [Fact]
    public void ThrowingSuccessfulPinCleanupStillReleasesPhysics()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        var environment = new EnvironmentAcquisitionFixture(view) { ThrowOnDispose = true };
        var result = environment.Acquire();
        using IDisposable? heldLease = result.Lease;
        Assert.Equal(MovementAvailability.Known, result.Status);
        Assert.NotNull(result.Lease);
        Assert.Throws<EnvironmentAcquisitionFixture.FixtureException>(result.Lease.Dispose);
        world.AddStatic(Box, Pose.Identity);
    }
}
