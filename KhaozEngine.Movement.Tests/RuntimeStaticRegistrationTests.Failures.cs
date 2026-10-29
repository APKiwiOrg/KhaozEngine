using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class RuntimeStaticFailureTests
{
    [Theory]
    [InlineData("before", false)]
    [InlineData("after", true)]
    [InlineData("replacement", true)]
    [InlineData("replacement-before", false)]
    [InlineData("later", true)]
    [InlineData("reentrant", true)]
    [InlineData("handle", true)]
    [InlineData("missing-revision", true)]
    public void FailurePreservesTrustOnlyWhenNoPhysicalWriteOccurred(string failure, bool poisoned)
    {
        using var source = new RuntimeStaticFailureWorld();
        var registration = Build(source);
        using var binding = EffectiveMovementBinding.Ordinary(OrdinaryContactScene.DefaultTuning, source, registration,
            (_, _) => 0, null, MovementBoundary.Rectangle(-20, -20, 20, 20));
        var scope = binding.CreateOrdinaryScope(new(default, Vector3.Zero, 1));
        if (failure.StartsWith("replacement", StringComparison.Ordinal)) registration.ApplyRuntimeStatics([RuntimeStaticRegistrationTests.Wall()]);
        long before = registration.Generation;
        if (failure == "before") source.BeforeAdd = Fail;
        if (failure == "after") source.AfterAdd = Fail;
        if (failure == "replacement") source.AfterRemove = Fail;
        if (failure == "replacement-before") source.BeforeRemove = Fail;
        if (failure == "later") source.BeforeAdd = () => { if (source.Adds == 2) Fail(); };
        if (failure == "reentrant") source.AfterAdd = () => source.Backend.AddStatic(new SphereShape(1), Pose.Identity);
        if (failure == "handle") source.ReturnHandle = _ => new(-1);
        if (failure == "missing-revision") source.FreezeRevision = true;
        RuntimeStaticChange[] batch = failure == "later"
            ? [RuntimeStaticRegistrationTests.Wall(3, "a"), RuntimeStaticRegistrationTests.Wall(5, "b")]
            : [RuntimeStaticRegistrationTests.Wall()];
        Assert.Throws<InvalidOperationException>(() => registration.ApplyRuntimeStatics(batch));
        Assert.Equal(before, registration.Generation);
        MovementAvailability availability = binding.TryAcquire(scope, out var queries);
        queries?.Dispose();
        Assert.Equal(poisoned ? MovementAvailability.Unresolved : MovementAvailability.Known, availability);
        source.BeforeAdd = source.AfterAdd = source.BeforeRemove = source.AfterRemove = null;
        source.ReturnHandle = null;
        if (poisoned)
        {
            Assert.Throws<InvalidOperationException>(() => registration.ApplyRuntimeStatics([RuntimeStaticRegistrationTests.Wall(7, "next")]));
            Assert.Equal(MovementAvailability.Unresolved, binding.TryAcquire(scope, out _));
        }
        else
        {
            registration.ApplyRuntimeStatics([RuntimeStaticRegistrationTests.Wall()]);
            Assert.Equal(MovementAvailability.Known, binding.TryAcquire(scope, out queries));
            queries!.Dispose();
        }
    }

    [Fact]
    public void ReaderSeesOnlyPublishedGeometryAndProvenance()
    {
        using var source = new RuntimeStaticFailureWorld();
        var registration = Build(source);
        using var added = new ManualResetEventSlim();
        using var publish = new ManualResetEventSlim();
        source.AfterAdd = () => { added.Set(); Assert.True(publish.Wait(TimeSpan.FromSeconds(30))); };
        Exception? writerFailure = null;
        var writer = new Thread(() =>
        {
            try { registration.ApplyRuntimeStatics([RuntimeStaticRegistrationTests.Wall()]); }
            catch (Exception error) { writerFailure = error; }
        });
        writer.Start();
        try
        {
            Assert.True(added.Wait(TimeSpan.FromSeconds(30)));
            // Publication has not advanced while the backend add is deliberately held.
            long held = registration.Generation;
            Assert.Equal(registration.GeometryGeneration, held);
            RuntimeStaticGateProbe.AssertWaits(() =>
            {
                using var forbidden = source.AcquireQueryReadLease();
                Assert.Equal(MovementAvailability.Known, registration.Authenticate(forbidden));
            });
        }
        finally { publish.Set(); Assert.True(writer.Join(TimeSpan.FromSeconds(30))); }
        Assert.Null(writerFailure);
        using var read = source.AcquireQueryReadLease();
        Assert.True(source.Raycast(new(0, 1, 0), Vector3.UnitX, 10, out var hit));
        Assert.Equal(MovementAvailability.Known, registration.TryBind(read, hit.Body!.Value, 0, out var owner));
        Assert.Equal(new MovementSupportKey("world", "wall"), owner);
        Assert.Equal(registration.GeometryGeneration + 1, registration.Generation);
    }

    static OrdinaryMovementRegistration Build(RuntimeStaticFailureWorld source)
    {
        using var read = source.AcquireQueryReadLease();
        return OrdinaryMovementRegistration.CreateBuilder("world", source, new(new(-20, -4, -20), new(20, 4, 20), 0, 0)).Build(read);
    }

    static void Fail() => throw new InvalidOperationException("Injected backend failure.");

    [Fact]
    public void ConcurrentCallerValidatesAgainstPublicationAfterItsWriterIsAdmitted()
    {
        using var source = new RuntimeStaticFailureWorld();
        var registration = Build(source);
        using var added = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var removeAttempt = new ManualResetEventSlim();
        source.AfterAdd = () => { added.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(30))); };
        Exception? firstFailure = null, secondFailure = null;
        bool secondStarted = false;
        var first = new Thread(() =>
        {
            try { registration.ApplyRuntimeStatics([RuntimeStaticRegistrationTests.Wall()]); }
            catch (Exception error) { firstFailure = error; }
        })
        { IsBackground = true };
        var second = new Thread(() =>
        {
            try { registration.ApplyRuntimeStatics([RuntimeStaticChange.Remove(new("world", "wall"))]); }
            catch (Exception error) { secondFailure = error; }
        })
        { IsBackground = true };
        first.Start();
        try
        {
            Assert.True(added.Wait(TimeSpan.FromSeconds(30)));
            source.WriterAttempt = removeAttempt.Set;
            second.Start();
            secondStarted = true;
            Assert.True(removeAttempt.Wait(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            release.Set();
            Assert.True(first.Join(TimeSpan.FromSeconds(30)));
            if (secondStarted) Assert.True(second.Join(TimeSpan.FromSeconds(30)));
        }
        Assert.Null(firstFailure);
        Assert.Null(secondFailure);
        Assert.Equal(registration.GeometryGeneration + 2, registration.Generation);
        using var read = source.AcquireQueryReadLease();
        Assert.Equal(MovementAvailability.Known, registration.Authenticate(read));
        Assert.False(source.Raycast(new(0, 1, 0), Vector3.UnitX, 10, out _));
    }

    [Fact]
    public void CanonicalOrdinalOwnersRemoveEveryOldShapeBeforeAddingReplacements()
    {
        using var source = new RuntimeStaticFailureWorld();
        var registration = Build(source);
        var firstFaces = new List<float>();
        source.AfterAdd = () =>
        {
            Assert.True(source.Backend.Raycast(new(0, 1, 0), Vector3.UnitX, 10, out var hit));
            firstFaces.Add(hit.Point.X);
        };
        registration.ApplyRuntimeStatics([RuntimeStaticRegistrationTests.Wall(6, "a"), RuntimeStaticRegistrationTests.Wall(3, "Z")]);
        // Ordinal Z precedes a. The first installed shape must therefore be the nearer wall.
        Assert.Equal(new[] { 2.8f, 2.8f }, firstFaces);
        var phases = new List<string>();
        source.AfterAdd = () => phases.Add("add");
        source.AfterRemove = () => phases.Add("remove");
        registration.ApplyRuntimeStatics([RuntimeStaticRegistrationTests.Wall(7, "new"), RuntimeStaticRegistrationTests.Wall(4, "Z"),
            RuntimeStaticChange.Remove(new("world", "a"))]);
        Assert.Equal(new[] { "remove", "remove", "add", "add" }, phases);
        Assert.Equal(2, registration.Generation - registration.GeometryGeneration);
    }
}
