using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class NavBakeCaptureBasisTests
{
    static readonly MovementBoundary Boundary = MovementBoundary.Rectangle(-20, -20, 20, 20);
    static PhysicsNavBakeOptions Options => new(0, 0, 1, 1, 1, 2, 4,
        OrdinaryContactScene.DefaultTuning.MaxSlopeRadians, 4, 4);
    static GroundMoveContext Context(OrdinaryContactScene scene, IMovementWaterField? water = null,
        Func<float, float, float, MovementMedium>? medium = null) =>
        new(scene.Height, null, scene.Physics, null, medium, null, water, scene.Registration, Boundary);

    [Fact]
    public void CaptureRecordsBasisFromALease()
    {
        using var scene = new OrdinaryContactScene(origin: new(8, 1, -16));
        using var context = Context(scene);
        NavAreaClassifier classifier = _ => 1;
        using PhysicsNavBake capture = PhysicsNavBake.Capture(context, Options, classifier);
        PhysicsNavCaptureBasis basis = Assert.IsType<PhysicsNavCaptureBasis>(capture.Basis);
        using var lease = scene.Physics.AcquireQueryReadLease();
        Assert.Same(scene.Physics, basis.Source);
        Assert.Equal(lease.GeometryGeneration, basis.GeometryGeneration);
        Assert.Equal(scene.Physics.Origin, basis.Origin);
        Assert.Same(classifier, capture.Classifier);
    }

    [Fact]
    public void GeometryChangedAfterCaptureRefusesContactBuild()
    {
        using var scene = new OrdinaryContactScene();
        using var context = Context(scene);
        using PhysicsNavBake capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        scene.Physics.AddStatic(new BoxShape(Vector3.One), Pose.At(new(10, 0, 0)));
        Assert.Throws<InvalidOperationException>(() => capture.BuildProfile(scene.Tuning, default));
    }

    [Fact]
    public void ClassifierThatMutatesGeometryRefusesCapture()
    {
        using var scene = new OrdinaryContactScene(install: (world, _) =>
            world.AddStatic(new BoxShape(new(2, 0.1f, 2)), Pose.At(new(0, -0.1f, 0))));
        using var context = Context(scene);
        int calls = 0;
        var failure = Assert.Throws<InvalidOperationException>(() => PhysicsNavBake.Capture(context, Options, _ =>
        {
            if (calls++ == 0) scene.Physics.AddStatic(new BoxShape(Vector3.One), Pose.At(new(10, 0, 0)));
            return 0;
        }));
        Assert.Equal(1, calls);
        Assert.Contains("geometry", failure.Message);
    }

    [Fact]
    public void WaterFieldWithoutMediumDelegateRefusesSampleWater()
    {
        var water = MovementWaterField.Level("world", 0, -4);
        using var scene = new OrdinaryContactScene(water: water);
        using var context = Context(scene, water);
        var failure = Assert.Throws<InvalidOperationException>(() =>
            PhysicsNavBake.Capture(context, Options with { SampleWater = true }, _ => 0));
        Assert.Contains("medium delegate", failure.Message);
    }

    [Fact]
    public void WaterFieldWithMediumDelegateCapturesWithoutValidatedTuning()
    {
        var water = MovementWaterField.Level("world", 0, -4);
        using var scene = new OrdinaryContactScene(water: water);
        using var context = Context(scene, water, (_, _, _) => new MovementMedium(0, true));
        using PhysicsNavBake capture = PhysicsNavBake.Capture(context, Options with { SampleWater = true }, _ => 2);
        PhysicsNavWater sample = Assert.Single(capture.Columns.Water.ToArray());
        Assert.Equal(0, sample.SurfaceY);
        Assert.Equal(2u, sample.Areas);
    }

    [Fact]
    public void WorldWithoutLeaseSourceKeepsLegacyCapture()
    {
        using var backend = new BepuPhysicsWorld();
        backend.AddStatic(new BoxShape(new(2, 0.1f, 2)), Pose.At(new(0, -0.1f, 0)));
        using var world = new WorldWithoutLeaseSource(backend);
        using var context = new GroundMoveContext((_, _) => 0, physics: world);
        using PhysicsNavBake capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        Assert.Null(capture.Basis);
        Assert.Single(capture.Columns.GetColumn(0, 0).ToArray());
        var legacy = MoveTuning.Default with { MaxSlopeRadians = Options.MaxSlopeRadians };
        Assert.True(capture.BuildProfile(legacy, default).Graph.Layers[0].IsAccepted(0, 0));
        Assert.Throws<InvalidOperationException>(() => capture.BuildProfile(OrdinaryContactScene.DefaultTuning, default));
    }

    sealed class WorldWithoutLeaseSource(IPhysicsWorld inner) : PhysicsForwarder(inner)
    {
        public override void Dispose() { }
    }
}
