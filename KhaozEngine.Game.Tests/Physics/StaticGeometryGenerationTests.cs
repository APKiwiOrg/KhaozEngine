using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class StaticGeometryGenerationTests
{
    [Fact]
    public void StaticGenerationSurvivesReadsDynamicsStepsAndRebases()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var source = Assert.IsAssignableFrom<IPhysicsStaticGeometrySource>(world);
        long empty = source.StaticGeometryGeneration;
        var handle = world.AddStatic(new BoxShape(Vector3.One), Pose.Identity);
        long installed = source.StaticGeometryGeneration;
        Assert.True(installed > empty);
        using var view = world.CreateQueryViewExcludingStatics([]);
        Assert.Equal(installed, Assert.IsAssignableFrom<IPhysicsStaticGeometrySource>(view).StaticGeometryGeneration);
        long read;
        using (var lease = world.AcquireQueryReadLease()) read = lease.GeometryGeneration;
        var body = world.AddDynamic(new SphereShape(0.5f), Pose.At(new(0, 5, 0)), new(1));
        var anchor = world.AddConstraint(ConstraintDescription.BallSocketJoint(ConstraintAttachment.OnBody(body),
            ConstraintAttachment.AtWorld(Pose.At(new(0, 5, 0))), Vector3.Zero, Vector3.Zero));
        world.Step(1f / 30f);
        world.Rebase(new(8, 10, -16));
        world.RemoveConstraint(anchor);
        world.RemoveDynamic(body);
        Assert.Equal(installed, source.StaticGeometryGeneration);
        using (var lease = world.AcquireQueryReadLease()) Assert.NotEqual(read, lease.GeometryGeneration);
        world.RemoveStatic(handle);
        Assert.True(source.StaticGeometryGeneration > installed);
    }
}
