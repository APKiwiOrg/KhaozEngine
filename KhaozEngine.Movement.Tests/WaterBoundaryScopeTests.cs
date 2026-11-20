using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.Fixtures.ContactNavigationFixture;

namespace KhaozEngine.Tests.Movement;

/// <summary>Navigation bake proofs answer every water boundary question with the full coverage solve, while runtime
/// movement may take the capture certificate. A bake that refuses today must still refuse.</summary>
public class WaterBoundaryScopeTests
{
    static MoveTuning Swimmer => Tuning with { Water = new(WaterTraversalMode.SurfaceSwimmer) };

    [Fact]
    public void ABakeNeverConsultsTheCertificateAndARuntimeStepDoes()
    {
        using var world = SwimTraversalProbeTests.PoolWorld();
        var field = new ConsultedWaterField(MovementWaterField.Level("world", 0, -4));
        using var context = Context(world, ContactNavigationWaterTests.PoolHeight, field);
        using (var capture = PhysicsNavBake.Capture(context, ContactNavigationWaterTests.PoolOptions, _ => 0))
            _ = capture.BuildProfile(Swimmer, default);
        Assert.True(field.Traced > 0);
        Assert.Equal(0, field.Consulted);

        context.ValidateTuning(Swimmer);
        EffectiveMovementBinding runtime = context.ResolvedBinding;
        MovementFrameDescriptor frame = context.NavigationFrame;
        Assert.Equal(MovementAvailability.Known, runtime.TryAcquire(runtime.CreateOrdinaryScope(frame), out var queries));
        using (queries)
        {
            var state = new FramedMovementState(new MoveState
            {
                Position = new(-2.5f, Swimmer.CapsuleHalfHeight, 0.25f),
                Grounded = true,
                SpeedScale = 1,
            }, frame, null);
            _ = runtime.StepTowards(state, Vector2.UnitX, false, 1f / 30, queries!);
        }
        Assert.True(field.Consulted > 0);
    }

    [Fact]
    public void BakeBindingsAndTheirProofBindingsLeaseTheFullSolve()
    {
        using var world = SwimTraversalProbeTests.PoolWorld();
        using var context = Context(world, ContactNavigationWaterTests.PoolHeight, MovementWaterField.Level("world", 0, -4));
        MovementFrameDescriptor frame = context.NavigationFrame;
        using var bake = context.CreateNavigationBakeBinding(Swimmer);
        using var proof = bake.CreateProofBinding();
        using var fall = bake.CreateFallProofBinding();
        using var navigation = context.CreateNavigationBinding(Swimmer);
        Assert.Equal(MovementWaterBoundaryScope.NavigationBake, Scope(bake, frame));
        Assert.Equal(MovementWaterBoundaryScope.NavigationBake, Scope(proof, frame));
        Assert.Equal(MovementWaterBoundaryScope.NavigationBake, Scope(fall, frame));
        Assert.Equal(MovementWaterBoundaryScope.Runtime, Scope(navigation, frame));
        context.ValidateTuning(Swimmer);
        Assert.Equal(MovementWaterBoundaryScope.Runtime, Scope(context.ResolvedBinding, frame));
    }

    static MovementWaterBoundaryScope Scope(EffectiveMovementBinding binding, MovementFrameDescriptor frame)
    {
        Assert.Equal(MovementAvailability.Known, binding.TryAcquire(binding.CreateOrdinaryScope(frame), out var lease));
        using (lease) return lease!.WaterBoundaryScope;
    }

    // Records whether the boundary reach certificate was consulted. It always declines, so every answer the
    // movement sees is the full solve's.
    sealed class ConsultedWaterField(IMovementWaterField inner) : IMovementWaterBoundaryReachProof
    {
        internal int Consulted { get; private set; }
        internal int Traced { get; private set; }
        public string SemanticIdentity => inner.SemanticIdentity;
        public long Generation => inner.Generation;
        public MovementWaterExtent Extent => inner.Extent;

        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 physicsOrigin) =>
            inner.SampleCentreWater(body, physicsOrigin);

        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
            in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
        {
            Traced++;
            return inner.TraceWater(query, physicsOrigin, identity, spans, contacts);
        }

        public MovementWaterReach CertifyBoundaryReach(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
            in MovementQueryIdentity identity)
        {
            Consulted++;
            return MovementWaterReach.Declined;
        }
    }
}
