using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.TileWorld.Physics;
using Xunit;
using static KhaozEngine.Tests.TileWorld.Physics.TileWorldPhysicsQueryTests;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class FilteredTileInstallationTests
{
    [Fact]
    public void PredicateIsCalledOnceInCanonicalAbsoluteOrder()
    {
        var colliders = Build();
        Vector3 origin = new(40.5f, 3.25f, -24.75f);
        using var world = new RecordingWorld(new BepuPhysicsWorld());
        world.Rebase(origin);
        var visited = new List<TileCollider>();
        using var installed = colliders.AddTo(world, collider =>
        {
            Assert.Empty(world.Added);
            Assert.Equal(0, world.OriginReads);
            visited.Add(collider);
            return true;
        });
        Assert.Equal(colliders.Colliders, visited);
        Assert.Equal(1, world.OriginReads);
        Assert.Equal(colliders.Colliders.Select(c => (c.Shape,
            new Pose(c.Pose.Position - origin, c.Pose.Orientation))), world.StaticAdds);
    }

    [Fact]
    public void SelectedHandlesRetainOriginalIndices()
    {
        var colliders = Build();
        int[] selected = [1, 3, 6, 8];
        using var world = new RecordingWorld(new BepuPhysicsWorld());
        int next = 0;
        var installed = colliders.AddTo(world, _ => selected.Contains(next++));
        Assert.Equal(selected, installed.ColliderIndices);
        Assert.Equal(installed.Handles.Count, installed.ColliderIndices.Count);
        Assert.Equal(selected.Select(i => (colliders.Colliders[i].Shape, colliders.Colliders[i].Pose)), world.StaticAdds);
        var indices = Assert.IsAssignableFrom<IList<int>>(installed.ColliderIndices);
        Assert.True(indices.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => indices[0] = 0);
        installed.Remove();
        installed.Dispose();
        Assert.Equal(selected, installed.ColliderIndices);
        Assert.Equal(installed.Handles, world.Removed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyAndGroundOnlySelectionsRetainResidentExtent(bool groundOnly)
    {
        var colliders = Build();
        using var world = new BepuPhysicsWorld();
        long generation = world.StaticGeometryGeneration;
        using var installed = colliders.AddTo(world, c => groundOnly && c.Kind == TileColliderKind.Ground);
        var registration = installed.CreateMovementRegistration("tiles");
        Assert.Equal(colliders.MovementExtent, registration.Extent);
        Assert.Equal(installed.Handles, installed.GroundHandles);
        Assert.Equal(installed.GroundHandles, registration.ExcludedStatics);
        if (groundOnly)
        {
            Assert.Single(installed.Handles);
            Assert.True(world.Raycast(new(30, 50, -30), -Vector3.UnitY, 100, out _));
            using var view = installed.CreateMovementQueryView();
            Assert.False(view.Raycast(new(30, 50, -30), -Vector3.UnitY, 100, out _));
        }
        else
        {
            Assert.Empty(installed.Handles);
            Assert.Empty(installed.ColliderIndices);
            Assert.Empty(registration.ExcludedStatics);
            installed.Remove();
            Assert.Equal(generation, world.StaticGeometryGeneration);
        }
    }

    [Fact]
    public void NullArgumentsAndPredicateFailureDoNotInsertStatics()
    {
        var colliders = Build();
        Assert.Throws<ArgumentNullException>(() => colliders.AddTo(null!));
        Assert.Throws<ArgumentNullException>(() => colliders.AddTo(null!, _ => true));
        using var world = new RecordingWorld(new BepuPhysicsWorld());
        Assert.Throws<ArgumentNullException>(() => colliders.AddTo(world, null!));
        var failure = new InvalidOperationException("policy refused");
        int calls = 0;
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => colliders.AddTo(world, _ =>
        {
            if (++calls == colliders.Colliders.Count) throw failure;
            return true;
        })));
        Assert.Equal(colliders.Colliders.Count, calls);
        Assert.Empty(world.Added);
        Assert.Empty(world.RemovalAttempts);
        Assert.Equal(0, world.OriginReads);
    }

    [Fact]
    public void SelectedAddFailureRollsBackOnlyAddedHandles()
    {
        var colliders = Build();
        var failure = new InvalidOperationException("selected add refused");
        var backend = new BepuPhysicsWorld();
        StaticHandle survivor = backend.AddStatic(new BoxShape(Vector3.One), Pose.At(new(-10, 2, -10)));
        using var world = new RecordingWorld(backend) { FailAddAt = 2, AddFailure = failure };
        int index = 0;
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
            colliders.AddTo(world, _ => new[] { 1, 3, 6, 8 }.Contains(index++))));
        Assert.Equal(2, world.Added.Count);
        Assert.Equal(world.Added, world.Removed);
        Assert.DoesNotContain(survivor, world.RemovalAttempts);
        Assert.True(backend.Raycast(new(-10, 10, -10), -Vector3.UnitY, 20, out var hit));
        Assert.Equal(survivor, hit.Body);
        Assert.Equal(new[] { 1, 3 }.Select(i => (colliders.Colliders[i].Shape, colliders.Colliders[i].Pose)), world.StaticAdds);
    }

    [Fact]
    public void SelectedCleanupFailurePreservesExceptionOrderAndRetry()
    {
        var colliders = Build();
        var addFailure = new InvalidOperationException("add refused");
        var cleanupFailure = new InvalidOperationException("cleanup refused");
        using (var world = new RecordingWorld(new BepuPhysicsWorld()) { FailAddAt = 2, AddFailure = addFailure })
        {
            world.FailAddedRemovalOnce.Add(0, cleanupFailure);
            int index = 0;
            var thrown = Assert.Throws<AggregateException>(() => colliders.AddTo(world, _ => index++ % 2 == 1));
            Assert.Equal(new Exception[] { addFailure, cleanupFailure }, thrown.InnerExceptions);
            Assert.Equal(world.Added, world.RemovalAttempts);
            Assert.Equal(world.Added.Skip(1), world.Removed);
        }

        using var retryWorld = new RecordingWorld(new BepuPhysicsWorld());
        int next = 0;
        using var installed = colliders.AddTo(retryWorld, _ => next++ % 2 == 1);
        StaticHandle first = installed.Handles[0], last = installed.Handles[^1];
        var lastFailure = new InvalidOperationException("last refused");
        retryWorld.FailRemovalOnce.Add(first, cleanupFailure);
        retryWorld.FailRemovalOnce.Add(last, lastFailure);
        var removal = Assert.Throws<AggregateException>(installed.Remove);
        Assert.Equal(new Exception[] { cleanupFailure, lastFailure }, removal.InnerExceptions);
        Assert.Equal(installed.Handles, retryWorld.RemovalAttempts);
        installed.Remove();
        installed.Remove();
        installed.Dispose();
        Assert.Equal(installed.Handles.Concat(new[] { first, last }), retryWorld.RemovalAttempts);
        Assert.Equal(installed.Handles.OrderBy(h => h.Value), retryWorld.Removed.OrderBy(h => h.Value));
    }

    private static TileWorldColliders Build() => TileWorldColliders.Build(
        TileWorldPhysicsTestData.EveryKindWorld(), TileWorldPhysicsTestData.Catalogs(), "tiles");
}
