using System;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TilePresenterTests
{
    static readonly TilePresenter P = new(tileSize: 1f, planeHeight: 3f);

    [Fact]
    public void A_standing_state_maps_to_its_tile_centre_line_through_TileWorldSpace()
    {
        TilePose pose = P.Pose(TileMoveState.At(new TileCoord(4, 7, 2), TileDirection.N));
        Assert.Equal(TileWorldSpace.ToWorld(4f, 6f, 7f, 1f), pose.Position);
        Assert.Equal(0f, pose.Yaw, 5);
    }

    [Fact]
    public void Yaw_turns_east_a_quarter_turn_from_north()
    {
        Assert.Equal(0f, TilePresenter.Yaw(TileDirection.N), 5);
        Assert.Equal(MathF.PI / 2f, TilePresenter.Yaw(TileDirection.E), 5);
        Assert.Equal(MathF.PI, MathF.Abs(TilePresenter.Yaw(TileDirection.S)), 5);
        Assert.Equal(-MathF.PI / 2f, TilePresenter.Yaw(TileDirection.W), 5);
    }

    [Fact]
    public void A_mid_step_state_sits_between_the_two_tiles()
    {
        TileMoveState s = TileMoveState.At(new TileCoord(0, 0, 0), TileDirection.N);
        s.Route = new TileRoute(new[] { new TileCoord(0, 1, 0) }, 0);
        s.StepTotal = 4;
        s.StepTicks = 2;
        Assert.Equal(TileWorldSpace.ToWorld(0f, 0f, 0.5f, 1f), P.Pose(s).Position);
        Assert.Equal(TileWorldSpace.ToWorld(0f, 0f, 0.75f, 1f), P.Pose(s, extraTicks: 1f).Position);
        Assert.Equal(TileWorldSpace.ToWorld(0f, 0f, 1f, 1f), P.Pose(s, extraTicks: 9f).Position);
    }

    [Fact]
    public void The_local_pose_reads_the_predictions_rendered_override()
    {
        var sim = new TileMoveSimulator(
            TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()), new TileStepTicks(4, 2));
        var pred = new ClientPrediction<TileMoveState, TileCommand>(
            sim, new PredictionSettings(0.25f, 64, 1f, 8f, 0.01f));
        pred.Reset(TileMoveState.At(new TileCoord(2, 2, 0), TileDirection.E));
        pred.Predict(TileCommand.WalkTo(new TileCoord(2, 6, 0), TileMoveMode.Run));
        pred.AdvancePresentation(0.125f);
        TilePose pose = P.LocalPose(pred);
        Assert.True(pose.Position.Z < TileWorldSpace.WorldZ(2f, 1f));   // already gliding north (world -z)
    }

    [Fact]
    public void A_plane_index_becomes_a_height_in_metres_through_the_plane_height()
    {
        var ground = new TilePresenter(tileSize: 2f, planeHeight: 4f);
        TilePose pose = ground.Pose(TileMoveState.At(new TileCoord(3, 5, 1), TileDirection.E));
        Assert.Equal(TileWorldSpace.ToWorld(3f, 4f, 5f, 2f), pose.Position);
        Assert.Equal(MathF.PI / 2f, pose.Yaw, 5);
    }

    [Fact]
    public void A_presenter_built_from_a_document_takes_its_tile_size_and_plane_height()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        doc.TileSize = 1.5f;
        doc.PlaneHeight = 5f;
        var p = new TilePresenter(doc);
        Assert.Equal(1.5f, p.TileSize);
        Assert.Equal(5f, p.PlaneHeight);
    }
}
