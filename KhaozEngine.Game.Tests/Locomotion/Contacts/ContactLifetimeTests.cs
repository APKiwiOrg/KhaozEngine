using System;
using System.Numerics;
using System.Threading;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Fixtures;
using KhaozEngine.Tests.Physics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public sealed class ContactLifetimeTests
{
    [Fact]
    public void PreparedWitnessIsImmutableAndReusedAcrossFreshPins()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        string[] resources = ["a", "b"];
        var environment = new EnvironmentAcquisitionFixture(view) { Resources = resources };
        var first = Assert.IsType<MovementQueryLease>(environment.Acquire().Lease);
        MovementScopeWitness witness = first.Witness;
        Assert.Same(witness, environment.LastPin!.Witness);
        first.Dispose();
        using (var next = Assert.IsType<MovementQueryLease>(environment.Acquire().Lease))
            Assert.Same(witness, next.Witness);
        resources[0] = "changed";
        using var changed = Assert.IsType<MovementQueryLease>(environment.Acquire().Lease);
        Assert.NotSame(witness, changed.Witness);
        Assert.Equal(new[] { "a", "b" }, witness.ResourceIds);
        Assert.Equal(new[] { "b", "changed" }, changed.Witness.ResourceIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposedTokensStayExpiredAfterReacquisition(bool throwingDisposal)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        var environment = new EnvironmentAcquisitionFixture(view) { ContactSupportEnabled = true };
        var old = Assert.IsType<MovementQueryLease>(environment.Acquire().Lease);
        var pin = environment.LastPin!;
        var physics = environment.LastPhysicsLease!;
        var request = SupportFootprintTests.Request(0, 0, 0.2f, 0.7f, -0.1, 0.1);
        Span<SupportFootprintMember> members = stackalloc SupportFootprintMember[1];
        Span<ulong> joins = stackalloc ulong[1];
        var footprint = ((IPhysicsSupportFootprint)view).QuerySupportFootprint(physics, request, members, joins);
        environment.ThrowOnDispose = throwingDisposal;
        if (throwingDisposal) Assert.Throws<EnvironmentAcquisitionFixture.FixtureException>(old.Dispose);
        else old.Dispose();
        environment.ThrowOnDispose = false;
        using var next = Assert.IsType<MovementQueryLease>(environment.Acquire().Lease);
        Assert.Equal(old.Id, next.Id);
        Assert.NotSame(old, next);
        Assert.NotSame(pin, environment.LastPin);
        Assert.NotSame(physics, environment.LastPhysicsLease);
        Assert.Throws<ObjectDisposedException>(old.AssertCurrent);
        Assert.Throws<ObjectDisposedException>(physics.AssertCurrent);
        Assert.Throws<ObjectDisposedException>(pin.AssertCurrent);
        Assert.Throws<InvalidOperationException>(() =>
            ((IPhysicsSupportFootprint)view).AssertFootprintCurrent(footprint, environment.LastPhysicsLease!));
        Exception? error = null, readError = null, pinError = null, physicsError = null;
        var thread = new Thread(() =>
        {
            error = Record.Exception(next.Dispose);
            readError = Record.Exception(next.AssertCurrent);
            pinError = Record.Exception(environment.LastPin!.AssertCurrent);
            physicsError = Record.Exception(environment.LastPhysicsLease!.AssertCurrent);
        })
        { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(error);
        Assert.IsType<InvalidOperationException>(readError);
        Assert.IsType<InvalidOperationException>(pinError);
        Assert.IsType<InvalidOperationException>(physicsError);
        next.AssertCurrent();
    }
}
