using System;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class GroundMoveQuerySelectionTests
{
    private static readonly MoveTuning Tuning = MoveTuning.Default with
    {
        WalkSpeed = 4f, RunSpeed = 8f, CapsuleRadius = 0.25f, CapsuleHalfHeight = 0.75f,
    };
    private static readonly Vector3 RebasedOrigin = new(100f, 20f, -80f);

    [Fact]
    public void ExistingFiveParameterConstructorRemainsAvailable()
    {
        Type[] signature =
        [
            typeof(Func<float, float, float>), typeof(Func<float, float, Vector3>),
            typeof(IPhysicsWorld), typeof(Func<float, float, Vector2>),
            typeof(Func<float, float, float, MovementMedium>),
        ];
        ConstructorInfo? constructor = typeof(GroundMoveContext).GetConstructor(signature);
        Assert.NotNull(constructor);
        ParameterInfo[] parameters = constructor.GetParameters();
        Assert.False(parameters[0].IsOptional);
        for (int i = 1; i < parameters.Length; i++)
        {
            Assert.True(parameters[i].IsOptional);
            Assert.Null(parameters[i].DefaultValue);
        }
        ConstructorInfo? selected = typeof(GroundMoveContext).GetConstructor(
            [.. signature, typeof(IPhysicsWorldQueryView)]);
        Assert.NotNull(selected);
        foreach (ParameterInfo parameter in selected.GetParameters()) Assert.False(parameter.IsOptional);

        var old = new GroundMoveContext((_, _) => 0f);
        var explicitNull = new GroundMoveContext((_, _) => 0f, null, null, null, null, null);
        MoveState body = Standing(new Vector3(0f, 0.75f, 0f));
        MoveState result = old.Step(body, Vector2.UnitX, false, 0.125f, Tuning);

        Assert.Null(old.Physics);
        Assert.Null(old.MovementQueries);
        Assert.Null(explicitNull.MovementQueries);
        Assert.Equal(new Vector3(0.5f, 0.75f, 0f), result.Position);
        Assert.True(result.Grounded);
        Assert.Equal(result, explicitNull.Step(body, Vector2.UnitX, false, 0.125f, Tuning));
    }

    [Fact]
    public void NullSelectionRetainsFullPhysicsMovement()
    {
        using var world = new BepuPhysicsWorld();
        AddWall(world);
        var old = new GroundMoveContext((_, _) => 0f, physics: world);
        var explicitNull = new GroundMoveContext((_, _) => 0f, null, world, null, null, null);
        MoveState body = Standing(new Vector3(-0.5f, 0.75f, 0f));

        MoveState result = old.Step(body, Vector2.UnitX, false, 0.25f, Tuning);

        Assert.Same(world, old.Physics);
        Assert.Null(old.MovementQueries);
        Assert.Null(explicitNull.MovementQueries);
        Assert.InRange(result.Position.X, -0.5f, -0.2f);
        Assert.Equal(result, explicitNull.Step(body, Vector2.UnitX, false, 0.25f, Tuning));
    }

    [Fact]
    public void SelectedMovementUsesSameSourceWhilePhysicsStaysComplete()
    {
        using var world = new BepuPhysicsWorld();
        StaticHandle wall = AddWall(world);
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics([wall]);
        var context = new GroundMoveContext((_, _) => 0f, null, world, null, null, view);
        var complete = new GroundMoveContext((_, _) => 0f, physics: world);
        MoveState body = Standing(new Vector3(-0.5f, 0.75f, 0f));

        Assert.Same(world, context.Physics);
        Assert.Same(view, context.MovementQueries);
        Assert.Same(context.Physics, view.SourceWorld);
        AssertCompleteWall(context.Physics!, wall);
        Assert.False(view.Raycast(new Vector3(-1f, 1f, 0f), Vector3.UnitX, 2f, out _));
        MoveState selected = context.Step(body, Vector2.UnitX, false, 0.25f, Tuning);
        MoveState unselected = complete.Step(body, Vector2.UnitX, false, 0.25f, Tuning);

        AssertPosition(new Vector3(0.5f, 0.75f, 0f), selected.Position);
        Assert.True(selected.Grounded);
        Assert.InRange(unselected.Position.X, -0.5f, -0.2f);
        AssertCompleteWall(context.Physics!, wall);
    }

    [Fact]
    public void ConstructorRejectsDifferentSourceEvenWithEqualOrigin()
    {
        using var complete = new BepuPhysicsWorld();
        using var other = new BepuPhysicsWorld();
        complete.Rebase(RebasedOrigin);
        other.Rebase(RebasedOrigin);
        using IPhysicsWorldQueryView view = other.CreateQueryViewExcludingStatics([]);
        Assert.Equal(complete.Origin, view.Origin);

        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            new GroundMoveContext((_, _) => 0f, null, complete, null, null, view));

        Assert.Equal("movementQueries", error.ParamName);
    }

    [Fact]
    public void ConstructorRejectsSelectedViewWithoutPhysics()
    {
        using var world = new BepuPhysicsWorld();
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics([]);

        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            new GroundMoveContext((_, _) => 0f, null, null, null, null, view));

        Assert.Equal("movementQueries", error.ParamName);
    }

    [Fact]
    public void LogicalDecoratorSourceIsAccepted()
    {
        using var backend = new BepuPhysicsWorld();
        var decorator = new LogicalDecorator(backend, forwardBackendIdentity: false);
        StaticHandle wall = AddWall(decorator);
        using IPhysicsWorldQueryView view = decorator.CreateQueryViewExcludingStatics([wall]);
        var context = new GroundMoveContext((_, _) => 0f, null, decorator, null, null, view);

        MoveState result = context.Step(Standing(new Vector3(-0.5f, 0.75f, 0f)),
            Vector2.UnitX, false, 0.25f, Tuning);

        Assert.Same(decorator, view.SourceWorld);
        Assert.Same(decorator, context.Physics);
        AssertPosition(new Vector3(0.5f, 0.75f, 0f), result.Position);
        Assert.True(result.Grounded);
        AssertCompleteWall(decorator, wall);
    }

    [Fact]
    public void ForwardedBackendIdentityThroughDecoratorIsRejected()
    {
        using var backend = new BepuPhysicsWorld();
        var decorator = new LogicalDecorator(backend, forwardBackendIdentity: true);
        using IPhysicsWorldQueryView view = decorator.CreateQueryViewExcludingStatics([]);
        Assert.Same(backend, view.SourceWorld);
        Assert.Equal(decorator.Origin, view.Origin);

        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            new GroundMoveContext((_, _) => 0f, null, decorator, null, null, view));

        Assert.Equal("movementQueries", error.ParamName);
    }

    [Fact]
    public void SelectedOriginTracksSourceRebaseBetweenSteps()
    {
        using var world = new BepuPhysicsWorld();
        using var controlWorld = new BepuPhysicsWorld();
        var platform = new BoxShape(new Vector3(4f, 0.25f, 4f));
        Pose platformPose = Pose.At(new Vector3(104f, 3.75f, -76f));
        StaticHandle floor = world.AddStatic(platform, platformPose);
        controlWorld.AddStatic(platform, platformPose);
        StaticHandle wall = world.AddStatic(new BoxShape(new Vector3(0.05f, 2f, 2f)),
            Pose.At(new Vector3(103.75f, 5f, -76f)));
        world.Rebase(RebasedOrigin);
        controlWorld.Rebase(RebasedOrigin);
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics([wall]);
        Func<float, float, float> ground = (_, _) => 1f;
        var context = new GroundMoveContext(ground, null, world, null, null, view);
        var control = new GroundMoveContext(ground, physics: controlWorld);
        MoveState body = Standing(new Vector3(103f, 4.75f, -76f));

        Assert.Same(world, context.Physics);
        AssertRebasedSelection(world, view, wall, floor);
        MoveState first = context.Step(body, Vector2.UnitX, false, 0.125f, Tuning);
        MoveState firstControl = control.Step(body, Vector2.UnitX, false, 0.125f, Tuning);
        AssertPosition(firstControl.Position, first.Position);
        Vector3 nextOrigin = new(96f, -12f, -72f);
        world.Rebase(nextOrigin);
        controlWorld.Rebase(nextOrigin);
        AssertRebasedSelection(world, view, wall, floor);
        MoveState second = context.Step(first, Vector2.UnitX, false, 0.125f, Tuning);
        MoveState secondControl = control.Step(firstControl, Vector2.UnitX, false, 0.125f, Tuning);

        Assert.Equal(world.Origin, view.Origin);
        AssertPosition(secondControl.Position, second.Position);
        Assert.True(second.Position.X > first.Position.X);
        Assert.InRange(first.Position.Y, 4.74f, 4.78f);
        Assert.InRange(second.Position.Y, 4.74f, 4.78f);
        Assert.Equal(-76f, first.Position.Z);
        Assert.Equal(-76f, second.Position.Z);
        Assert.True(first.Grounded);
        Assert.True(second.Grounded);
        Assert.True(firstControl.Grounded);
        Assert.True(secondControl.Grounded);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void SelectedOriginMutationDuringProviderIsRejected(int provider, bool rebaseSource)
    {
        using var world = new BepuPhysicsWorld();
        using var view = new SelectedQueryView(world, world.CreateQueryViewExcludingStatics([]));
        bool mutate = true, mutated = false;
        GroundMoveContext context = WithMutation(world, view, provider, () =>
        {
            if (!mutate) return;
            mutated = true;
            if (rebaseSource) world.Rebase(RebasedOrigin);
            else view.OriginOverride = Vector3.UnitX;
        });
        MoveState body = Standing(new Vector3(0f, 0.75f, 0f));

        Assert.Throws<InvalidOperationException>(() => context.Step(body, Vector2.UnitX, false, 0.125f, Tuning));
        Assert.True(mutated);
        mutate = false;
        view.OriginOverride = null;
        AssertRecovered(context, body);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SelectedSourceMutationDuringProviderIsRejected(int provider)
    {
        using var world = new BepuPhysicsWorld();
        using var other = new BepuPhysicsWorld();
        using var view = new SelectedQueryView(world, world.CreateQueryViewExcludingStatics([]));
        bool mutate = true, mutated = false;
        GroundMoveContext context = WithMutation(world, view, provider, () =>
        {
            if (!mutate) return;
            mutated = true;
            view.SourceWorld = other;
        });
        MoveState body = Standing(new Vector3(0f, 0.75f, 0f));

        Assert.Throws<InvalidOperationException>(() => context.Step(body, Vector2.UnitX, false, 0.125f, Tuning));
        Assert.True(mutated);
        mutate = false;
        view.SourceWorld = world;
        AssertRecovered(context, body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedMismatchAtStepEntryIsRejectedBeforeSampling(bool changeSource)
    {
        using var world = new BepuPhysicsWorld();
        using var other = new BepuPhysicsWorld();
        using var view = new SelectedQueryView(world, world.CreateQueryViewExcludingStatics([]));
        bool sampled = false;
        var context = new GroundMoveContext((_, _) => { sampled = true; return 0f; },
            null, world, null, null, view);
        if (changeSource) view.SourceWorld = other;
        else view.OriginOverride = Vector3.UnitX;
        MoveState body = Standing(new Vector3(0f, 0.75f, 0f));

        Assert.Throws<InvalidOperationException>(() => context.Step(body, Vector2.UnitX, false, 0.125f, Tuning));
        Assert.False(sampled);
        view.SourceWorld = world;
        view.OriginOverride = null;
        AssertRecovered(context, body);
    }

    private static GroundMoveContext WithMutation(IPhysicsWorld world, IPhysicsWorldQueryView view,
        int provider, Action mutate) => new(
        (_, _) => { if (provider == 0) mutate(); return 0f; },
        (_, _) => { if (provider == 1) mutate(); return Vector3.UnitY; },
        world,
        (x, z) => { if (provider == 2) mutate(); return new Vector2(x, z); },
        (_, _, _) => { if (provider == 3) mutate(); return MovementMedium.Dry; },
        view);

    private static void AssertRecovered(GroundMoveContext context, MoveState body)
    {
        MoveState recovered = context.Step(body, Vector2.UnitX, false, 0.125f, Tuning);
        Assert.InRange(MathF.Abs(recovered.Position.X - 0.5f), 0f, 0.00003f);
        Assert.InRange(MathF.Abs(recovered.Position.Y - 0.75f), 0f, 0.00003f);
        Assert.InRange(MathF.Abs(recovered.Position.Z), 0f, 0.00003f);
        Assert.True(recovered.Grounded);
    }

    private static void AssertPosition(Vector3 expected, Vector3 actual)
    {
        Assert.InRange(MathF.Abs(actual.X - expected.X), 0f, 0.00003f);
        Assert.InRange(MathF.Abs(actual.Y - expected.Y), 0f, 0.00003f);
        Assert.InRange(MathF.Abs(actual.Z - expected.Z), 0f, 0.00003f);
    }

    private static void AssertRebasedSelection(IPhysicsWorld world, IPhysicsWorldQueryView view,
        StaticHandle wall, StaticHandle floor)
    {
        Assert.Same(world, view.SourceWorld);
        Assert.Equal(world.Origin, view.Origin);
        Vector3 wallRay = new Vector3(103f, 5f, -76f) - world.Origin;
        Assert.True(world.Raycast(wallRay, Vector3.UnitX, 2f, out RayHit completeWall));
        Assert.Equal(wall, completeWall.Body);
        Assert.False(view.Raycast(wallRay, Vector3.UnitX, 2f, out _));
        Vector3 floorRay = new Vector3(104f, 10f, -76f) - world.Origin;
        Assert.True(world.Raycast(floorRay, -Vector3.UnitY, 10f, out RayHit capturedFloor));
        Assert.Equal(floor, capturedFloor.Body);
        Assert.InRange(MathF.Abs(capturedFloor.Point.Y + world.Origin.Y - 4f), 0f, 0.00003f);
    }

    private static MoveState Standing(Vector3 position) => new() { Position = position, Grounded = true, SpeedScale = 1f };

    private static StaticHandle AddWall(IPhysicsWorld world) => world.AddStatic(
        new BoxShape(new Vector3(0.05f, 1f, 2f)), Pose.At(new Vector3(0f, 1f, 0f)));

    private static void AssertCompleteWall(IPhysicsWorld world, StaticHandle wall)
    {
        Assert.True(world.Raycast(new Vector3(-1f, 1f, 0f), Vector3.UnitX, 2f, out RayHit hit));
        Assert.Equal(wall, hit.Body);
    }

    private class PhysicsDecorator(IPhysicsWorld inner) : IPhysicsWorld
    {
        public virtual Vector3 Origin => inner.Origin;
        public bool CanRebase => inner.CanRebase;
        public StaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null)
            => inner.AddStatic(shape, pose, material);
        public void RemoveStatic(StaticHandle handle) => inner.RemoveStatic(handle);
        public DynamicBodyHandle AddDynamic(PhysicsShape shape, Pose pose, DynamicBodyDescription body,
            PhysicsMaterial? material = null) => inner.AddDynamic(shape, pose, body, material);
        public void RemoveDynamic(DynamicBodyHandle handle) => inner.RemoveDynamic(handle);
        public Pose GetDynamicPose(DynamicBodyHandle handle) => inner.GetDynamicPose(handle);
        public void GetDynamicVelocity(DynamicBodyHandle handle, out Vector3 linear, out Vector3 angular)
            => inner.GetDynamicVelocity(handle, out linear, out angular);
        public void SetDynamicVelocity(DynamicBodyHandle handle, Vector3 linear, Vector3 angular)
            => inner.SetDynamicVelocity(handle, linear, angular);
        public bool IsAwake(DynamicBodyHandle handle) => inner.IsAwake(handle);
        public ConstraintHandle AddConstraint(in ConstraintDescription description) => inner.AddConstraint(description);
        public void RemoveConstraint(ConstraintHandle handle) => inner.RemoveConstraint(handle);
        public void SetConstraintTarget(ConstraintHandle handle, float target) => inner.SetConstraintTarget(handle, target);
        public void Step(float dt) => inner.Step(dt);
        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit,
            QueryFilter filter = default) => inner.Raycast(origin, direction, maxDistance, out hit, filter);
        public bool SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction, float maxDistance,
            out SweepHit hit, QueryFilter filter = default)
            => inner.SweepCapsule(capsule, pose, direction, maxDistance, out hit, filter);
        public bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv)
            => inner.ComputePenetration(capsule, pose, out mtv);
        public virtual IPhysicsWorldQueryView CreateQueryViewExcludingStatics(ReadOnlySpan<StaticHandle> handles)
            => inner.CreateQueryViewExcludingStatics(handles);
        public void Rebase(Vector3 origin) => inner.Rebase(origin);
        public void Dispose() => inner.Dispose();
    }

    private sealed class LogicalDecorator(IPhysicsWorld inner, bool forwardBackendIdentity) : PhysicsDecorator(inner)
    {
        public override IPhysicsWorldQueryView CreateQueryViewExcludingStatics(ReadOnlySpan<StaticHandle> handles)
        {
            IPhysicsWorldQueryView delegated = base.CreateQueryViewExcludingStatics(handles);
            return forwardBackendIdentity ? delegated : new SelectedQueryView(this, delegated);
        }
    }

    private sealed class SelectedQueryView(IPhysicsWorld source, IPhysicsWorldQueryView inner)
        : PhysicsDecorator(inner), IPhysicsWorldQueryView
    {
        public IPhysicsWorld SourceWorld { get; set; } = source;
        public Vector3? OriginOverride { get; set; }
        public override Vector3 Origin => OriginOverride ?? base.Origin;
    }
}
