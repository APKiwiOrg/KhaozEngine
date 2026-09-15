using System.Numerics;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// A large body on screen and in the client's reads. The presenter centres a body on its FOOTPRINT, which is the
/// tile centre for a one-tile body and the middle of the NxN square anchored on the south-west tile for a larger
/// one. The client carries the size in both of its remote stores, so the delayed read agrees with the drawn body
/// and the newest-snapshot read is what the client's target resolver answers a rule with.
/// <para>A large remote is spawned through the server and read back on both timelines, and a client predicting an
/// approach to it lands on the same in-range anchor as the server with nothing corrected.</para>
/// </summary>
public class TileFootprintPresentationTests
{
    [Theory, InlineData(1), InlineData(2), InlineData(3)]
    public void A_body_draws_centred_on_its_footprint(int n)
    {
        var presenter = new TilePresenter(1f, 3f);
        TileMoveState s = TileMoveState.At(new TileCoord(10, 20, 0), TileDirection.S);
        s.FootprintSize = n;
        TilePose pose = presenter.Pose(s);
        Assert.Equal(presenter.PoseAt(new TileRect(10, 20, n, n), 0).Position, pose.Position);
        Vector3 expected = TileWorldSpace.ToWorld(10 + n * 0.5f, 0f, 20 + n * 0.5f, 1f);
        Assert.Equal(expected, pose.Position);
    }

    // The glide runs anchor to anchor and the size does not change mid step, so the centre offset over a one-tile
    // body of the same state is the same at the start of the step, half way, and on landing.
    [Fact]
    public void A_gliding_large_body_keeps_its_centre_offset_through_the_step()
    {
        var presenter = new TilePresenter(1f, 3f);
        TileMoveState s = TileMoveState.At(new TileCoord(11, 20, 0), TileDirection.E);
        s.StepFrom = new TileCoord(10, 20, 0);
        s.StepTotal = 4;
        s.FootprintSize = 2;

        s.StepTicks = 2;
        Assert.Equal(TileWorldSpace.ToWorld(10.5f + 1f, 0f, 20f + 1f, 1f), presenter.Pose(s).Position);

        Vector3 offset = TileWorldSpace.ToWorld(0.5f, 0f, 0.5f, 1f);
        foreach (byte ticks in new byte[] { 0, 1, 2, 3, 4 })
        {
            s.StepTicks = ticks;
            TileMoveState one = s;
            one.FootprintSize = 1;
            Vector3 drift = presenter.Pose(s).Position - presenter.Pose(one).Position;
            Assert.Equal(offset.X, drift.X, 5);
            Assert.Equal(offset.Y, drift.Y, 5);
            Assert.Equal(offset.Z, drift.Z, 5);
        }
    }

    // The overlay form centres any rect, square or not, lifts it by the plane index and faces it. A one-tile rect is
    // the whole-tile overlay form exactly, so a marker drawn through either lands on the same point.
    [Fact]
    public void The_overlay_pose_centres_a_rect_and_a_one_tile_rect_is_the_tile_pose()
    {
        var presenter = new TilePresenter(1f, 3f);

        TilePose wide = presenter.PoseAt(new TileRect(4, 7, 3, 2), 2, TileDirection.N);
        Assert.Equal(TileWorldSpace.ToWorld(4f + 1.5f, 6f, 7f + 1f, 1f), wide.Position);
        Assert.Equal(TilePresenter.Yaw(TileDirection.N), wide.Yaw);

        TilePose one = presenter.PoseAt(new TileRect(4, 7, 1, 1), 2);
        Assert.Equal(presenter.PoseAt(new TileCoord(4, 7, 2)), one);
    }

    // Both remote footprint reads over a real session, pinned against their tile twins so the rect is anchored on
    // the tile each timeline already answers. A one-tile remote is its own one-by-one rect on both, and the client's
    // target resolver answers the NEWEST read, never the delayed one.
    [Fact]
    public void Both_remote_footprint_reads_answer_a_one_tile_remote_on_their_own_timelines()
    {
        using var h = new TileCombatHarness(TileMoveSimulatorTests.FlatWorld(), new TileCoord(20, 20, 0));
        h.Frames(8);
        long actor = h.Server.SpawnActor(new TileCoord(26, 20, 0), new TileActorSpawn(100, 4, TileDirection.S));
        h.Frames(20);

        Assert.True(h.Client.TryGetRemoteFootprint(actor, out TileRect delayed, out int delayedPlane));
        Assert.True(h.Client.TryGetRemoteTile(actor, out TileCoord delayedTile));
        Assert.Equal(new TileRect(26, 20, 1, 1), delayed);
        Assert.Equal(new TileRect(delayedTile.X, delayedTile.Z, 1, 1), delayed);
        Assert.Equal(delayedTile.Plane, delayedPlane);

        Assert.True(h.Client.TryGetLatestRemoteFootprint(actor, out TileRect latest, out int latestPlane));
        Assert.True(h.Client.TryGetLatestRemoteTile(actor, out TileCoord latestTile));
        Assert.Equal(new TileRect(26, 20, 1, 1), latest);
        Assert.Equal(new TileRect(latestTile.X, latestTile.Z, 1, 1), latest);
        Assert.Equal(latestTile.Plane, latestPlane);

        Assert.True(new TileRemoteTargets(h.Client).TryGetFootprint(actor, out TileRect resolved, out int plane));
        Assert.Equal(latest, resolved);
        Assert.Equal(latestPlane, plane);
    }

