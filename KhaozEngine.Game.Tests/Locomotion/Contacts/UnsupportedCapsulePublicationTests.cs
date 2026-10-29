using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public class UnsupportedCapsulePublicationTests
{
    [Theory]
    [InlineData(10f, 0.9f)]
    [InlineData(-10f, 0.9f)]
    [InlineData(16777218f, 0.75f)]
    public void TinyUnsupportedDescentKeepsTheNearestCentreMapping(float centre, float half)
    {
        var input = new MoveState { Position = new(0, centre, 0), TimeSinceGrounded = 1 };
        var tuning = MoveTuning.Default with { Controller = MovementControllerKind.Contact, CapsuleHalfHeight = half };
        MoveState result = CharacterMovement.StepTowards(input, Vector2.Zero, false, 0.00001f,
            (_, _) => -100f, tuning, preserveSmallMagnitude: true);
        Assert.Equal(input.Position, result.Position);
        Assert.Equal(-0.00024999998277053237f, result.VerticalVelocity);
        Assert.False(result.Grounded);
        Assert.False(result.SupportGranted);
    }
}
