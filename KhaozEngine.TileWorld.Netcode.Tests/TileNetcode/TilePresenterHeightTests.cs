using System.Numerics;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// The GROUND under a drawn pose. Every assertion here is against <see cref="TileWorldDocument.HeightAt"/> read at
/// the pose's own drawn position rather than against a hand-computed lerp, because the defect this file pins is
/// exactly a sample taken somewhere other than where the body is drawn.
/// </summary>
public class TilePresenterHeightTests
{
    // A lattice that ramps on BOTH axes, a metre per tile east and a tenth of one north, so a sample that swapped
    // the two axes, dropped one of them, or lost the tile-centre half reads a number no other point produces.
    static TileWorldDocument Slope()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        for (int z = 0; z <= 16; z++)
            for (int x = 0; x <= 16; x++)
                doc.SetCornerHeightCm(x, z, 0, (short)(x * 100 + z * 10));
        return doc;
    }

    [Fact]
    public void A_tile_pose_lands_on_the_ground_under_its_own_centre()
    {
        TileWorldDocument doc = Slope();
        var presenter = new TilePresenter(doc);

        TilePose pose = presenter.PoseAt(new TileCoord(4, 7, 0));

        Assert.Equal(doc.HeightAt(TileWorldSpace.WorldX(4.5f, doc.TileSize),
            TileWorldSpace.WorldZ(7.5f, doc.TileSize), 0), pose.Position.Y, 5);
        // 4.5 metres of the east ramp plus 0.75 of the north one. Neither the plane floor, nor the corner's own
        // height, nor the two axes swapped.
        Assert.Equal(5.25f, pose.Position.Y, 5);
    }

    [Fact]
    public void A_mid_step_body_samples_the_ground_under_where_it_is_drawn()
    {
        TileWorldDocument doc = Slope();
        var presenter = new TilePresenter(doc);
        TileMoveState state = TileMoveState.At(new TileCoord(5, 7, 0), TileDirection.E);
        state.StepFrom = new TileCoord(4, 7, 0);
        state.StepTotal = 4;
        state.StepTicks = 1;

        // Half a tick of the step spent plus one carried forward, so the body is exactly halfway across.
        TilePose pose = presenter.Pose(state, extraTicks: 1f);

        Assert.Equal(doc.HeightAt(pose.Position.X, pose.Position.Z, 0), pose.Position.Y, 5);
        float from = presenter.PoseAt(new TileCoord(4, 7, 0)).Position.Y;
        float to = presenter.PoseAt(new TileCoord(5, 7, 0)).Position.Y;
        // Following the slope BETWEEN the two tile centres rather than stepping at the tile edge.
        Assert.InRange(pose.Position.Y, from + 1e-3f, to - 1e-3f);
        Assert.Equal((from + to) * 0.5f, pose.Position.Y, 5);
    }

    [Fact]
    public void The_local_body_samples_the_ground_under_its_smoothed_position()
    {
        TileWorldDocument doc = Slope();
        var presenter = new TilePresenter(doc);
        var sim = new TileMoveSimulator(TileMoveSimulatorTests.Bake(doc), new TileStepTicks(4, 2));
        var prediction = new ClientPrediction<TileMoveState, TileCommand>(
            sim, new PredictionSettings(0.25f, 64, 0.5f, 8f, 0.01f));
        prediction.Reset(TileMoveState.At(new TileCoord(2, 7, 0), TileDirection.E));
        prediction.Predict(TileCommand.WalkTo(new TileCoord(8, 7, 0), TileMoveMode.Run));
        prediction.AdvancePresentation(0.125f);

        TilePose pose = presenter.LocalPose(prediction);

        Assert.Equal(doc.HeightAt(pose.Position.X, pose.Position.Z, 0), pose.Position.Y, 5);
        // Off the plane floor, so the assertion above is not satisfied by a flat world.
        Assert.True(pose.Position.Y > 2f, $"local body drew at {pose.Position.Y} on a ramp two metres up");
    }

    [Fact]
    public void The_placeholder_presenter_stays_on_the_plane_floor()
    {
        var presenter = new TilePresenter(tileSize: 1f, planeHeight: 3f);

        Assert.Null(presenter.Ground);
        Assert.Equal(6f, presenter.PoseAt(new TileCoord(4, 7, 2)).Position.Y, 5);
        Assert.Equal(0f, presenter.PoseAt(new TileCoord(4, 7, 0)).Position.Y, 5);
    }

    [Fact]
    public void A_document_with_no_authored_heights_stays_on_the_plane_floor()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        var presenter = new TilePresenter(doc);

        Assert.Equal(0f, presenter.PoseAt(new TileCoord(4, 7, 0)).Position.Y, 5);
        Assert.Equal(2f * doc.PlaneHeight, presenter.PoseAt(new TileCoord(4, 7, 2)).Position.Y, 5);
    }

    [Fact]
    public void A_plane_with_no_authored_heights_lifts_the_slope_by_one_plane_height()
    {
        TileWorldDocument doc = Slope();
        var presenter = new TilePresenter(doc);
        float ground = presenter.PoseAt(new TileCoord(4, 7, 0)).Position.Y;

        // The SLOPE carried up, not a flat world's plane floor, which is what makes the two asserts below say
        // something a presenter that ignored the lattice could not also say.
        Assert.Equal(5.25f, ground, 5);
        Assert.Equal(ground + doc.PlaneHeight, presenter.PoseAt(new TileCoord(4, 7, 1)).Position.Y, 5);
        Assert.Equal(ground + 2f * doc.PlaneHeight, presenter.PoseAt(new TileCoord(4, 7, 2)).Position.Y, 5);
    }

    [Fact]
    public void An_aimed_pose_draws_at_the_same_height_as_the_plain_one()
    {
        TileWorldDocument doc = Slope();
        var presenter = new TilePresenter(doc);
        var aim = new Vector2(9f, 11f);
        TileMoveState standing = TileMoveState.At(new TileCoord(4, 7, 0), TileDirection.N);

        Assert.Equal(presenter.Pose(standing).Position, presenter.Pose(standing, aim).Position);
        Assert.NotEqual(presenter.Pose(standing).Yaw, presenter.Pose(standing, aim).Yaw);
        // On the terrain in both, rather than agreeing on the plane floor.
        Assert.Equal(5.25f, presenter.Pose(standing, aim).Position.Y, 5);

        TileMoveState stepping = standing;
        stepping.Tile = new TileCoord(5, 7, 0);
        stepping.StepFrom = new TileCoord(4, 7, 0);
        stepping.StepTotal = 4;
        stepping.StepTicks = 3;
        Assert.Equal(presenter.Pose(stepping, 0.5f).Position, presenter.Pose(stepping, aim, 0.5f).Position);
    }

    [Fact]
    public void A_hand_supplied_ground_source_is_what_the_pose_reads()
    {
        var ramp = new Ramp();
        var presenter = new TilePresenter(tileSize: 1f, planeHeight: 3f, ramp);

        Assert.Same(ramp, presenter.Ground);
        // The tile CENTRE, 4.5 along the ramp, plus the plane the source itself reports rather than the floor.
        Assert.Equal(4.5f * 0.25f + 10f, presenter.PoseAt(new TileCoord(4, 7, 1)).Position.Y, 5);
    }

    [Fact]
    public void A_fractional_plane_index_reads_between_the_two_plane_samples()
    {
        var presenter = new TilePresenter(tileSize: 1f, planeHeight: 3f, new Ramp());
        var planar = new Vector2(4f, 7f);

        float low = presenter.PoseAt(planar, 0f, TileDirection.S).Position.Y;
        float high = presenter.PoseAt(planar, 1f, TileDirection.S).Position.Y;

        Assert.Equal((low + high) * 0.5f, presenter.PoseAt(planar, 0.5f, TileDirection.S).Position.Y, 5);
    }

    // A synthetic source rather than a document: a quarter-metre ramp along tile x, ten metres a plane, so every
    // number it produces is unreachable from the plane floor and from the documents above.
    sealed class Ramp : ITileGroundHeight
    {
        public float HeightAt(float tileX, float tileZ, int plane) => tileX * 0.25f + plane * 10f;
    }
}
