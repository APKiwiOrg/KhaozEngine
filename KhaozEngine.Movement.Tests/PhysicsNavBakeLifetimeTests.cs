using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Movement;

[Collection("AllocSensitive")]
public class PhysicsNavBakeLifetimeTests
{
    private static readonly PhysicsNavBakeOptions Options = new(
        -1f, -1f, 1f, 1f, 1f, 3f, 7f, 0.8f, 16, 64);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DisposedBuilderDoesNotRetainItsProviders(bool physicsSource)
    {
        var (bake, source, classifierTarget) = CaptureAndDispose();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        WeakReference provider = physicsSource ? source : classifierTarget;
        Assert.False(provider.IsAlive, physicsSource
            ? "The disposed builder still retains its physics source."
            : "The disposed builder still retains its classifier target.");
        GC.KeepAlive(bake);
    }

    [Fact]
    public void DisposedBuilderRejectsProviderAccessAndProfileBuilds()
    {
        var (bake, _, _) = CaptureAndDispose();
        MoveTuning legacy = GroundTraversalProbeTests.Tuning;
        MoveTuning contact = legacy with
        {
            Controller = MovementControllerKind.Contact,
            Water = new(WaterTraversalMode.DryOnly),
        };

        Assert.Throws<ObjectDisposedException>(() => { _ = bake.Context; });
        Assert.Throws<ObjectDisposedException>(() => { _ = bake.Basis; });
        Assert.Throws<ObjectDisposedException>(() => { _ = bake.Classifier; });
        Assert.Throws<ObjectDisposedException>(() => bake.BuildProfile(legacy, default));
        Assert.Throws<ObjectDisposedException>(() => bake.BuildProfile(legacy, default, GroundProfileOptions.Default));
        Assert.Throws<ObjectDisposedException>(() => bake.BuildProfile(contact, default));
        Assert.Throws<ObjectDisposedException>(() => bake.BuildProfile(contact, default, GroundProfileOptions.Default));
        Assert.Same(Options, bake.Options);
        Assert.Equal(Vector3.Zero, bake.Origin);
        Assert.Equal(0f, bake.Columns.GetColumn(0, 0)[0].Height);
    }

    // Keep provider locals out of the collecting frame so only the builder can retain them.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (PhysicsNavBake Bake, WeakReference Source, WeakReference ClassifierTarget) CaptureAndDispose()
    {
        using var world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(2f, 0.1f, 2f)), Pose.At(new Vector3(0f, -0.1f, 0f)));
        using var context = new GroundMoveContext((_, _) => 0f, physics: world);
        var classifier = new AreaClassifier();
        var bake = PhysicsNavBake.Capture(context, Options, classifier.Classify);
        Assert.Same(world, bake.Basis!.Value.Source);
        Assert.Same(classifier, bake.Classifier.Target);
        var source = new WeakReference(world);
        var classifierTarget = new WeakReference(classifier);
        bake.Dispose();
        return (bake, source, classifierTarget);
    }

    private sealed class AreaClassifier
    {
        public uint Classify(Vector3 feet) => 0x01u;
    }
}
