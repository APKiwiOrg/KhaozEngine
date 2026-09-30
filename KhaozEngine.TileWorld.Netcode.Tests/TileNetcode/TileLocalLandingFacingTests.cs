using System;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public sealed class TileLocalLandingFacingTests
{
    const long Target = 101;
    const float Tick = .25f;
    static readonly TilePresenter Presenter = new(1f, 3f);

    [Theory]
    [InlineData(1, 0, 1.5707963f)]
    [InlineData(1, 1, 2.3561945f)]
    [InlineData(-1, 1, -2.3561945f)]
    [InlineData(-1, -1, -.7853982f)]
    public void The_local_presenter_keeps_step_facing_after_the_final_prediction_normalizes_the_step(
        int dx, int dz, float yaw)
    {
        var simulator = new TileMoveSimulator(TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()),
            new TileStepTicks(4, 2));
        var prediction = new ClientPrediction<TileMoveState, TileCommand>(simulator,
            new PredictionSettings(Tick, 64, 4f, 8f, .01f));
        prediction.Reset(FinalStep(dx, dz));
        prediction.Predict(TileCommand.None);
        prediction.AdvancePresentation(Tick);
        prediction.Predict(TileCommand.None);
        TileMoveState landed = prediction.PredictedState;
        Assert.False(landed.IsStepping);
        Assert.Equal(TileDirection.N, landed.Facing);

        AssertPose(.75f, yaw);
        prediction.AdvancePresentation(Tick / 2);
        AssertPose(.875f, yaw);
        prediction.AdvancePresentation(Tick / 2 - .0001f);
        Assert.Equal(yaw, Presenter.LocalPose(prediction).Yaw, 5);
        prediction.AdvancePresentation(.0001f);
        AssertPose(1f, MathF.PI);
        Assert.Equal(landed, prediction.PredictedState);

        void AssertPose(float progress, float expectedYaw)
        {
            TilePose pose = Presenter.LocalPose(prediction);
            Assert.Equal(20.5f + dx * progress, pose.Position.X, 5);
            Assert.Equal(-20.5f - dz * progress, pose.Position.Z, 5);
            Assert.Equal(expectedYaw, pose.Yaw, 5);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Entity_nomination_resumes_only_when_the_local_predicted_landing_is_displayed(bool interaction)
    {
        using var scenario = LandingScenario(interaction);
        TileWorldClient client = scenario.Client;
        int nominations = 0;
        client.EntityAimPointResolver = Nominate;
        client.Prediction.Predict(TileCommand.None);
        TileMoveState landed = client.Prediction.PredictedState;
        byte[] encoded = Encode(landed);
        Assert.False(landed.IsStepping);
        Assert.Equal(TileDirection.N, landed.Facing);

        AssertGliding(21.25f);
        client.AdvancePresentation(Tick / 2);
        AssertGliding(21.375f);
        client.AdvancePresentation(Tick / 2 - .0001f);
        AssertGliding(21.4999f);
        client.AdvancePresentation(.0001f);
        TilePose pose = client.LocalPose;
        Assert.Equal(21.5f, pose.Position.X, 5);
        Assert.Equal(MathF.PI, pose.Yaw, 5);
        Assert.Equal(1, nominations);
        Assert.Equal(landed, client.Prediction.PredictedState);
        Assert.Equal(encoded, Encode(client.Prediction.PredictedState));

        void AssertGliding(float x)
        {
            TilePose drawn = client.LocalPose;
            Assert.Equal(0, nominations);
            Assert.Equal(x, drawn.Position.X, 5);
            Assert.Equal(MathF.PI / 2, drawn.Yaw, 5);
        }

        bool Nominate(long id, TileRect footprint, int plane, out Vector2 aim)
        {
            Assert.Equal(Target, id);
            Assert.Equal(new TileRect(21, 21, 1, 1), footprint);
            Assert.Equal(0, plane);
            nominations++;
            aim = new Vector2(21, 21);
            return true;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reconciliation_preserves_the_local_commanded_landing_without_delaying_aim_for_its_offset(bool correction)
    {
        using var scenario = LandingScenario(interaction: true);
        TileWorldClient client = scenario.Client;
        int seq = client.Prediction.Predict(TileCommand.None);
        client.AdvancePresentation(Tick / 2);
        TilePose before = Presenter.LocalPose(client.Prediction);
        TileMoveState basis = client.Prediction.PredictedState;
        if (correction) basis.Tile = basis.StepFrom = new TileCoord(22, 20, 0);

        ReconciliationResult result = client.Prediction.Reconcile(20, basis, seq);
        Assert.False(result.HardSnapApplied);
        TilePose rebased = client.LocalPose;
        Assert.Equal(before.Position, rebased.Position);
        Assert.Equal(MathF.PI / 2, rebased.Yaw, 5);
        client.AdvancePresentation(Tick / 2);
        TilePose landed = client.LocalPose;
        Assert.Equal(correction ? -2.3561945f : MathF.PI, landed.Yaw, 5);
        if (correction) Assert.NotEqual(Presenter.PoseAt(basis.Tile).Position, landed.Position);
    }

    [Fact]
    public void A_correction_only_glide_does_not_hold_the_local_aim_gate_closed()
    {
        using var scenario = new TilePresentationScenario();
        scenario.Snapshot((Target, TileMoveState.At(new TileCoord(21, 21, 0), TileDirection.S)));
        TileWorldClient client = scenario.Client;
        client.Prediction.Reset(Locked(new TileCoord(20, 20, 0)));
        TileMoveState basis = Locked(new TileCoord(21, 20, 0));

        Assert.False(client.Prediction.Reconcile(20, basis, -1).HardSnapApplied);
        TilePose pose = client.LocalPose;
        Assert.Equal(20.5f, pose.Position.X, 5);
        Assert.Equal(MathF.PI, pose.Yaw, 5);
        Assert.NotEqual(Presenter.PoseAt(basis.Tile).Position, pose.Position);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Reset_reseed_and_hard_snap_discard_the_prior_commanded_landing(int replacement)
    {
        using var scenario = LandingScenario(interaction: true);
        TileWorldClient client = scenario.Client;
        int seq = client.Prediction.Predict(TileCommand.None);
        client.AdvancePresentation(Tick / 2);
        TileMoveState basis = client.Prediction.PredictedState;
        if (replacement == 0) client.Prediction.Reset(basis);
        else if (replacement == 1) client.Prediction.Reseed(basis);
        else
        {
            basis.Tile = basis.StepFrom = new TileCoord(26, 20, 0);
            Assert.True(client.Prediction.Reconcile(20, basis, seq).HardSnapApplied);
        }

        TilePose pose = Presenter.LocalPose(client.Prediction);
        Assert.Equal(MathF.PI, pose.Yaw, 5);
        Assert.Equal(replacement == 2 ? -1.7681919f : MathF.PI, client.LocalPose.Yaw, 5);
        if (replacement != 1) Assert.Equal(Presenter.PoseAt(basis.Tile).Position, pose.Position);
    }

    static TilePresentationScenario LandingScenario(bool interaction)
    {
        var scenario = new TilePresentationScenario();
        scenario.Snapshot((Target, TileMoveState.At(new TileCoord(21, 21, 0), TileDirection.S)));
        TileMoveState state = FinalStep(1, 0);
        if (interaction)
        {
            state.InteractTarget = Target;
            state.InteractDomain = TileInteractionDomain.Entity;
        }
        else state.CombatTarget = Target;
        scenario.Client.Prediction.Reset(state);
        scenario.Client.Prediction.Predict(TileCommand.None);
        scenario.Client.AdvancePresentation(Tick);
        Assert.Equal(3, scenario.Client.Prediction.PredictedState.StepTicks);
        return scenario;
    }

    static TileMoveState FinalStep(int dx, int dz)
    {
        TileMoveState state = TileMoveState.At(new TileCoord(20 + dx, 20 + dz, 0), TileDirection.N);
        state.StepFrom = new TileCoord(20, 20, 0);
        state.StepTotal = 4;
        state.StepTicks = 2;
        return state;
    }

    static TileMoveState Locked(TileCoord tile)
    {
        TileMoveState state = TileMoveState.At(tile, TileDirection.N);
        state.InteractTarget = Target;
        state.InteractDomain = TileInteractionDomain.Entity;
        return state;
    }

    static byte[] Encode(TileMoveState state)
    {
        var world = new World();
        Entity entity = world.Spawn();
        world.Set(entity, new NetId(100));
        world.Set(entity, state);
        return SnapshotWriter.Write(world, TileProtocol.CreateRegistry());
    }
}
