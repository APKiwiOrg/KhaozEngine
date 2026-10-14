using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.Fixtures.ContactNavigationFixture;

namespace KhaozEngine.Tests.Movement;

public class ContactNavigationBasisTests
{
    [Fact]
    public void UnknownAnchorThrowsWithItsCellInsteadOfPublishingAPartialGraph()
    {
        bool unresolved = false;
        using var scene = new OrdinaryContactScene(Tuning, (_, _) => unresolved ? float.NaN : 0,
            install: (world, builder) => Box(world, builder, "floor", new(-8, -0.2f, -8), new(8, 0, 8)));
        using var context = Context(scene);
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        unresolved = true;
        var failure = Assert.Throws<InvalidOperationException>(() => capture.BuildProfile(Tuning, default));
        Assert.Contains("cell (0, 0)", failure.Message);
        Assert.Contains("Unresolved", failure.Message);
    }

    [Fact]
    public void ProfileBindingsKeepTheContextsCachedTuningAndBorrowedViewAlive()
    {
        var field = new ObservedWater();
        using var scene = new OrdinaryContactScene(Tuning, water: field,
            install: (world, builder) => Box(world, builder, "floor", new(-8, -0.2f, -8), new(8, 0, 8)));
        using var view = scene.Physics.CreateQueryViewExcludingStatics([]);
        using var context = new GroundMoveContext(scene.Height, null, scene.Physics, null, null, view,
            field, scene.Registration, MovementBoundary.Rectangle(-20, -20, 20, 20));
        MoveTuning cached = Tuning with { WalkSpeed = 0, RunSpeed = 0 };
        context.ValidateTuning(cached);
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        GroundNavigation nav = capture.BuildProfile(Tuning, default);
        Assert.True(nav.AllowsSegment(new(-0.075f, 0, 0.125f), new(0.175f, 0, 0.125f)));
        int before = field.IdentityReads;
        context.ValidateTuning(cached);
        Assert.Equal(before, field.IdentityReads);
        context.Dispose();
        using var survivor = EffectiveMovementBinding.Ordinary(Tuning, scene.Physics, scene.Registration,
            scene.Height, null, MovementBoundary.Rectangle(-20, -20, 20, 20), field, movementQueries: view);
        Assert.Equal(MovementAvailability.Known, survivor.TryAcquire(survivor.CreateOrdinaryScope(scene.Frame), out var queries));
        using (queries)
            Assert.Equal(MovementStepOutcome.Advanced, survivor.StepTowards(scene.State(new(0, 0.75f, 0)),
                Vector2.UnitX, false, Options.EdgeProbeSeconds, queries!).Outcome);
    }

    sealed class ObservedWater : IMovementWaterField
    {
        readonly IMovementWaterField _inner = MovementWaterField.Level("world", -3, -4);
        internal int IdentityReads { get; private set; }
        public string SemanticIdentity { get { IdentityReads++; return _inner.SemanticIdentity; } }
        public long Generation => _inner.Generation;
        public MovementWaterExtent Extent => _inner.Extent;
        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 physicsOrigin) =>
            _inner.SampleCentreWater(body, physicsOrigin);
        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
            in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts) =>
            _inner.TraceWater(query, physicsOrigin, identity, spans, contacts);
    }
}
