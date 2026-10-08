using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Fixtures.AnalyticMovementEnvironment;

namespace KhaozEngine.Tests.Locomotion;

public partial class ExplicitCharacterMovementTests
{
    [Fact]
    public void AThirtyHertzColumnProducerApproachReachesAWithinStepHeightBank()
    {
        using var scene = new Scene(floor: true, bankHeight: 0.25f);
        var state = scene.State(new(0, 0.752f, 0), grounded: true);
        for (int tick = 0; tick < 30; tick++)
        {
            var result = ExplicitCharacterMovement.StepTowards(state, Vector2.UnitX, false, 1f / 30,
                Tuning, Policy, scene.Lease);
            Assert.True(result.Outcome is MovementStepOutcome.Advanced or MovementStepOutcome.Blocked);
            state = result.State;
        }
        Assert.True(state.State.Position.X > 1.1f, $"Stopped at {state.State.Position}.");
        Assert.True(state.State.Grounded);
        Assert.InRange(state.State.Position.Y, 1.0009f, 1.0011f);
    }

    [Fact]
    public void AnOffColumnPointAloneIsNotAnAxisSupportPlaneCertificate()
    {
        using var scene = new Scene(floor: true, bankHeight: 0.25f);
        scene.Environment.Acquisition.OnSupport = (in MovementSupportRequest request, Span<MovementSupportCandidate> candidates) =>
        {
            Assert.True(1 - request.Body.Centre.X <= request.Body.Radius);
            candidates[0] = new(new("world", "bank"), Space("room"), new(1, 0.25f, 0), Vector3.UnitY, null);
            return new(MovementAvailability.Known, 1, 1, Identity);
        };
        var body = new MovementBodyQuery(new(0.9f, 0.751f, 0), 0.25f, 0.75f, Space("room"), null);
        var status = MovementSupportResolver.Select(new(body, 0.4f, 0, Tuning.MaxSlopeRadians,
            new(Space("room"), body.Feet)), scene.Lease, out var placement);
        Assert.Equal(MovementAvailability.Invalid, status);
        Assert.Null(placement);
    }
}
