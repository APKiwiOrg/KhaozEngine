using System;
using System.Numerics;
using System.Threading;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public sealed class StableLegacyReadScopeTests
{
    internal static MoveTuning Tuning => GroundTraversalProbeTests.Tuning;
    internal static PhysicsNavBakeOptions Options => new(-1.5f, -0.5f, 1.5f, 0.5f, 1, 4, 8, Tuning.MaxSlopeRadians, 16, 64);
    internal static NavBakeExecution Stable(int degree = 1) => new(degree) { UseStableLegacyReadScope = true };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScopeCoversEveryProviderAndReleasesAfterSuccess(bool create)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        using var inner = world.CreateQueryViewExcludingStatics([]);
        int leases = 0;
        using var view = new LegacyScopeView(inner)
        {
            Acquire = () => { leases++; return ((IPhysicsQueryLeaseSource)inner).AcquireQueryReadLease(); },
        };
        int reads = 0;
        using var context = new GroundMoveContext((_, _) =>
        {
            reads++;
            Assert.Throws<InvalidOperationException>(() => world.Rebase(Vector3.Zero));
            return 0;
        }, (_, _) =>
        {
            Assert.Throws<InvalidOperationException>(() => world.Rebase(Vector3.Zero));
            return Vector3.UnitY;
        }, world, (x, z) =>
        {
            Assert.Throws<InvalidOperationException>(() => world.Rebase(Vector3.Zero));
            return new(x, z);
        }, null, view);
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        GroundNavigation profile = create
            ? GroundNavigationBake.Create(capture, ContactBakeFile.Sources(), [new("legacy", Tuning, default)], Stable(8))
                .GetProfile("legacy")
            : capture.BuildProfile(Tuning, default, GroundProfileOptions.Default, Stable(8));
        Assert.True(reads > 0);
        Assert.Equal(1, leases);
        Assert.True(profile.Graph.IsNodePassable(0, 1, 0));
        world.Rebase(Vector3.Zero);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallbackFailureOrMutationReleasesScope(bool mutation)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        var sentinel = new InvalidOperationException("provider failure");
        int reads = 0;
        using var context = new GroundMoveContext((_, _) =>
        {
            reads++;
            if (!mutation) throw sentinel;
            world.AddStatic(new BoxShape(Vector3.One), Pose.At(new(20, 0, 0)));
            return 0;
        }, physics: world);
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        var failure = Assert.Throws<InvalidOperationException>(() =>
            capture.BuildProfile(Tuning, default, GroundProfileOptions.Default, Stable()));
        if (!mutation) Assert.Same(sentinel, failure);
        Assert.Equal(1, reads);
        world.AddStatic(new BoxShape(Vector3.One), Pose.At(new(30, 0, 0)));
    }

    [Fact]
    public void DefaultRetainsMutableProvidersAndExistingOuterLease()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        bool mutate = true;
        using var context = new GroundMoveContext((_, _) =>
        {
            if (mutate) world.Rebase(Vector3.Zero);
            return 0;
        }, physics: world);
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        Assert.True(capture.BuildProfile(Tuning, default).Graph.IsNodePassable(0, 1, 0));
        mutate = false;
        using var outer = world.AcquireQueryReadLease();
        Assert.True(capture.BuildProfile(Tuning, default, GroundProfileOptions.Default, new(8))
            .Graph.IsNodePassable(0, 1, 0));
        Assert.Throws<InvalidOperationException>(() =>
            capture.BuildProfile(Tuning, default, GroundProfileOptions.Default, Stable()));
        outer.AssertCurrent();
        Assert.Throws<InvalidOperationException>(() => world.Rebase(Vector3.Zero));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactOwnerAndSelectedViewMustDeclareLeaseCapability(bool viewOnly)
    {
        using var backend = GroundTraversalProbeTests.FlatWorld();
        using var inner = backend.CreateQueryViewExcludingStatics([]);
        using var wrapper = new LegacyWorldWithoutLease(backend);
        IPhysicsWorld world = viewOnly ? backend : wrapper;
        using IPhysicsWorldQueryView view = viewOnly ? new LegacyViewWithoutLease(inner)
            : new LegacyScopeView(inner) { Owner = wrapper };
        int reads = 0;
        using var context = new GroundMoveContext((_, _) => { reads++; return 0; }, null, world, null, null, view);
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        var failure = Assert.Throws<NotSupportedException>(() =>
            capture.BuildProfile(Tuning, default, GroundProfileOptions.Default, Stable()));
        Assert.Contains(nameof(IPhysicsQueryLeaseSource), failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, reads);
        Assert.Throws<NotSupportedException>(() => GroundNavigationBake.Create(capture, ContactBakeFile.Sources(),
            [new("legacy", Tuning, default)], Stable()));
        Assert.Equal(0, reads);
        Assert.True(capture.BuildProfile(Tuning, default).Graph.IsNodePassable(0, 1, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SourceViewAndLeaseIdentityMustMatchTheCapturedOwner(int mismatch)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        using var other = new BepuPhysicsWorld();
        using var inner = world.CreateQueryViewExcludingStatics([]);
        using var view = new LegacyScopeView(inner);
        int reads = 0;
        using var context = new GroundMoveContext((_, _) => { reads++; return 0; }, null, world, null, null, view);
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        if (mismatch == 0) view.Owner = other;
        if (mismatch == 1) view.ReportedOrigin = Vector3.UnitX;
        if (mismatch == 2) view.Acquire = () => new LegacyLeaseMetadata(world.AcquireQueryReadLease(), other, Vector3.Zero);
        if (mismatch == 3) view.Acquire = () => new LegacyLeaseMetadata(world.AcquireQueryReadLease(), world, Vector3.UnitX);
        Assert.Throws<InvalidOperationException>(() =>
            capture.BuildProfile(Tuning, default, GroundProfileOptions.Default, Stable()));
        Assert.Equal(0, reads);
        world.Rebase(Vector3.Zero);
    }

    [Fact]
    public void CapturedGeometryIsCheckedWithoutAcquiringASecondLease()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        int reads = 0;
        using var context = new GroundMoveContext((_, _) => { reads++; return 0; }, physics: world);
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        world.AddStatic(new BoxShape(Vector3.One), Pose.At(new(20, 0, 0)));
        Assert.Throws<InvalidOperationException>(() =>
            capture.BuildProfile(Tuning, default, GroundProfileOptions.Default, Stable()));
        Assert.Equal(0, reads);
        world.Rebase(Vector3.Zero);
        // The unselected legacy path retains its historical geometry behavior.
        Assert.True(capture.BuildProfile(Tuning, default).Graph.IsNodePassable(0, 1, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidLeaseIsRefusedBeforeProvidersWithoutDisposingAnotherThreadsLease(bool disposed)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        using var inner = world.CreateQueryViewExcludingStatics([]);
        using var view = new LegacyScopeView(inner);
        int reads = 0;
        using var context = new GroundMoveContext((_, _) => { reads++; return 0; }, null, world, null, null, view);
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        if (disposed)
        {
            var lease = world.AcquireQueryReadLease();
            lease.Dispose();
            view.Acquire = () => lease;
            Assert.Throws<ObjectDisposedException>(() =>
                capture.BuildProfile(Tuning, default, GroundProfileOptions.Default, Stable()));
        }
        else
        {
            using var ready = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            IPhysicsQueryLease? foreign = null;
            Exception? threadFailure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using var lease = world.AcquireQueryReadLease();
                    foreign = lease;
                    ready.Set();
                    release.Wait();
                    lease.AssertCurrent();
                }
                catch (Exception exception) { threadFailure = exception; ready.Set(); }
            });
            thread.Start();
            try
            {
                Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));
                Assert.Null(threadFailure);
                view.Acquire = () => foreign!;
                Assert.Throws<InvalidOperationException>(() =>
                    capture.BuildProfile(Tuning, default, GroundProfileOptions.Default, Stable()));
            }
            finally { release.Set(); thread.Join(); }
            Assert.Null(threadFailure);
        }
        Assert.Equal(0, reads);
        world.Rebase(Vector3.Zero);
    }

    [Fact]
    public void ArgumentValidationStillPrecedesCapabilityAndAcquisition()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        using var context = new GroundMoveContext((_, _) => throw new InvalidOperationException(), physics: world);
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        using var outer = world.AcquireQueryReadLease();
        Assert.Equal("execution", Assert.Throws<ArgumentOutOfRangeException>(() =>
            capture.BuildProfile(Tuning, default, null, Stable(0))).ParamName);
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(() =>
            capture.BuildProfile(Tuning, default, null, Stable())).ParamName);
        Assert.Throws<ArgumentException>(() => capture.BuildProfile(Tuning with { MaxSlopeRadians = 0.1f }, default,
            GroundProfileOptions.Default, Stable()));
        outer.AssertCurrent();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void OrdinaryContactIgnoresLegacyScopeOption(int degree)
    {
        using var scene = new SmallStairScene();
        using var capture = scene.Capture();
        NavBakeProfile[] profiles = [new("contact", SmallStairScene.StairTuning, default)];
        Assert.Equal(ContactBakeFile.Write(GroundNavigationBake.Create(capture, ContactBakeFile.Sources(), profiles, new(degree))),
            ContactBakeFile.Write(GroundNavigationBake.Create(capture, ContactBakeFile.Sources(), profiles, Stable(degree))));
        scene.AssertUsable();
    }
}
