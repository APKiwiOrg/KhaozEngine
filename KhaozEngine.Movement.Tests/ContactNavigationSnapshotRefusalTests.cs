using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ContactNavigationSnapshotRefusalTests
{
    [Theory]
    [InlineData(NavigationBuildRefusal.SnapshotLeaseSourceChanged)]
    [InlineData(NavigationBuildRefusal.SnapshotLeaseGenerationChanged)]
    [InlineData(NavigationBuildRefusal.SnapshotLeaseFrameChanged)]
    public void SnapshotLeaseMismatchNamesItsFirstFailedInput(NavigationBuildRefusal reason)
    {
        using var fixture = new ContactNavigationReuseFixture();
        using var other = new OrdinaryContactScene();
        var snapshot = ContactNavigationSnapshot.Capture(fixture.Bake, fixture.Context);
        using var lease = fixture.Scene.Acquire();
        MovementQueryLeaseId expected = lease.Id;
        var actual = new MovementQueryLeaseId(reason == NavigationBuildRefusal.SnapshotLeaseSourceChanged
                ? other.Physics : expected.SourceWorld,
            reason == NavigationBuildRefusal.SnapshotLeaseGenerationChanged
                ? expected.GeometryGeneration + 1 : expected.GeometryGeneration,
            expected.EnvironmentGeneration, expected.FrameEpoch);
        var frame = reason == NavigationBuildRefusal.SnapshotLeaseFrameChanged
            ? new MovementFrameDescriptor(snapshot.Frame.Frame, snapshot.Frame.PhysicsOrigin, snapshot.Frame.Epoch + 1)
            : snapshot.Frame;
        var failure = Assert.Throws<NavigationBuildException>(() => snapshot.InjectChangedLeaseForTests(actual, frame));
        Assert.Equal(reason, failure.Reason);
        if (reason == NavigationBuildRefusal.SnapshotLeaseSourceChanged)
            Assert.Equal(NavigationSourceRole.ReadLease, failure.SourceRole);
        else if (reason == NavigationBuildRefusal.SnapshotLeaseGenerationChanged)
        {
            Assert.Equal(expected.GeometryGeneration, failure.ExpectedGeneration);
            Assert.Equal(actual.GeometryGeneration, failure.ActualGeneration);
        }
        else
        {
            Assert.Equal(snapshot.Frame, failure.ExpectedFrame);
            Assert.Equal(frame, failure.ActualFrame);
        }
        snapshot.InjectChangedLeaseForTests(expected, snapshot.Frame);
        snapshot.Authenticate(lease);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnapshotViewRefusalReportsTheComparedProducer(bool origin)
    {
        using var fixture = new ContactNavigationReuseFixture();
        using var other = new OrdinaryContactScene();
        using var inner = fixture.Scene.Physics.CreateQueryViewExcludingStatics([]);
        using var view = new LegacyScopeView(inner);
        using var context = new GroundMoveContext(fixture.Scene.Height, null, fixture.Scene.Physics, null, null,
            view, fixture.Water, fixture.Scene.Registration, MovementBoundary.Rectangle(-20, -20, 20, 20));
        var snapshot = ContactNavigationSnapshot.Capture(fixture.Bake, context);
        if (origin) view.ReportedOrigin = Vector3.UnitX;
        else view.Owner = other.Physics;
        var failure = Assert.Throws<NavigationBuildException>(snapshot.AssertCurrent);
        Assert.Equal(origin ? NavigationBuildRefusal.SnapshotViewOriginChanged : NavigationBuildRefusal.SnapshotViewSourceChanged,
            failure.Reason);
        if (origin)
        {
            Assert.Equal(Vector3.Zero, failure.ExpectedOrigin);
            Assert.Equal(Vector3.UnitX, failure.ActualOrigin);
        }
        else Assert.Equal(NavigationSourceRole.MovementQueryView, failure.SourceRole);
        view.Owner = fixture.Scene.Physics;
        view.ReportedOrigin = null;
        snapshot.AssertCurrent();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnapshotFrameGuardsKeepBitwiseOrigins(bool contextFrame)
    {
        using var fixture = new ContactNavigationReuseFixture();
        var snapshot = ContactNavigationSnapshot.Capture(fixture.Bake, fixture.Context);
        // Value equality admits signed zero, while the snapshot's identity contract compares its bits.
        var changed = new MovementFrameDescriptor(snapshot.Frame.Frame, new(-0f, 0, 0), snapshot.Frame.Epoch);
        var failure = Assert.Throws<NavigationBuildException>(() =>
            snapshot.InjectChangedFrameForTests(changed, contextFrame));
        Assert.Equal(contextFrame ? NavigationBuildRefusal.SnapshotContextFrameChanged : NavigationBuildRefusal.SnapshotBasisOriginChanged,
            failure.Reason);
        if (contextFrame)
        {
            Assert.Equal(snapshot.Frame, failure.ExpectedFrame);
            Assert.Equal(changed, failure.ActualFrame);
            Assert.Equal(int.MinValue, BitConverter.SingleToInt32Bits(failure.ActualFrame!.Value.PhysicsOrigin.X));
        }
        else
        {
            Assert.Equal(Vector3.Zero, failure.ExpectedOrigin);
            Assert.Equal(changed.PhysicsOrigin, failure.ActualOrigin);
            Assert.Equal(int.MinValue, BitConverter.SingleToInt32Bits(failure.ActualOrigin!.Value.X));
        }
        snapshot.InjectChangedFrameForTests(snapshot.Frame, contextFrame);
        snapshot.AssertCurrent();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegistrationRefusalSelectsGenerationBeforeRuntimeStatics(bool runtime)
    {
        using var fixture = new ContactNavigationReuseFixture();
        var snapshot = ContactNavigationSnapshot.Capture(fixture.Bake, fixture.Context);
        long generation = fixture.Scene.Registration.Generation;
        var failure = Assert.Throws<NavigationBuildException>(() =>
            snapshot.InjectChangedRegistrationForTests(runtime ? generation : generation + 1, true));
        Assert.Equal(runtime ? NavigationBuildRefusal.RuntimeStaticsPresent : NavigationBuildRefusal.RegistrationGenerationChanged,
            failure.Reason);
        if (runtime) Assert.Equal(NavigationSourceRole.MovementRegistration, failure.SourceRole);
        else
        {
            Assert.Equal(generation, failure.ExpectedGeneration);
            Assert.Equal(generation + 1, failure.ActualGeneration);
        }
        snapshot.InjectChangedRegistrationForTests(generation, false);
        snapshot.AssertCurrent();
    }

    [Theory]
    [InlineData("generation", NavigationBuildRefusal.WaterGenerationChanged)]
    [InlineData("identity", NavigationBuildRefusal.WaterIdentityChanged)]
    [InlineData("extent", NavigationBuildRefusal.WaterExtentChanged)]
    [InlineData("signed-zero", NavigationBuildRefusal.WaterExtentChanged)]
    public void WaterRefusalReportsOneProducerInput(string change, NavigationBuildRefusal reason)
    {
        using var fixture = new ContactNavigationReuseFixture();
        fixture.Water.ExtentOverride = new(Vector3.Zero, Vector3.One);
        var snapshot = ContactNavigationSnapshot.Capture(fixture.Bake, fixture.Context);
        string identity = fixture.Water.SemanticIdentity;
        if (change == "generation") fixture.Water.Advance();
        else if (change == "identity") fixture.Water.IdentityOverride = "changed";
        else fixture.Water.ExtentOverride = new(change == "extent" ? Vector3.UnitX : new(-0f, 0, 0), Vector3.One);
        var failure = Assert.Throws<NavigationBuildException>(snapshot.AssertCurrent);
        Assert.Equal(reason, failure.Reason);
        if (change == "generation")
        {
            Assert.Equal(0, failure.ExpectedGeneration);
            Assert.Equal(1, failure.ActualGeneration);
        }
        else if (change == "identity")
        {
            Assert.Equal(identity, failure.ExpectedIdentity);
            Assert.Equal("changed", failure.ActualIdentity);
        }
        else
        {
            Assert.Equal(new MovementWaterExtent(Vector3.Zero, Vector3.One), failure.ExpectedExtent);
            Assert.Equal(fixture.Water.ExtentOverride, failure.ActualExtent);
            if (change == "signed-zero")
                Assert.Equal(int.MinValue, BitConverter.SingleToInt32Bits(failure.ActualExtent!.Value.Minimum.X));
        }
        fixture.Water.IdentityOverride = null;
        fixture.Water.ExtentOverride = new(Vector3.Zero, Vector3.One);
        // A fresh snapshot repairs a generation change without rolling a live producer back.
        ContactNavigationSnapshot.Capture(fixture.Bake, fixture.Context).AssertCurrent();
    }
}