    // A client predicting an approach to a 2x2 at (26, 20) matches the server tick for tick. From the WEST the
    // in-range anchor (25, 20) is also where a one-tile answer stops, so that case alone cannot tell a footprint from
    // a dropped one. From the EAST and the NORTH a one-tile answer picks (27, 20) and (26, 21), both inside the body,
    // so a resolver on either head that lost the size either corrects or stops inside it.
    [Theory]
    [InlineData(20, 20, 25, 20)]
    [InlineData(30, 20, 28, 20)]
    [InlineData(26, 25, 26, 22)]
    public void A_client_predicts_an_approach_to_a_large_remote_with_nothing_corrected(int fromX, int fromZ,
        int anchorX, int anchorZ)
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileCollisionMap map = TileMoveSimulatorTests.Bake(doc);
        using var h = new TileCombatHarness(doc, new TileCoord(fromX, fromZ, 0));
        long actor = h.Server.SpawnActor(new TileCoord(26, 20, 0),
            new TileActorSpawn(100, 4, TileDirection.S) { FootprintSize = 2 });
        h.Frames(8);
        var body = new TileRect(26, 20, 2, 2);
        Assert.True(h.Client.TryGetRemoteFootprint(actor, out TileRect early, out _));
        Assert.Equal(body, early);

        h.Client.Queue(TileCommand.Attack(actor, TileMoveMode.Run));
        h.Frames(60);

        Assert.Equal(0, h.Client.CorrectionCount);
        Assert.Equal(0, h.Client.SnapCount);
        TileMoveState predicted = h.Client.Prediction.PredictedState;
        Assert.True(h.Server.TryGetActorState(h.Client.LocalNetId, out TileMoveState server));
        Assert.Equal(server.Tile, predicted.Tile);
        Assert.Equal(new TileCoord(anchorX, anchorZ, 0), server.Tile);
        Assert.Equal(actor, server.CombatTarget);
        Assert.Equal(actor, predicted.CombatTarget);
        Assert.True(TileReach.Contains(map, body, 0, server.Tile, 1), $"stopped on {server.Tile}");
        Assert.True(h.Client.TryGetRemoteFootprint(actor, out TileRect delayed, out int delayedPlane));
        Assert.Equal(body, delayed);
        Assert.Equal(0, delayedPlane);
        Assert.True(h.Client.TryGetLatestRemoteFootprint(actor, out TileRect latest, out int latestPlane));
        Assert.Equal(body, latest);
        Assert.Equal(0, latestPlane);
    }

    // Both reads refuse what their tile twins refuse, with default outs: an id nobody is tracking and the local
    // player, whose footprint is its own predicted state's.
    [Fact]
    public void Both_remote_footprint_reads_refuse_an_unknown_id_and_the_local_player()
    {
        using var h = new TileCombatHarness(TileMoveSimulatorTests.FlatWorld(), new TileCoord(20, 20, 0));
        h.Frames(8);

        foreach (long id in new[] { 9999L, h.Client.LocalNetId })
        {
            Assert.False(h.Client.TryGetRemoteFootprint(id, out TileRect delayed, out int delayedPlane));
            Assert.Equal(default, delayed);
            Assert.Equal(0, delayedPlane);
            Assert.False(h.Client.TryGetLatestRemoteFootprint(id, out TileRect latest, out int latestPlane));
            Assert.Equal(default, latest);
            Assert.Equal(0, latestPlane);
        }
    }

    // The resolver's LOCAL branch answers the predicted state's whole footprint rather than a one-tile rect on its
    // anchor. A joined player is one tile, so the size is put onto the prediction directly to tell the two apart.
    [Fact]
    public void The_client_resolver_answers_the_local_players_own_footprint()
    {
        using var h = new TileCombatHarness(TileMoveSimulatorTests.FlatWorld(), new TileCoord(20, 20, 0));
        h.Frames(8);
        var targets = new TileRemoteTargets(h.Client);

        Assert.True(targets.TryGetFootprint(h.Client.LocalNetId, out TileRect joined, out int joinedPlane));
        Assert.Equal(new TileRect(20, 20, 1, 1), joined);
        Assert.Equal(0, joinedPlane);

        TileMoveState large = h.Client.Prediction.PredictedState;
        large.FootprintSize = 2;
        h.Client.Prediction.Reset(large);

        Assert.True(targets.TryGetFootprint(h.Client.LocalNetId, out TileRect own, out int plane));
        Assert.Equal(new TileRect(20, 20, 2, 2), own);
        Assert.Equal(0, plane);
    }
}
