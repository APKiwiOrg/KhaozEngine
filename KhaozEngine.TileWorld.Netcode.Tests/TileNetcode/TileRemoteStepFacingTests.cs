using System;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public sealed class TileRemoteStepFacingTests
{
    const long Body = 100;
    const long Target = 101;
    static readonly TilePresenter Presenter = new(1f, 3f);

    [Theory]
    [InlineData(TileInteractionDomain.Entity)]
    [InlineData(TileInteractionDomain.AuthoredObject)]
    public void A_delayed_final_east_step_faces_east_until_the_displayed_landing(TileInteractionDomain domain)
    {
        using var scenario = new TilePresentationScenario();
        TileMoveState state = FinalStep(domain);
        scenario.Snapshot((Body, state), (Target, TileMoveState.At(new TileCoord(21, 21, 0), TileDirection.S)));
        scenario.Client.AdvancePresentation(.5f);

        AssertPose(.25f, MathF.PI / 2f);
        scenario.Client.AdvancePresentation(.25f);
        AssertPose(.5f, MathF.PI / 2f);
        scenario.Client.AdvancePresentation(.499f);
        Assert.True(scenario.Client.TryGetRemoteStepProgress(Body, out float before));
        Assert.InRange(before, .99f, .99999f);
        Assert.True(scenario.Client.TryGetRemotePose(Body, out TilePose gliding));
        Assert.Equal(MathF.PI / 2f, gliding.Yaw, 5);
        scenario.Client.AdvancePresentation(.0011f);
        AssertPose(1f, MathF.PI);

        void AssertPose(float fraction, float yaw)
        {
            Assert.True(scenario.Client.TryGetRemoteStepProgress(Body, out float progress));
            Assert.Equal(fraction, progress, 5);
            Assert.True(scenario.Client.TryGetRemotePose(Body, out TilePose pose));
            Assert.Equal(yaw, pose.Yaw, 5);
        }
    }

    [Theory]
    [InlineData(1, 0, 1.5707963f)]
    [InlineData(1, 1, 2.3561945f)]
    [InlineData(-1, 1, -2.3561945f)]
    [InlineData(-1, -1, -.7853982f)]
    public void The_presenter_uses_the_physical_step_then_the_authoritative_arrival_facing(int dx, int dz, float yaw)
    {
        TileMoveState state = TileMoveState.At(new TileCoord(20 + dx, 20 + dz, 0), TileDirection.N);
        state.StepFrom = new TileCoord(20, 20, 0);
        state.StepTotal = 4;
        state.StepTicks = 1;

        Assert.Equal(yaw, Presenter.Pose(state).Yaw, 5);
        Assert.Equal(yaw, Presenter.Pose(state, extraTicks: 2.99f).Yaw, 5);
        Assert.Equal(MathF.PI, Presenter.Pose(state, extraTicks: 3f).Yaw, 5);
        Assert.Equal(TileDirection.N, state.Facing);
    }

    [Fact]
    public void Carry_forward_landing_resolves_the_target_aim_before_the_next_snapshot()
    {
        using var scenario = new TilePresentationScenario();
        TileMoveState state = FinalStep(TileInteractionDomain.Entity);
        TileMoveState target = TileMoveState.At(new TileCoord(21, 21, 0), TileDirection.S);
        target.FootprintSize = 2;
        scenario.Snapshot((Body, state), (Target, target));
        scenario.Client.AdvancePresentation(.5f);
        Assert.True(scenario.Client.TryGetRemotePose(Body, out TilePose gliding));
        Assert.Equal(MathF.PI / 2f, gliding.Yaw, 5);

        scenario.Client.AdvancePresentation(.75f);
        Assert.True(scenario.Client.TryGetRemoteStepProgress(Body, out float fraction));
        Assert.Equal(1f, fraction);
        Assert.True(scenario.Client.TryGetRemotePose(Body, out TilePose landed));
        Assert.Equal(2.819842f, landed.Yaw, 5);
        Assert.True(scenario.Client.View.TryGetEntity(Body, out var entity));
        Assert.True(scenario.Client.World.TryGet(entity, out TileMoveState delayed));
        Assert.True(delayed.IsStepping);
        Assert.Equal(TileDirection.N, delayed.Facing);
    }

    [Fact]
    public void Standing_keeps_the_authoritative_facing()
    {
        TileMoveState state = TileMoveState.At(new TileCoord(20, 20, 0), TileDirection.W);
        Assert.Equal(-MathF.PI / 2f, Presenter.Pose(state, extraTicks: 10f).Yaw, 5);
    }

    [Fact]
    public void Local_prediction_uses_the_physical_final_step_facing()
    {
        using var scenario = new TilePresentationScenario();
        TileMoveState state = FinalStep(TileInteractionDomain.AuthoredObject);
        scenario.Client.Prediction.Reset(state);
        Assert.Equal(MathF.PI / 2f, scenario.Client.LocalPose.Yaw, 5);
        Assert.Equal(TileDirection.N, scenario.Client.Prediction.PredictedState.Facing);
    }

    static TileMoveState FinalStep(TileInteractionDomain domain)
    {
        TileMoveState state = TileMoveState.At(new TileCoord(21, 20, 0), TileDirection.N);
        state.StepFrom = new TileCoord(20, 20, 0);
        state.StepTotal = 4;
        state.StepTicks = 1;
        state.InteractTarget = Target;
        state.InteractDomain = domain;
        return state;
    }
}
