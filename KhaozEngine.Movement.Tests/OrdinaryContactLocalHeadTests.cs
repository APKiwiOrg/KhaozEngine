using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using LifecycleWorld = KhaozEngine.Tests.NetWorld.Fixtures.ObservedPhysicsWorld;
using static KhaozEngine.Tests.Movement.MoveToRangeTests;

namespace KhaozEngine.Tests.Movement;

public partial class OrdinaryContactLocalHeadTests
{
    const float Dt = 0.1f;
    static readonly MovementBoundary Boundary = MovementBoundary.Rectangle(-20, -20, 20, 20);

    [Fact]
    public void IncapableContextRefusesOnFirstContactUse()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var context = new GroundMoveContext((_, _) => 0, physics: world);
        Assert.False(context.SupportsContactController);
        var body = new MoveState { Position = new(0, 0.75f, 0), Grounded = true };
        Assert.Throws<NotSupportedException>(() => NpcGroundMovement.Hold(body, 0.1f,
            OrdinaryContactScene.DefaultTuning, context));
    }

    [Theory]
    [InlineData(false, 0f)]
    [InlineData(true, 0f)]
    [InlineData(false, 256f)]
    [InlineData(true, 256f)]
    public void LocalHeadsMatchOrdinaryCompositionDryWetAndPreciseInput(bool wet, float originX)
    {
        var tuning = OrdinaryContactScene.DefaultTuning with { Water = new(WaterTraversalMode.WadeOnly) };
        var water = wet ? MovementWaterField.Level("world", 0, -4) : null;
        float y = wet ? -0.2f : 0;
        var boundary = MovementBoundary.Rectangle(originX - 20, -20, originX + 20, 20);
        using var scene = new OrdinaryContactScene(tuning, (_, _) => y, origin: new(originX, 0, 0),
            extent: new(new(originX - 20, -4, -20), new(originX + 20, 4, 20), y, y),
            water: water, boundary: boundary);
        using var context = new GroundMoveContext(scene.Height, null, scene.Physics, null, null, null,
            water, scene.Registration, boundary);
        Assert.True(context.SupportsContactController);
        var local = new MoveState { Position = new(0, y + 0.75f, 0), Grounded = true };
        var body = local with { Position = local.Position + scene.Physics.Origin };
        var npc = NpcGroundMovement.Step(body, new(Vector2.UnitX, RangeMoveStatus.Following), false, Dt, tuning, context);
        MoveState reference;
        using (var lease = scene.Acquire())
        {
            var ordinary = scene.Binding.StepTowards(new(local, scene.Frame, null), Vector2.UnitX, false, Dt, lease);
            Assert.Equal(MovementStepOutcome.Advanced, ordinary.Outcome);
            reference = ordinary.State.State with { Position = ordinary.State.State.Position + scene.Physics.Origin };
        }
        Assert.Equal(reference, npc);
        if (!wet)
        {
            var dry = CharacterMovement.StepTowards(local, Vector2.UnitX, false, Dt, (_, _) => y, tuning,
                world: scene.Physics);
            Assert.Equal(reference, dry with { Position = dry.Position + scene.Physics.Origin });
        }
        // The graph is authored here. No contact profile or bake is generated.
        var space = NavSpace.Single(NavGrid.FromWalkable(32, 32, 1, originX - 16, -16, (_, _) => true));
        var planner = new ScriptPlanner((_, _) => Route(NavPathStatus.Complete, new Vector2(originX + 4, 0)));
        var ranged = new MoveToRange(planner, space, (_, _) => true);
        var target = ReachTarget.Point(new(originX + 4, body.Position.Y, 0));
        var steering = ranged.Tick(body, tuning, target, 0, false, Dt, context);
        Assert.Equal(Vector2.UnitX, steering.WorldDirection);
        Assert.Equal(reference, NpcGroundMovement.Step(body, steering, false, Dt, tuning, context));
        var direct = new DirectMoveToRange(new(20, 0.01f, 20, 0.01f));
        steering = direct.Tick(body, tuning, target, 0, false, false, Dt, context);
        Assert.Equal(Vector2.UnitX, steering.WorldDirection);
        Assert.Equal(reference, NpcGroundMovement.Step(body, steering, false, Dt, tuning, context));
        var precise = new Vector2(0.0001f, 0);
        var preciseResult = context.Step(body, precise, false, Dt, tuning);
        Assert.True(preciseResult.Position.X > body.Position.X);
        using var preciseLease = scene.Acquire();
        var preciseReference = scene.Binding.StepTowards(new(local, scene.Frame, null), precise, false, Dt, preciseLease);
        Assert.Equal(preciseReference.State.State with
        { Position = preciseReference.State.State.Position + scene.Physics.Origin }, preciseResult);
    }

    [Fact]
    public void TriggeredStairSignalMatchesOrdinaryAndDryComposition()
    {
        using var scene = new OrdinaryContactScene(install: (world, builder) =>
        {
            for (int i = 0; i < 4; i++)
            {
                Vector3 min = new(0.4f + i * 0.25f, 0, -4), max = new(4, (i + 1) * 0.15f, 4);
                var handle = world.AddStatic(new BoxShape((max - min) * 0.5f), Pose.At((max + min) * 0.5f));
                builder.Add(handle, 0, int.MaxValue, new("world", $"stair-{i}"), min, max);
            }
        });
        using var context = new GroundMoveContext(scene.Height, null, scene.Physics, null, null, null,
            null, scene.Registration, Boundary);
        var state = new MoveState { Position = new(0, 0.75f, 0), Grounded = true };
        bool triggered = false;
        for (int tick = 0; tick < 8; tick++)
        {
            var next = context.Step(state, Vector2.UnitX, false, Dt, scene.Tuning);
            var dry = CharacterMovement.StepTowards(state, Vector2.UnitX, false, Dt, scene.Height, scene.Tuning,
                world: scene.Physics);
            using var queries = scene.Acquire();
            var reference = scene.Binding.StepTowards(new(state, scene.Frame, null), Vector2.UnitX, false, Dt, queries);
            Assert.Equal(reference.State.State, next);
            Assert.Equal(dry, next);
            triggered |= next.ClimbRateEwma > 0;
            state = next;
        }
        Assert.True(triggered);
    }

    [Theory]
    [InlineData("gravity")]
    [InlineData("fall")]
    [InlineData("swim-enter")]
    public void FirstContactUseRefusesInvalidKernelBeforeStepping(string control)
    {
        using var scene = new OrdinaryContactScene();
        using var context = new GroundMoveContext(scene.Height, null, scene.Physics, null, null, null,
            null, scene.Registration, Boundary);
        var tuning = control switch
        {
            "gravity" => scene.Tuning with { Gravity = 0 },
            "fall" => scene.Tuning with { MaxFallSpeed = 0 },
            _ => scene.Tuning with { SwimEnterDepthFraction = 0, SwimExitDepthFraction = 0 }
        };
        Assert.Throws<ArgumentException>(() => NpcGroundMovement.Hold(default, Dt, tuning, context));
    }

    [Theory]
    [InlineData("registration")]
    [InlineData("boundary")]
    [InlineData("field")]
    [InlineData("sweep")]
    public void MissingEnvironmentAndCapabilitiesRefuseOnFirstContactUse(string missing)
    {
        using var physics = new LifecycleWorld { CertifiedSweep = missing != "sweep" };
        var registration = Registration(physics);
        using var context = new GroundMoveContext((_, _) => 0, null, physics, null,
            missing == "field" ? (_, _, _) => MovementMedium.Dry : null, null, null,
            missing == "registration" ? null : registration, missing == "boundary" ? null : Boundary);
        Assert.False(context.SupportsContactController);
        if (missing == "sweep")
            Assert.Throws<NotSupportedException>(() => context.ValidateTuning(OrdinaryContactScene.DefaultTuning));
        else
            Assert.Throws<ArgumentException>(() => context.ValidateTuning(OrdinaryContactScene.DefaultTuning));
    }

    [Fact]
    public void FailedViewCreationDoesNotConstructAContactContext()
    {
        using var physics = new LifecycleWorld { Selectable = false };
        var registration = Registration(physics);
        Assert.Throws<NotSupportedException>(() => new GroundMoveContext((_, _) => 0, null, physics,
            null, null, null, null, registration, Boundary));
        Assert.Equal(0, physics.ViewCreations);
        Assert.Equal(0, physics.ViewDisposals);
    }

    [Fact]
    public void TuningCacheRevalidatesChangedFootAndDisposesOnlyOwnedViews()
    {
        using var physics = new LifecycleWorld();
        var registration = Registration(physics);
        var field = new ConfigurationField(MovementWaterField.Level("world", 0, -4));
        var context = new GroundMoveContext((_, _) => 0, null, physics, null, null, null, field, registration, Boundary);
        var tuning = OrdinaryContactScene.DefaultTuning;
        context.ValidateTuning(tuning);
        context.ValidateTuning(tuning);
        Assert.Equal(1, field.IdentityReads);
        context.ValidateTuning(tuning with { FootRadiusFraction = 0.6f });
        Assert.Equal(2, field.IdentityReads);
        Assert.Throws<ArgumentOutOfRangeException>(() => context.ValidateTuning(tuning with { FootRadiusFraction = 0 }));
        context.ValidateTuning(tuning with { FootRadiusFraction = 0.6f });
        Assert.Equal(2, field.IdentityReads);
        Assert.Equal(1, physics.ViewCreations);
        context.Dispose();
        context.Dispose();
        Assert.Equal(1, physics.ViewDisposals);
        Assert.Equal(0, physics.SourceDisposals);
        using var view = physics.CreateQueryViewExcludingStatics([]);
        using var borrower = new GroundMoveContext((_, _) => 0, null, physics, null, null, view, null, registration, Boundary);
        borrower.ValidateTuning(tuning);
        borrower.Dispose();
        using var survivor = EffectiveMovementBinding.Ordinary(tuning, physics, registration, (_, _) => 0, null,
            Boundary, movementQueries: view);
        var frame = new MovementFrameDescriptor(default, Vector3.Zero, 1);
        Assert.Equal(MovementAvailability.Known, survivor.TryAcquire(survivor.CreateOrdinaryScope(frame), out var queries));
        using (queries)
            Assert.Equal(MovementStepOutcome.Advanced, survivor.StepTowards(new(new MoveState
            { Position = new(0, 0.75f, 0), Grounded = true }, frame, null), Vector2.UnitX, false, Dt, queries!).Outcome);
        Assert.Equal(1, physics.ViewDisposals);
    }

    internal static OrdinaryMovementRegistration Registration(IPhysicsWorld world)
    {
        using var read = ((IPhysicsQueryLeaseSource)world).AcquireQueryReadLease();
        return OrdinaryMovementRegistration.CreateBuilder("world", world,
            new(new(-20, -4, -20), new(20, 4, 20), 0, 0)).Build(read);
    }

    sealed class ConfigurationField(IMovementWaterField inner) : IMovementWaterField
    {
        public int IdentityReads;
        public string SemanticIdentity { get { IdentityReads++; return inner.SemanticIdentity; } }
        public long Generation => inner.Generation;
        public MovementWaterExtent Extent => inner.Extent;
        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 origin) => inner.SampleCentreWater(body, origin);
        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 origin,
            in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts) =>
            inner.TraceWater(query, origin, identity, spans, contacts);
    }
}
