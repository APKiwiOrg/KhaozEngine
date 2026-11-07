using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class PlayerPublicationPolicyTests
{
    [Theory]
    [InlineData(0f, 0)]
    [InlineData(0.00390625f, 1)]
    [InlineData(0.001953125f, 0)]
    [InlineData(0.005859375f, 2)]
    [InlineData(-0.005859375f, -2)]
    [InlineData(0.0019531249f, 0)]
    [InlineData(0.0019531252f, 1)]
    [InlineData(0.3921569f, 100)]
    [InlineData(127f, 32512)]
    [InlineData(128f, 32512)]
    [InlineData(-128f, -32512)]
    [InlineData(float.NaN, 0)]
    [InlineData(float.PositiveInfinity, 32512)]
    [InlineData(float.NegativeInfinity, -32512)]
    public void HorizontalGridKeepsTheExistingWireBoundaries(float value, short encoded)
    {
        Assert.Equal(encoded, MovementStateQuantization.QuantizeHorizontalVelocity(value));
        Assert.Equal(encoded, MovementState.QuantizeHorizontalVelocity(value));
        Assert.Equal(encoded / 256f, PlayerMovementPublication.Canonicalize(new() { HorizontalVelocity = new(value, -value) }).HorizontalVelocity.X);
    }

    [Theory]
    [InlineData(1f, 0)]
    [InlineData(1.00048828125f, 0)]
    [InlineData(1.00146484375f, 2)]
    [InlineData(1.1f, 102)]
    [InlineData(0f, -1024)]
    [InlineData(-1f, -1024)]
    [InlineData(8f, 7168)]
    [InlineData(9f, 7168)]
    [InlineData(float.NaN, 0)]
    public void SpeedGridKeepsTheExistingWireBoundaries(float value, short encoded)
    {
        Assert.Equal(encoded, MovementStateQuantization.QuantizeSpeedScale(value));
        Assert.Equal(encoded, MovementState.QuantizeSpeedScale(value));
    }

    [Theory]
    [InlineData(-127f, 0f, -3.1415927f, -6.35f)]
    [InlineData(0.3921569f, 1.1f, 0.7f, 0.73f)]
    [InlineData(999f, 12f, 12345f, 0f)]
    public void PublicationIsIdempotentAndKeepsExactCarryAndStepEvents(float horizontal, float scale, float yaw, float climb)
    {
        var state = new MoveState
        {
            Position = new(1.25f, 4.5f, -8),
            HorizontalVelocity = new(horizontal, -horizontal),
            SpeedScale = scale,
            FacingYaw = yaw,
            ClimbRate = climb,
            ClimbRateEwma = 0.123f,
            VerticalVelocity = -3.123f,
            Grounded = true,
            Swimming = true,
            WaterExcursion = WaterExcursionState.Surface,
            TimeSinceGrounded = 0.031f,
            JumpBufferRemaining = 0.123f,
            StepDeltaY = 0.2f,
            CommandedVelocity = new(1.123f, 2.456f),
            LandingImpactSpeed = 7,
            SupportGranted = true
        };
        var published = PlayerMovementPublication.Canonicalize(state);
        Assert.Equal(published, PlayerMovementPublication.Canonicalize(published));
        Assert.Equal(state.Position, published.Position);
        Assert.Equal(state.VerticalVelocity, published.VerticalVelocity);
        Assert.Equal(state.Grounded, published.Grounded);
        Assert.Equal(state.Swimming, published.Swimming);
        Assert.Equal(state.WaterExcursion, published.WaterExcursion);
        Assert.Equal(state.Commitment, published.Commitment);
        Assert.Equal(state.TimeSinceGrounded, published.TimeSinceGrounded);
        Assert.Equal(state.JumpBufferRemaining, published.JumpBufferRemaining);
        Assert.Equal(state.StepDeltaY, published.StepDeltaY);
        Assert.Equal(state.CommandedVelocity, published.CommandedVelocity);
        Assert.Equal(state.LandingImpactSpeed, published.LandingImpactSpeed);
        Assert.Equal(state.SupportGranted, published.SupportGranted);
        var fromWire = OrdinaryContactHeadTests.WirePublication(published);
        ContactDropPublicationTests.AssertBodyBasis(published, fromWire);
        Assert.Equal(published.FacingYaw, fromWire.FacingYaw);
        Assert.Equal(published.ClimbRate, fromWire.ClimbRate);
        Assert.Equal(published.ClimbRateEwma, fromWire.ClimbRateEwma);
        Assert.Equal(0, PlayerMovementPublication.PrepareStep(published).StepDeltaY);
    }

    [Theory]
    [InlineData(-127f)]
    [InlineData(-0.025f)]
    [InlineData(0.025f)]
    [InlineData(0.73f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void ClimbGridKeepsReleasedCodecBytes(float value)
    {
        sbyte released = (sbyte)Math.Clamp((int)MathF.Round(value / MoveState.ClimbRateQuantum), -127, 127);
        Assert.Equal(released, MovementStateQuantization.QuantizeClimbRate(value));
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(-1.5f)]
    [InlineData(2.5000002f)]
    [InlineData(-8388607.5f)]
    [InlineData(float.Epsilon)]
    [InlineData(float.MaxValue)]
    public void PortableRoundingKeepsIeeeTiesAndSignedZero(float value)
    {
        Assert.Equal(BitConverter.SingleToInt32Bits(MathF.Round(value)),
            BitConverter.SingleToInt32Bits(MovementStateQuantization.RoundToEven(value)));
    }
}
