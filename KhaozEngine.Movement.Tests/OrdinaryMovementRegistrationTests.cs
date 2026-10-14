using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class OrdinaryMovementRegistrationTests
{
    static readonly OrdinaryMovementExtent Extent = new(new(-20, -4, -20), new(20, 4, 20), 0, 0);

    [Fact]
    public void RegistrationRefusesMissingExcludedAmbiguousAndStaleFeatures()
    {
        using var physics = new BepuPhysicsWorld(Vector3.Zero);
        var a = physics.AddStatic(new BoxShape(Vector3.One), Pose.Identity);
        var b = physics.AddStatic(new BoxShape(Vector3.One), Pose.At(new(4, 0, 0)));
        var owner = new MovementSupportKey("world", "joined");
        var builder = OrdinaryMovementRegistration.CreateBuilder("world", physics, Extent)
            .Add(a, 0, 5, owner, new(-1), new(1))
            .Add(a, 3, 4, new("world", "conflict"), new(-1), new(1))
            .Add(b, 0, 5, owner, new(3, -1, -1), new(5, 1, 1)).Exclude(b);
        OrdinaryMovementRegistration registration;
        using (var lease = physics.AcquireQueryReadLease())
        {
            registration = builder.Build(lease);
            Assert.Equal(MovementAvailability.Unresolved, registration.TryBind(lease, a, 7, out _));
            Assert.Equal(MovementAvailability.Unresolved, registration.TryBind(lease, b, 0, out _));
            Assert.Equal(MovementAvailability.Unresolved, registration.TryBind(lease, a, 3, out _));
            Assert.Equal(MovementAvailability.Known, registration.TryBind(lease, a, 0, out var bound));
            Assert.Equal(owner, bound);
        }
        physics.RemoveStatic(a);
        physics.AddStatic(new BoxShape(Vector3.One), Pose.Identity);
        using var next = physics.AcquireQueryReadLease();
        Assert.Equal(MovementAvailability.Unresolved, registration.TryBind(next, a, 0, out _));
    }

    [Fact]
    public void SnapshotAliasesCanonicalOwnersAndSurvivesStepAndRebase()
    {
        using var physics = new BepuPhysicsWorld(Vector3.Zero);
        var a = physics.AddStatic(new BoxShape(Vector3.One), Pose.Identity);
        var b = physics.AddStatic(new BoxShape(Vector3.One), Pose.At(new(4, 0, 0)));
        var owner = new MovementSupportKey("world", "authored-platform");
        var builder = OrdinaryMovementRegistration.CreateBuilder("world", physics, Extent)
            .Add(a, 0, int.MaxValue, owner, new(-1), new(1))
            .Add(b, 0, int.MaxValue, owner, new(3, -1, -1), new(5, 1, 1));
        OrdinaryMovementRegistration registration;
        long before;
        long beforeStatic = physics.StaticGeometryGeneration;
        using (var lease = physics.AcquireQueryReadLease())
        {
            registration = builder.Build(lease);
            before = lease.GeometryGeneration;
            Assert.Equal(MovementAvailability.Known, registration.TryBind(lease, a, 5, out var joined));
            Assert.Equal(MovementAvailability.Known, registration.TryBind(lease, b, 5, out var split));
            Assert.Equal(joined, split);
        }
        builder.Exclude(a);
        physics.Step(1f / 30f);
        Assert.Equal(beforeStatic, physics.StaticGeometryGeneration);
        physics.Rebase(new(8, 10, -16));
        Assert.Equal(beforeStatic, physics.StaticGeometryGeneration);
        using var current = physics.AcquireQueryReadLease();
        Assert.NotEqual(before, current.GeometryGeneration);
        Assert.Equal(MovementAvailability.Known, registration.TryBind(current, a, 5, out _));
        Assert.Empty(registration.ExcludedStatics);
    }

    [Fact]
    public void ExtentsAndInstallerProvenanceRefuseInvalidDeclarations()
    {
        Assert.Throws<ArgumentException>(() => new OrdinaryMovementExtent(new(-1), new(1), 0, 2));
        Assert.Throws<ArgumentException>(() => new OrdinaryMovementExtent(new(-1), new(1), 0, null));
        Assert.Throws<ArgumentException>(() => new MovementWaterExtent(new(float.NaN), new(1)));
        using var physics = new BepuPhysicsWorld(Vector3.Zero);
        var builder = OrdinaryMovementRegistration.CreateBuilder("world", physics, Extent);
        Assert.Throws<ArgumentException>(() => builder.Add(new(0), 3, 2, new("world", "owner"), new(-1), new(1)));
        Assert.Throws<ArgumentException>(() => builder.Add(new(0), 0, 5, new("other", "owner"), new(-1), new(1)));
        builder.Add(new(0), 0, 5, new("world", "owner"), new(-30), new(30));
        using var lease = physics.AcquireQueryReadLease();
        Assert.Throws<ArgumentException>(() => builder.Build(lease));
    }

    [Fact]
    public void InstallationOrderCellsAndPartitionCopiesUseAuthoredOwners()
    {
        using var forward = new BepuPhysicsWorld(Vector3.Zero);
        using var reverse = new BepuPhysicsWorld(Vector3.Zero);
        var first = Publish(forward, "world", false);
        var second = Publish(reverse, "world", true);
        using var a = forward.AcquireQueryReadLease();
        using var b = reverse.AcquireQueryReadLease();
        Assert.Equal(MovementAvailability.Known, first.Registration.TryBind(a, first.Left, 0, out var original));
        Assert.Equal(MovementAvailability.Known, second.Registration.TryBind(b, second.Left, 0, out var copy));
        Assert.Equal(original, copy);
        Assert.Equal(first.Registration.SemanticDigest, second.Registration.SemanticDigest);
        Assert.NotEqual(first.Left, second.Left);
        using var cellA = new BepuPhysicsWorld(Vector3.Zero);
        using var cellB = new BepuPhysicsWorld(Vector3.Zero);
        var leftCell = Publish(cellA, "cell-a", false);
        var rightCell = Publish(cellB, "cell-b", false);
        using var c = cellA.AcquireQueryReadLease();
        using var d = cellB.AcquireQueryReadLease();
        Assert.Equal(leftCell.Left, rightCell.Left);
        Assert.Equal(MovementAvailability.Known, leftCell.Registration.TryBind(c, leftCell.Left, 0, out var cellAOwner));
        Assert.Equal(MovementAvailability.Known, rightCell.Registration.TryBind(d, rightCell.Left, 0, out var cellBOwner));
        Assert.NotEqual(cellAOwner, cellBOwner);
        Assert.Equal(MovementAvailability.Invalid, first.Registration.TryBind(b, second.Left, 0, out _));
    }

    static (OrdinaryMovementRegistration Registration, StaticHandle Left) Publish(BepuPhysicsWorld physics,
        string world, bool reverse)
    {
        var left = new BoxShape(Vector3.One);
        StaticHandle a, b;
        if (reverse) { b = physics.AddStatic(left, Pose.At(new(4, 0, 0))); a = physics.AddStatic(left, Pose.Identity); }
        else { a = physics.AddStatic(left, Pose.Identity); b = physics.AddStatic(left, Pose.At(new(4, 0, 0))); }
        var builder = OrdinaryMovementRegistration.CreateBuilder(world, physics, Extent)
            .Add(a, 0, int.MaxValue, new(world, "platform"), new(-1), new(1))
            .Add(b, 0, int.MaxValue, new(world, "platform"), new(3, -1, -1), new(5, 1, 1));
        using var read = physics.AcquireQueryReadLease();
        return (builder.Build(read), a);
    }

    [Fact]
    public void SourceWithoutStaticGenerationRequiresTheOriginalReadGeneration()
    {
        using var source = new ObservedPhysicsWorld();
        var handle = source.AddStatic(new BoxShape(Vector3.One), Pose.Identity);
        var builder = OrdinaryMovementRegistration.CreateBuilder("world", source, Extent)
            .Add(handle, 0, int.MaxValue, new("world", "platform"), new(-1), new(1));
        OrdinaryMovementRegistration registration;
        using (var read = source.AcquireQueryReadLease())
        {
            registration = builder.Build(read);
            Assert.Equal(MovementAvailability.Known, registration.TryBind(read, handle, 0, out _));
        }
        source.Step(1f / 30f);
        using var later = source.AcquireQueryReadLease();
        Assert.Equal(MovementAvailability.Unresolved, registration.TryBind(later, handle, 0, out _));
        Assert.Equal(MovementAvailability.Known, builder.Build(later).TryBind(later, handle, 0, out _));
    }

    [Fact]
    public void SelectedExclusionsNeedCanonicalProvenanceForPortableIdentity()
    {
        using var physics = new BepuPhysicsWorld(Vector3.Zero);
        var handle = physics.AddStatic(new BoxShape(Vector3.One), Pose.Identity);
        var builder = OrdinaryMovementRegistration.CreateBuilder("world", physics, Extent).Exclude(handle);
        using var read = physics.AcquireQueryReadLease();
        Assert.Throws<ArgumentException>(() => builder.Build(read));
        builder.Add(handle, 0, int.MaxValue, new("world", "omitted-platform"), new(-1), new(1));
        var registration = builder.Build(read);
        Assert.Equal(MovementAvailability.Unresolved, registration.TryBind(read, handle, 0, out _));
    }
}
