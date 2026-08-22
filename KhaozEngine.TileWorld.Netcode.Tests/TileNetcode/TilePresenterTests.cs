using System;
using System.Numerics;
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
        Assert.Equal(MathF.PI, pose.Yaw, 5);
    }

    // The four cardinals, spelled out, so a reader can see the convention without deriving it. Tile SOUTH is the
    // zero, because a model authored facing +z is facing world +z, and world +z is tile south.
    [Fact]
    public void Yaw_reads_zero_at_tile_south_and_a_quarter_turn_at_east()
    {
        Assert.Equal(0f, TilePresenter.Yaw(TileDirection.S), 5);
        Assert.Equal(MathF.PI / 2f, TilePresenter.Yaw(TileDirection.E), 5);
        Assert.Equal(MathF.PI, MathF.Abs(TilePresenter.Yaw(TileDirection.N)), 5);
        Assert.Equal(-MathF.PI / 2f, TilePresenter.Yaw(TileDirection.W), 5);
    }

    // The convention pin, and it is the one that matters: the four numbers above are only correct if they are the
    // ENGINE's model yaw, the value CharacterFacing.YawOf hands a Matrix4x4.CreateRotationY for a world direction
    // and the hand TileObjectProps.YawRadians turns tile objects by. Get it backwards and an avatar walking north
    // is drawn facing south, next to a tile object that is not.
    //
    // Pinned against the TRANSFORM the convention feeds rather than against literals: for all eight directions,
    // CreateRotationY at the presenter's yaw must carry a +z-forward mesh onto the world-space delta of that step,
    // taken through TileWorldSpace so the tile-z negation is the engine's own rather than a copy of it. Any change
    // to either hand fails here. YawOf itself is not called because it lives in KhaozEngine.Game.Render3D, and a
    // reference to it would drag the whole 3D renderer into this package's test graph, which is what CI selects
    // test projects by.
    [Fact]
    public void Yaw_points_a_forward_facing_mesh_along_the_world_delta_of_every_direction()
    {
        foreach (TileDirection d in TileDirections.All)
        {
            (int dx, int dz) = TileDirections.Delta(d);
            Vector3 expected = Vector3.Normalize(TileWorldSpace.ToWorld(dx, 0f, dz, 1f));
            Vector3 drawn = Vector3.Transform(Vector3.UnitZ,
                Matrix4x4.CreateRotationY(TilePresenter.Yaw(d)));
            Assert.Equal(expected.X, drawn.X, 5);
            Assert.Equal(expected.Z, drawn.Z, 5);
        }
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
            sim, new PredictionSettings(0.25f, 64, 0.5f, 8f, 0.01f));
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
