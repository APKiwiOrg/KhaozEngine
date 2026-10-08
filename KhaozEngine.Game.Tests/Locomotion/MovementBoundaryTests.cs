using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class MovementBoundaryTests
{
    [Theory]
    [InlineData(false, 1f)]
    [InlineData(false, -1f)]
    [InlineData(true, 1f)]
    [InlineData(true, -1f)]
    public void AStoredEndpointCannotHideAffineTravelOutsideTheBoundary(bool circle, float sign)
    {
        MovementBoundary boundary = circle ? MovementBoundary.Circle(0, 0, 1) : MovementBoundary.Rectangle(-1, -1, 1, 1);
        Vector3 start = new(sign, 0, 0), delta = new(sign * MathF.ScaleB(1, -54), 0, 0);
        Assert.Equal(start, start + delta);
        Assert.Equal(MovementAvailability.Known, boundary.ProveSegment(start, delta, start + delta, out bool admitted));
        Assert.False(admitted);
        Assert.Equal(MovementAvailability.Known, boundary.Constrain(start, delta, out var constrained));
        Assert.Equal(MovementAvailability.Known, boundary.ProveSegment(start, constrained, start + constrained, out admitted));
        Assert.True(admitted);
    }

    [Fact]
    public void AFloatRoundedThreeFourFiveApproximationIsNotAnExactBoundaryPoint()
    {
        MovementBoundary boundary = MovementBoundary.Circle(0, 0, 1);
        Assert.Equal(MovementAvailability.Known, boundary.Classify(new(0.6f, 0, 0.8f), out bool inside));
        Assert.False(inside);
        boundary = MovementBoundary.Circle(0, 0, 5);
        Assert.Equal(MovementAvailability.Known, boundary.Classify(new(3, 0, 4), out inside));
        Assert.True(inside);
        Assert.Equal(MovementAvailability.Known, boundary.Classify(new(MathF.BitIncrement(3), 0, 4), out inside));
        Assert.False(inside);
    }

    [Fact]
    public void AConvexChordBetweenTwoAdmittedCircleEndpointsIsFullyAdmitted()
    {
        var boundary = MovementBoundary.Circle(0, 0, 5);
        Assert.Equal(MovementAvailability.Known, boundary.ProveSegment(new(5, 0, 0), new(-5, 0, 5), new(0, 0, 5), out bool admitted));
        Assert.True(admitted);
    }
}

public partial class ExplicitCharacterMovementTests
{
    [Fact]
    public void ABoundaryRefusalAfterASolidCorrectionDiscardsTheWholeStep()
    {
        using var scene = new Scene(wall: true);
        var boundary = new RefuseCorrectionBoundary();
        var input = scene.State(new(0, 2, 0));
        var result = ExplicitCharacterMovement.StepTowards(input, Vector2.One, true, 0.5f,
            Tuning, Policy, scene.Lease, boundary);
        Assert.True(boundary.Corrected);
        Assert.Equal(MovementStepOutcome.EnvironmentUnresolved, result.Outcome);
        Assert.Equal(input.State.Position, result.State.State.Position);
        Assert.Equal(Vector2.Zero, result.State.State.CommandedVelocity);
    }

    sealed class RefuseCorrectionBoundary : MovementBoundary
    {
        readonly MovementBoundary inner = Rectangle(-8, -8, 8, 8);
        public bool Corrected;
        public override string SemanticIdentity => inner.SemanticIdentity;
        public override MovementAvailability Classify(Vector3 centre, out bool inside) => inner.Classify(centre, out inside);
        public override MovementAvailability Constrain(Vector3 start, Vector3 displacement, out Vector3 constrained) =>
            inner.Constrain(start, displacement, out constrained);
        public override MovementAvailability ProveSegment(Vector3 start, Vector3 displacement, Vector3 storedEnd, out bool admitted)
        {
            if (start.X > 0.1f && displacement.Z > 0)
            {
                Corrected = true;
                admitted = false;
                return MovementAvailability.Unresolved;
            }
            return inner.ProveSegment(start, displacement, storedEnd, out admitted);
        }
    }
}
