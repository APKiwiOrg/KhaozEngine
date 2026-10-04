using System;
using System.Numerics;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

// Opt-in inter-tick interpolation of the predicted heading (PredictionSettings.InterpolateYaw).
public class ClientPredictionYawTests
{
    private const float Tick = 1f / 30f;

    private readonly record struct YawState(Vector2 Position, float Heading, uint Epoch) : IPredictedState<YawState>
    {
        bool IPredictedState<YawState>.HasYaw => true;
        float IPredictedState<YawState>.Yaw => Heading;
        uint IPredictedState<YawState>.TeleportEpoch => Epoch;
        public YawState WithPosition(Vector2 position) => this with { Position = position };
        YawState IPredictedState<YawState>.WithRenderState(Vector2 position, float vertical, float yaw)
            => this with { Position = position, Heading = yaw };
    }

    private readonly record struct YawCmd(Vector2 Velocity, float Heading);

    private sealed class YawSimulator : ITickSimulator<YawState, YawCmd>
    {
        public YawState Step(in YawState state, in YawCmd command, float dt)
            => state with { Position = state.Position + command.Velocity * dt, Heading = command.Heading };
    }

    private static ClientPrediction<YawState, YawCmd> New(float heading, bool interpolate = true)
    {
        var settings = PredictionSettings.Default with { TickSeconds = Tick, InterpolateYaw = interpolate };
        var p = new ClientPrediction<YawState, YawCmd>(new YawSimulator(), settings);
        p.Reset(new YawState(Vector2.Zero, heading, 0));
        return p;
    }

    private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);

    private static float Wrap(float radians) => radians - MathF.Tau * MathF.Floor((radians + MathF.PI) / MathF.Tau);

    [Fact]
    public void YawInterpolationIsOffByDefault()
    {
        Assert.False(PredictionSettings.Default.InterpolateYaw);
        var off = new ClientPrediction<YawState, YawCmd>(new YawSimulator(),
            PredictionSettings.Default with { TickSeconds = Tick });
        off.Reset(new YawState(Vector2.Zero, 0f, 0));
        ClientPrediction<YawState, YawCmd> on = New(0f);
        var command = new YawCmd(new Vector2(3f, -1f), 1f);
        off.Predict(command);
        on.Predict(command);
        off.AdvancePresentation(Tick / 2f);
        on.AdvancePresentation(Tick / 2f);

        YawState rendered = off.RenderedState;
        Assert.Equal(Bits(1f), Bits(rendered.Heading));
        Assert.Equal(Bits(on.RenderedState.Position.X), Bits(rendered.Position.X));
        Assert.Equal(Bits(on.RenderedState.Position.Y), Bits(rendered.Position.Y));
        Assert.NotEqual(Bits(1f), Bits(on.RenderedState.Heading));
    }

    [Fact]
    public void HalfTickRendersHalfTheTurn()
    {
        ClientPrediction<YawState, YawCmd> p = New(0f);
        p.Predict(new YawCmd(Vector2.Zero, 0.6f));
        p.AdvancePresentation(Tick / 2f);

        Assert.Equal(0.3f, p.RenderedState.Heading, 1e-6f);
    }

    [Fact]
    public void SeamTurnsTheShortWayRound()
    {
        ClientPrediction<YawState, YawCmd> p = New(3f);
        p.Predict(new YawCmd(Vector2.Zero, -3f));
        p.AdvancePresentation(Tick / 2f);

        // The short way from 3.0 to -3.0 is 2 pi - 6 = 0.2832 rad through the seam, so half a tick turns 0.1416.
        Assert.Equal(0.14159f, Wrap(p.RenderedState.Heading - 3f), 1e-5f);
    }

    [Fact]
    public void RenderedYawStaysCanonicalAndLandsExactly()
    {
        ClientPrediction<YawState, YawCmd> p = New(3f);
        p.Predict(new YawCmd(Vector2.Zero, -3f));
        for (int quarter = 1; quarter <= 3; quarter++)
        {
            p.AdvancePresentation(Tick / 4f);
            float heading = p.RenderedState.Heading;
            Assert.True(heading >= -MathF.PI && heading < MathF.PI, $"quarter {quarter} rendered {heading}");
        }

        p.AdvancePresentation(Tick / 4f);
        Assert.Equal(Bits(-3f), Bits(p.RenderedState.Heading));
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("reseed")]
    [InlineData("hardsnap")]
    [InlineData("epoch")]
    public void CollapseSitesSnapThePreviousYaw(string site)
    {
        ClientPrediction<YawState, YawCmd> p = New(0f);
        // A matching reconcile first, so the seed is no longer fresh and an epoch advance counts.
        p.Reconcile(0, new YawState(Vector2.Zero, 0f, 0), -1);
        int seq = p.Predict(new YawCmd(Vector2.Zero, 1f));
        p.AdvancePresentation(Tick / 2f);
        Assert.Equal(0.5f, p.RenderedState.Heading, 1e-6f);

        switch (site)
        {
            case "reset":
                p.Reset(new YawState(Vector2.Zero, 2f, 0));
                break;
            case "reseed":
                p.Reseed(new YawState(Vector2.Zero, 2f, 0));
                p.Reconcile(1, new YawState(Vector2.Zero, 2f, 0), seq);
                break;
            case "hardsnap":
                Assert.True(p.Reconcile(1, new YawState(new Vector2(1000f, 0f), 2f, 0), seq).HardSnapApplied);
                break;
            case "epoch":
                ReconciliationResult result = p.Reconcile(1, new YawState(Vector2.Zero, 2f, 1), seq);
                Assert.True(result.HardSnapApplied && result.Teleported);
                break;
        }

        Assert.Equal(Bits(2f), Bits(p.RenderedState.Heading));
        p.AdvancePresentation(Tick / 2f);
        Assert.Equal(Bits(2f), Bits(p.RenderedState.Heading));
    }

    [Fact]
    public void NonSnapReconcileKeepsThePhase()
    {
        ClientPrediction<YawState, YawCmd> p = New(0f);
        int seq = p.Predict(new YawCmd(Vector2.Zero, 0.6f));
        p.AdvancePresentation(Tick / 4f);
        Assert.Equal(0.15f, p.RenderedState.Heading, 1e-6f);

        Assert.False(p.Reconcile(1, new YawState(Vector2.Zero, 0.6f, 0), seq).HardSnapApplied);
        Assert.Equal(0.15f, p.RenderedState.Heading, 1e-6f);

        // Only the target moves: a quarter of the way from the held previous 0 to the rebased 0.8.
        Assert.False(p.Reconcile(2, new YawState(Vector2.Zero, 0.8f, 0), seq).HardSnapApplied);
        Assert.Equal(0.2f, p.RenderedState.Heading, 1e-6f);
    }
}
