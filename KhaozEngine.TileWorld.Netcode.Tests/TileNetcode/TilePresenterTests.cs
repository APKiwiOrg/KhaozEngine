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

    // Pose is the RULES' answer, not the body's: the tile the simulation has committed the player to, whatever the
    // step in flight says. The step pair is deliberately not read here (a body is drawn by a TileChase pursuing
    // this point, see TileChaseTests), and neither is the route: a remote's route is owner-only, so a pose that
    // needed one could never place an observer honestly.
    [Fact]
    public void A_pose_names_the_committed_tile_whatever_the_step_in_flight_says()
    {
        TileMoveState s = TileMoveState.At(new TileCoord(0, 1, 0), TileDirection.N);
        s.StepFrom = new TileCoord(0, 0, 0);
        s.StepTotal = 4;
        s.StepTicks = 2;
        // Tile (0, 1) centred is tile-space z 1.5, and it reads that from the tick the step COMMITS rather than
        // from the tick the body would have arrived: this is where the rules say the player is.
        Assert.Equal(TileWorldSpace.ToWorld(0.5f, 0f, 1.5f, 1f), P.Pose(s).Position);
        s.StepTicks = 0;
        Assert.Equal(TileWorldSpace.ToWorld(0.5f, 0f, 1.5f, 1f), P.Pose(s).Position);

        // A route pointing somewhere else cannot move the pose either.
        s.Route = new TileRoute(new[] { new TileCoord(5, 5, 0) }, 0);
        Assert.Equal(TileWorldSpace.ToWorld(0.5f, 0f, 1.5f, 1f), P.Pose(s).Position);
    }

    // PoseAt is the mapping a chased body goes through, so it takes a CONTINUOUS tile point rather than a lattice
    // one and centres it exactly as a whole tile is centred. Pinned against Pose at a whole coordinate, so the two
    // entry points cannot drift apart, and at a fractional one, where the centring must still be the same half
    // tile rather than a rounded lattice cell.
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
