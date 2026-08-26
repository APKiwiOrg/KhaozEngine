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

    // A pose names the tile CENTRE: tile (4, 7) spans 4..5 and 7..8, the same span its ground quad covers and the
    // same one a 1x1 prop is centred in, so the presenter adds half a tile on each axis. The half tile is added
    // in TILE units, before TileWorldSpace, so the z half goes through the negation with the coordinate it belongs
    // to. Drawn on the corner instead, an avatar stands half a tile diagonally off every prop it walks up to.
    [Fact]
    public void A_standing_state_maps_to_its_tile_centre_line_through_TileWorldSpace()
    {
        TilePose pose = P.Pose(TileMoveState.At(new TileCoord(4, 7, 2), TileDirection.N));
        Assert.Equal(TileWorldSpace.ToWorld(4.5f, 6f, 7.5f, 1f), pose.Position);
        Assert.Equal(MathF.PI, pose.Yaw, 5);
    }

    // The centring against the two things it has to agree with, rather than against a literal half: the ground
    // quad's own span and the prop anchor's own centre. TileObjectProps lives in KhaozEngine.TileWorld.Render3D
    // and referencing it would drag the 3D renderer into this package's test graph, which is what CI selects test
    // projects by, so the anchor's formula is restated here for a 1x1 object and pinned against the pose.
    [Fact]
    public void A_pose_lands_on_the_same_point_a_one_by_one_prop_on_that_tile_is_anchored_at()
    {
        var tile = new TileCoord(4, 7, 0);
        const float TileSize = 1f;
        // TileObjectProps.AnchorPosition for a 1x1: WorldX(o.X + sizeX / 2f), WorldZ(o.Z + sizeZ / 2f).
        float propX = TileWorldSpace.WorldX(tile.X + 1 / 2f, TileSize);
        float propZ = TileWorldSpace.WorldZ(tile.Z + 1 / 2f, TileSize);

        TilePose pose = P.Pose(TileMoveState.At(tile, TileDirection.N));
        Assert.Equal(propX, pose.Position.X, 5);
        Assert.Equal(propZ, pose.Position.Z, 5);
        // And inside the tile's own ground quad, which spans x..x+1 and z..z+1 in tile units.
        Assert.InRange(TileWorldSpace.TileX(pose.Position.X, TileSize), tile.X, tile.X + 1);
        Assert.InRange(TileWorldSpace.TileZ(pose.Position.Z, TileSize), tile.Z, tile.Z + 1);
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

    // THE glide, pinned at its three defining points. The body runs from StepFrom INTO Tile, linearly, by the
    // step's own tick count: nothing at the start of the step, half way at half the ticks, and exactly on the
    // committed tile when the last tick lands. That is the whole curve, and it is the OSRS model the feel
    // iteration ruled on (see TileGlideTests for the four rounds and why this one won).
    //
    // Note the fraction-zero case: the body draws on the tile it is LEAVING while the simulation has already
    // committed it to the next one. That is the lead commit working as designed, not a bug, and it is exactly the
    // gap the game-side true-tile marker exists to make visible.
    [Fact]
    public void The_body_glides_linearly_from_the_departed_tile_into_the_committed_one()
    {
        TileMoveState s = TileMoveState.At(new TileCoord(0, 1, 0), TileDirection.N);
        s.StepFrom = new TileCoord(0, 0, 0);
        s.StepTotal = 4;
        // Centred, so the walk from tile (0, 0) into tile (0, 1) runs from tile-space z 0.5 to 1.5.
        s.StepTicks = 0;
        Assert.Equal(TileWorldSpace.ToWorld(0.5f, 0f, 0.5f, 1f), P.Pose(s).Position);
        s.StepTicks = 2;
        Assert.Equal(TileWorldSpace.ToWorld(0.5f, 0f, 1f, 1f), P.Pose(s).Position);
        // The last tick of a step is the tick that lands: the simulator pulls StepFrom up to Tile there, so the
        // fraction reaching 1 is spelled as a standing state. Both spellings draw on the committed tile.
        s.StepTicks = 4;
        Assert.Equal(TileWorldSpace.ToWorld(0.5f, 0f, 1.5f, 1f), P.Pose(s).Position);
        Assert.Equal(TileWorldSpace.ToWorld(0.5f, 0f, 1.5f, 1f),
            P.Pose(TileMoveState.At(new TileCoord(0, 1, 0), TileDirection.N)).Position);

        // The ROUTE is not consulted, at any fraction: a remote's route is owner-only, so a pose that needed one
        // could never place an observer honestly.
        s.StepTicks = 2;
        s.Route = new TileRoute(new[] { new TileCoord(5, 5, 0) }, 0);
        Assert.Equal(TileWorldSpace.ToWorld(0.5f, 0f, 1f, 1f), P.Pose(s).Position);
    }

    // extraTicks is what carries a REMOTE forward between snapshots: the sample holds the step progress at its own
    // instant and the presenter adds the fraction of a tick since. Clamped at the end of the step, so a sample
    // that went overdue (a lost snapshot, a stalled server) parks on the committed tile rather than walking past
    // it into ground nobody routed it over.
    [Fact]
    public void Extra_ticks_carry_the_glide_forward_and_clamp_at_the_committed_tile()
    {
        TileMoveState s = TileMoveState.At(new TileCoord(0, 1, 0), TileDirection.N);
        s.StepFrom = new TileCoord(0, 0, 0);
        s.StepTotal = 4;
        s.StepTicks = 2;
        Assert.Equal(TileWorldSpace.ToWorld(0.5f, 0f, 1.25f, 1f), P.Pose(s, extraTicks: 1f).Position);
        Assert.Equal(TileWorldSpace.ToWorld(0.5f, 0f, 1.5f, 1f), P.Pose(s, extraTicks: 9f).Position);
        // Negative is treated as zero rather than walking the body backwards out of its step.
        Assert.Equal(P.Pose(s).Position, P.Pose(s, extraTicks: -3f).Position);
    }

    // PoseAt is the mapping every other entry point here goes through, so it takes a CONTINUOUS tile point rather
    // than a lattice one and centres it exactly as a whole tile is centred. Pinned at a whole coordinate against a
    // standing pose, so the two entry points cannot drift apart, and at a fractional one, where the centring must
    // still be the same half tile rather than a rounded lattice cell.
    [Fact]
    public void PoseAt_centres_a_continuous_tile_point_the_same_way_a_whole_tile_is_centred()
    {
        TileMoveState s = TileMoveState.At(new TileCoord(4, 7, 2), TileDirection.W);
        Assert.Equal(P.Pose(s), P.PoseAt(new Vector2(4f, 7f), 2f, TileDirection.W));
        Assert.Equal(TileWorldSpace.ToWorld(4.75f, 6f, 7.25f, 1f),
            P.PoseAt(new Vector2(4.25f, 6.75f), 2f, TileDirection.W).Position);
        // A fractional plane is legal: it is what a prediction layer's eased vertical hands in.
        Assert.Equal(TileWorldSpace.ToWorld(4.5f, 4.5f, 7.5f, 1f),
            P.PoseAt(new Vector2(4f, 7f), 1.5f, TileDirection.W).Position);
    }

    // The TileCoord overload is the OVERLAY's call: a true-tile marker and a route highlight place whole tiles,
    // one call per tile, with no state and no glide. It has to land on the same point a standing body does, or a
    // marker sits off the avatar it is marking, and it has to take the tile's own plane rather than plane 0, or a
    // marker on an upper floor draws through the ground.
    [Fact]
    public void PoseAt_places_a_whole_tile_on_the_same_centre_a_standing_body_draws_on()
    {
        var tile = new TileCoord(4, 7, 2);
        Assert.Equal(P.Pose(TileMoveState.At(tile, TileDirection.S)).Position, P.PoseAt(tile).Position);
        Assert.Equal(TileWorldSpace.ToWorld(4.5f, 6f, 7.5f, 1f), P.PoseAt(tile).Position);
        // A marker with no facing of its own takes yaw 0, so a head that ignores the yaw pays nothing for it.
        Assert.Equal(0f, P.PoseAt(tile).Yaw);
        Assert.Equal(MathF.PI / 2f, P.PoseAt(tile, TileDirection.E).Yaw, 5);
    }

    // The RULES' tile and the BODY are different points mid step, and an overlay that mixes them up draws the
    // marker on the avatar and calls the lead invisible. This is the pair the true-tile marker exists to show: a
    // whole tile apart at the start of the step, meeting exactly when the body lands.
    [Fact]
    public void The_committed_tile_and_the_drawn_body_are_a_whole_step_apart_at_the_start_of_a_step()
    {
        TileMoveState s = TileMoveState.At(new TileCoord(0, 1, 0), TileDirection.N);
        s.StepFrom = new TileCoord(0, 0, 0);
        s.StepTotal = 4;
        s.StepTicks = 0;
        Assert.Equal(TileWorldSpace.ToWorld(0.5f, 0f, 1.5f, 1f), P.PoseAt(s.Tile).Position);
        Assert.Equal(TileWorldSpace.ToWorld(0.5f, 0f, 0.5f, 1f), P.Pose(s).Position);
        s.StepTicks = 4;
        Assert.Equal(P.PoseAt(s.Tile).Position, P.Pose(s).Position);
    }

    // The LOCAL player draws off the prediction layer's rendered override, which is the same StepFrom-to-Tile
    // glide eased between command ticks with the decaying correction offset folded in. Read from the override
    // rather than from the tile, because rounding a continuous position back to a lattice cell here would throw
    // away every frame of smoothing the layer just computed.
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
        // Past the CENTRE of tile (2, 2), which is where a standing pose would be, so the local path is centred
        // too. Measured against the corner instead, the half tile alone would satisfy this.
        Assert.True(pose.Position.Z < TileWorldSpace.WorldZ(2.5f, 1f));  // already gliding north (world -z)
        // And SHORT of the tile the simulation already committed it to, which is the lead the marker shows.
        Assert.True(pose.Position.Z > P.PoseAt(pred.PredictedState.Tile).Position.Z);
    }

    [Fact]
    public void A_plane_index_becomes_a_height_in_metres_through_the_plane_height()
    {
        var ground = new TilePresenter(tileSize: 2f, planeHeight: 4f);
        TilePose pose = ground.Pose(TileMoveState.At(new TileCoord(3, 5, 1), TileDirection.E));
        // Centred, and the half tile scales with the tile size: a metre on each axis for a two metre tile.
        Assert.Equal(TileWorldSpace.ToWorld(3.5f, 4f, 5.5f, 2f), pose.Position);
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
