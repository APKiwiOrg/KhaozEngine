using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class RuntimeStaticRegistrationTests
{
    internal static MovementSupportKey Owner(string name = "wall") => new("world", name);
    internal static RuntimeStaticChange Wall(float x = 3, string name = "wall") =>
        RuntimeStaticChange.Set(Owner(name), new BoxShape(new(0.2f, 1, 3)), Pose.At(new(x, 1, 0)));

    [Fact]
    public void ExistingBindingReacquiresAfterAddReplacementAndRemovalWithoutChangingAuthoredIdentity()
    {
        using var scene = new OrdinaryContactScene();
        long generation = scene.Registration.Generation;
        long installed = scene.Registration.GeometryGeneration;
        string digest = scene.Registration.SemanticDigest;
        var extent = scene.Registration.Extent;
        var identity = scene.Scope.Identity;
        scene.Registration.ApplyRuntimeStatics([]);
        Assert.Equal(generation, scene.Registration.Generation);
        scene.Registration.ApplyRuntimeStatics([Wall()]);
        Assert.Equal(generation + 1, scene.Registration.Generation);
        Assert.True(scene.Registration.HasRuntimeStatics);
        AssertHit(scene, 3);
        scene.Registration.ApplyRuntimeStatics([Wall(5)]);
        Assert.Equal(generation + 2, scene.Registration.Generation);
        AssertHit(scene, 5);
        scene.Registration.ApplyRuntimeStatics([Wall(5)]);
        Assert.Equal(generation + 3, scene.Registration.Generation);
        scene.Registration.ApplyRuntimeStatics([RuntimeStaticChange.Remove(Owner())]);
        Assert.Equal(generation + 4, scene.Registration.Generation);
        Assert.False(scene.Registration.HasRuntimeStatics);
        using var movement = scene.Acquire();
        Assert.False(scene.Physics.Raycast(new(0, 1, 0), Vector3.UnitX, 10, out _));
        Assert.Equal(installed, scene.Registration.GeometryGeneration);
        Assert.Equal(digest, scene.Registration.SemanticDigest);
        Assert.Equal(extent, scene.Registration.Extent);
        Assert.Equal(identity, scene.Binding.CreateOrdinaryScope(scene.Frame).Identity);
        Assert.Empty(scene.Registration.ExcludedStatics);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    [InlineData("managed-remove")]
    [InlineData("baked-remove")]
    [InlineData("add-remove")]
    public void RawMutationCannotBeLaunderedBySanctionedBatch(string mutation)
    {
        StaticHandle baked = default;
        using var scene = new OrdinaryContactScene(install: (physics, builder) =>
        {
            baked = physics.AddStatic(new BoxShape(Vector3.One), Pose.At(new(-5, 1, 0)));
            builder.Add(baked, 0, int.MaxValue, Owner("baked"), new(-6, 0, -1), new(-4, 2, 1));
        });
        if (mutation != "before") scene.Registration.ApplyRuntimeStatics([Wall()]);
        if (mutation == "managed-remove")
        {
            Assert.True(scene.Physics.Raycast(new(0, 1, 0), Vector3.UnitX, 10, out var hit));
            scene.Physics.RemoveStatic(hit.Body!.Value);
        }
        else if (mutation == "baked-remove") scene.Physics.RemoveStatic(baked);
        else
        {
            var raw = scene.Physics.AddStatic(new BoxShape(Vector3.One), Pose.At(new(12, 1, 0)));
            if (mutation == "add-remove") scene.Physics.RemoveStatic(raw);
        }
        long generation = scene.Registration.Generation;
        scene.Registration.ApplyRuntimeStatics([]);
        Assert.Equal(MovementAvailability.Unresolved, scene.Binding.TryAcquire(scene.Scope, out _));
        Assert.Throws<InvalidOperationException>(() => scene.Registration.ApplyRuntimeStatics([Wall(6, "later")]));
        Assert.Equal(generation, scene.Registration.Generation);
        Assert.Equal(MovementAvailability.Unresolved, scene.Binding.TryAcquire(scene.Scope, out _));
    }

    [Fact]
    public void IndependentRegistrationDoesNotAdoptUpdatesAndStepAndRebaseRemainAuthenticated()
    {
        using var scene = new OrdinaryContactScene();
        OrdinaryMovementRegistration other;
        using (var read = scene.Physics.AcquireQueryReadLease())
            other = OrdinaryMovementRegistration.CreateBuilder("world", scene.Physics, scene.Registration.Extent).Build(read);
        scene.Registration.ApplyRuntimeStatics([Wall()]);
        scene.Physics.Step(1f / 30);
        scene.Physics.Rebase(new(8, 0, -16));
        scene.Registration.ApplyRuntimeStatics([Wall()]);
        using var later = scene.Physics.AcquireQueryReadLease();
        Assert.Equal(MovementAvailability.Known, scene.Registration.Authenticate(later));
        Assert.Equal(MovementAvailability.Unresolved, other.Authenticate(later));
    }

    [Fact]
    public void BatchValidationRefusesEveryInvalidOwnerBeforeEditingSource()
    {
        using var scene = new OrdinaryContactScene(install: (physics, builder) =>
        {
            var baked = physics.AddStatic(new BoxShape(Vector3.One), Pose.At(new(-5, 1, 0)));
            builder.Add(baked, 0, int.MaxValue, Owner("baked"), new(-6, 0, -1), new(-4, 2, 1));
        });
        RuntimeStaticChange[][] invalid = [
            [default], [Wall(), Wall()], [Wall(), RuntimeStaticChange.Remove(Owner())],
            [RuntimeStaticChange.Remove(Owner("absent"))], [Wall(3, "baked")], [Wall(3, "analytic-ground")],
            [RuntimeStaticChange.Set(new("wrong", "wall"), new SphereShape(1), Pose.Identity)],
            [Wall(), RuntimeStaticChange.Set(Owner("outside"), new BoxShape(Vector3.One), Pose.At(new(20, 0, 0)))]];
        long revision = scene.Physics.StaticGeometryGeneration;
        foreach (var changes in invalid)
        {
            Assert.Throws<ArgumentException>(() => scene.Registration.ApplyRuntimeStatics(changes));
            Assert.Equal(revision, scene.Physics.StaticGeometryGeneration);
            using var known = scene.Acquire();
        }
        Assert.Throws<ArgumentException>(() => RuntimeStaticChange.Remove(default));
        Assert.Throws<ArgumentException>(() => RuntimeStaticChange.Set(default, new SphereShape(1), Pose.Identity));
    }

    [Fact]
    public void MutationInsideReadAndSourcesWithoutWriterCapabilityRefuseBeforeEditing()
    {
        using var scene = new OrdinaryContactScene();
        using (var read = scene.Acquire())
            Assert.Throws<InvalidOperationException>(() => scene.Registration.ApplyRuntimeStatics([Wall()]));
        using var source = new ObservedPhysicsWorld();
        OrdinaryMovementRegistration registration;
        using (var read = source.AcquireQueryReadLease()) registration = OrdinaryMovementRegistration.CreateBuilder(
            "world", source, new(new(-20), new(20))).Build(read);
        Assert.Throws<NotSupportedException>(() => registration.ApplyRuntimeStatics([Wall()]));
        using var stillKnown = source.AcquireQueryReadLease();
        Assert.Equal(MovementAvailability.Known, registration.Authenticate(stillKnown));
    }

    static void AssertHit(OrdinaryContactScene scene, float x)
    {
        using var movement = scene.Acquire();
        Assert.True(scene.Physics.Raycast(new(0, 1, 0), Vector3.UnitX, 10, out var hit));
        Assert.Equal(x - 0.2f, hit.Point.X);
    }
}
