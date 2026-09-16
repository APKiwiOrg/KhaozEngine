using System;
using System.Numerics;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// A body that holds a lock is DRAWN pointing at its target's centre mass rather than at one of the eight
/// <see cref="TileDirection"/> steps. The rules are untouched: <see cref="TileMoveState.Facing"/> still answers the
/// cardinal side the two footprints touch on, which is exact for the reach question and up to 18 degrees off as a
/// drawn yaw the moment either body is bigger than one tile.
/// <para>The pins here are the two the feature rests on. The continuous formula agrees with the direction overload
/// on all eight steps, so the two can never disagree about which way north is, and a one-tile body beside a one-tile
/// target draws EXACTLY its cardinal, so a consumer asserting <c>Yaw(TileDirection)</c> against a pose for an
/// ordinary fight stays true.</para>
/// </summary>
public class TileAimFacingTests
{
    static readonly TilePresenter P = new(tileSize: 1f, planeHeight: 3f);

    // A 2x1 interactive archetype, the same one TileInteractTests needs and for the same reason: every interactive
    // archetype in the greybox catalog is 1x1, whose centre is its anchor, so nothing else can tell a centred aim
    // point from an anchored one.
    const string WideBooth = """
        { "archetypes": [ { "id": "long_booth", "name": "Long booth", "meshRef": "kit/long_booth.glb",
                            "sizeX": 2, "sizeZ": 1, "collisionKind": "Solid", "interactive": true } ] }
        """;

    // THE agreement pin. Both overloads take the delta in world space, so both go through the same z negation, and a
    // continuous yaw that drifted from the direction one would point a large body at north while its cardinal said
    // south. Exact equality rather than a tolerance: the deltas are whole tiles, so the two calls hand atan2 the
    // same pair of floats.
    [Fact]
    public void The_continuous_yaw_agrees_with_the_eight_directions()
    {
        Vector2[] origins = { new(0f, 0f), new(10f, 20f), new(-7f, 3f), new(4.5f, 9.5f) };
        foreach (TileDirection d in TileDirections.All)
        {
            (int dx, int dz) = TileDirections.Delta(d);
            foreach (Vector2 origin in origins)
                Assert.Equal(TilePresenter.Yaw(d), TilePresenter.Yaw(origin, origin + new Vector2(dx, dz)));
        }
    }

    // The whole reason the feature exists: a 2x2 body beside a one-tile target is nowhere near its cardinal, and the
    // error is the angle the owner reported as both bodies pointing off each other.
    [Fact]
    public void A_large_body_aims_well_off_the_cardinal_the_footprints_touch_on()
    {
        // A 2x2 anchored at (21, 19) touches (20, 20) on its west edge, so the rules face it W.
        float aimed = TilePresenter.Yaw(new Vector2(21.5f, 19.5f), new Vector2(20f, 20f));
        Assert.NotEqual(TilePresenter.Yaw(TileDirection.W), aimed);
        Assert.Equal(-1.8925469f, aimed, 5);
    }

    // Coincident points have no direction to report, so the raw formula answers south, and the pose overload keeps
    // the tile facing instead. That is the case a body locked onto ITSELF hits, which the server clears a tick later.
    [Fact]
    public void A_coincident_aim_point_reads_south_and_the_pose_keeps_the_tile_facing()
    {
        var at = new Vector2(12f, 34f);
        Assert.Equal(0f, TilePresenter.Yaw(at, at));

        TileMoveState s = TileMoveState.At(new TileCoord(12, 34, 0), TileDirection.NE);
        Assert.Equal(TilePresenter.Yaw(TileDirection.NE), P.Pose(s, at).Yaw);
    }

    // The default: the footprint centre, in the tile units PoseAt(Vector2, float, TileDirection) takes, which is the
    // point PoseAt(TileRect, int, TileDirection) already draws an overlay on.
    [Theory]
    [InlineData(10, 20, 1, 1, 10f, 20f)]
    [InlineData(10, 20, 2, 2, 10.5f, 20.5f)]
    [InlineData(10, 20, 3, 3, 11f, 21f)]
    [InlineData(10, 20, 2, 1, 10.5f, 20f)]
    public void The_default_aim_point_is_the_footprint_centre(int x, int z, int w, int h, float aimX, float aimZ)
    {
        ITileTargets targets = new RectTargets(new TileRect(x, z, w, h), plane: 2);

        Assert.True(targets.TryGetAimPoint(RectTargets.Id, out Vector2 aim, out int plane));
        Assert.Equal(new Vector2(aimX, aimZ), aim);
        Assert.Equal(2, plane);
        Assert.Equal(P.PoseAt(new TileRect(x, z, w, h), 2).Position, P.PoseAt(aim, 2, TileDirection.S).Position);
    }

    // A target that does not resolve answers false with default outs, exactly as TryGetFootprint does, because every
    // caller of this already has to handle the miss a stale click leaves behind.
    [Fact]
    public void An_unresolved_target_has_no_aim_point()
    {
        ITileTargets targets = new RectTargets(new TileRect(10, 20, 2, 2), plane: 0);

        Assert.False(targets.TryGetAimPoint(RectTargets.Id + 1, out Vector2 aim, out int plane));
        Assert.Equal(default, aim);
        Assert.Equal(0, plane);
    }

    // The built-in implementers take the default rather than each restating the centre. The document resolver
    // answers a 2x1 booth, whose centre is half a tile east of its anchor, and the entity resolver with nothing
    // captured refuses an id the same way its footprint read does.
    [Fact]
    public void The_built_in_resolvers_take_the_default_aim_point()
    {
        TileWorldCatalogs catalogs =
            TileWorldCatalogs.Merge(TileWorldCatalogs.Greybox(), TileWorldCatalogs.LoadJson(WideBooth, "wide"));
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileObject booth = doc.AddObject("long_booth", 10, 10, 0, 0);
        ITileTargets document = new TileDocumentTargets(doc, catalogs);

        Assert.True(document.TryGetAimPoint(booth.Id, out Vector2 aim, out int plane));
        Assert.Equal(new Vector2(10.5f, 10f), aim);
        Assert.Equal(0, plane);
        Assert.False(document.TryGetAimPoint(booth.Id + 500, out _, out _));

        ITileTargets entities = new TileEntityTargets();
        Assert.False(entities.TryGetAimPoint(1L, out Vector2 none, out int nonePlane));
        Assert.Equal(default, none);
        Assert.Equal(0, nonePlane);
    }

    // The hand-placement overload: the body draws exactly where the ordinary Pose draws it, and only the yaw moves.
    [Fact]
    public void The_pose_overload_moves_the_yaw_and_nothing_else()
    {
        TileMoveState s = TileMoveState.At(new TileCoord(21, 19, 0), TileDirection.W);
        s.FootprintSize = 2;
        var target = new Vector2(20f, 20f);

        TilePose aimed = P.Pose(s, target);
        Assert.Equal(P.Pose(s).Position, aimed.Position);
        Assert.Equal(TilePresenter.Yaw(new Vector2(21.5f, 19.5f), target), aimed.Yaw);
    }

    // The exactness rule, at the level of the formula: a one-tile body beside a one-tile target is a whole-tile
    // delta, so the aimed yaw IS the cardinal and a consumer comparing the two with no tolerance stays green.
    [Theory]
    [InlineData(TileDirection.W)]
    [InlineData(TileDirection.E)]
    [InlineData(TileDirection.S)]
    [InlineData(TileDirection.N)]
    [InlineData(TileDirection.SW)]
    [InlineData(TileDirection.SE)]
    [InlineData(TileDirection.NW)]
    [InlineData(TileDirection.NE)]
    public void A_one_tile_body_beside_a_one_tile_target_aims_exactly_at_its_cardinal(TileDirection d)
    {
        (int dx, int dz) = TileDirections.Delta(d);
        TileMoveState s = TileMoveState.At(new TileCoord(20, 20, 0), d);

        TilePose aimed = P.Pose(s, new Vector2(20 + dx, 20 + dz));
        Assert.Equal(TilePresenter.Yaw(d), aimed.Yaw);
        Assert.Equal(P.Pose(s).Yaw, aimed.Yaw);
    }

    // A 2x2 remote beside a one-tile target, from every reach position it has. The rules face it along the cardinal
    // the two footprints touch on, which for a 2x2 is never through the target's centre: the drawn body points at the
    // centre instead, and the two answers differ on all eight.
    [Theory]
    [InlineData(21, 19)]
    [InlineData(21, 20)]
    [InlineData(18, 19)]
    [InlineData(18, 20)]
    [InlineData(19, 21)]
    [InlineData(20, 21)]
    [InlineData(19, 18)]
    [InlineData(20, 18)]
    public void A_large_remote_aims_at_its_targets_centre_from_every_reach_position(int anchorX, int anchorZ)
    {
        using var h = new TileCombatHarness(TileMoveSimulatorTests.FlatWorld(), new TileCoord(20, 20, 0));
        h.Frames(8);
        long cow = h.Server.SpawnActor(new TileCoord(anchorX, anchorZ, 0),
            new TileActorSpawn(100, 4, TileDirection.S) { FootprintSize = 2 });
        h.Frames(8);
        h.Server.Actors.Command(cow, TileCommand.Attack(h.Client.LocalNetId, TileMoveMode.Walk));
        h.Frames(20);

        // Standing where it was put, in reach of the player, so the pose below is a body at rest rather than a glide.
        Assert.True(h.Server.TryGetActorState(cow, out TileMoveState onServer));
        Assert.Equal(new TileCoord(anchorX, anchorZ, 0), onServer.Tile);
        Assert.Equal(h.Client.LocalNetId, onServer.CombatTarget);
        Assert.False(onServer.IsStepping);

        var centre = new Vector2(anchorX + 0.5f, anchorZ + 0.5f);
        Assert.True(h.Client.TryGetRemotePose(cow, out TilePose pose));
        Assert.Equal(TilePresenter.Yaw(centre, new Vector2(20f, 20f)), pose.Yaw);
        Assert.NotEqual(TilePresenter.Yaw(onServer.Facing), pose.Yaw);
    }

    // The other half of the owner's report: the one-tile player beside the 2x2, from every side of it. The drawn yaw
    // is the same formula from the player's own centre, and the local body reads it off the LATEST capture, which is
    // what the reach rules already read.
    [Theory]
    [InlineData(25, 20)]
    [InlineData(25, 21)]
    [InlineData(28, 20)]
    [InlineData(28, 21)]
    [InlineData(26, 19)]
    [InlineData(27, 19)]
    [InlineData(26, 22)]
    [InlineData(27, 22)]
    public void A_one_tile_local_body_aims_at_a_large_targets_centre_from_every_side(int fromX, int fromZ)
    {
        using var h = new TileCombatHarness(TileMoveSimulatorTests.FlatWorld(), new TileCoord(fromX, fromZ, 0));
        long cow = h.Server.SpawnActor(new TileCoord(26, 20, 0),
            new TileActorSpawn(100, 4, TileDirection.S) { FootprintSize = 2 });
        h.Frames(12);
        h.Client.Queue(TileCommand.Attack(cow, TileMoveMode.Walk));
        h.Frames(12);

        TileMoveState me = h.Client.Prediction.PredictedState;
        Assert.Equal(new TileCoord(fromX, fromZ, 0), me.Tile);
        Assert.Equal(cow, me.CombatTarget);
        Assert.False(me.IsStepping);

        float aimed = TilePresenter.Yaw(new Vector2(fromX, fromZ), new Vector2(26.5f, 20.5f));
        Assert.Equal(aimed, h.Client.LocalPose.Yaw);
        Assert.NotEqual(TilePresenter.Yaw(me.Facing), h.Client.LocalPose.Yaw);
    }

    // The compatibility pin, through the whole client rather than through the formula: one tile against one tile
    // draws EXACTLY the cardinal on both heads' bodies, so a consumer comparing Yaw(TileDirection) with a pose for an
    // ordinary fight keeps passing. No tolerance on either assertion, deliberately.
    [Fact]
    public void A_one_tile_fight_draws_exactly_the_cardinal_on_both_bodies()
    {
        using var h = new TileCombatHarness(TileMoveSimulatorTests.FlatWorld(), new TileCoord(20, 20, 0));
        h.Frames(8);
        long goblin = h.Server.SpawnActor(new TileCoord(21, 20, 0), new TileActorSpawn(100, 4, TileDirection.S));
        h.Frames(8);
        h.Client.Queue(TileCommand.Attack(goblin, TileMoveMode.Walk));
        h.Server.Actors.Command(goblin, TileCommand.Attack(h.Client.LocalNetId, TileMoveMode.Walk));
        h.Frames(20);

        TileMoveState me = h.Client.Prediction.PredictedState;
        Assert.Equal(goblin, me.CombatTarget);
        Assert.Equal(TileDirection.E, me.Facing);
        Assert.Equal(TilePresenter.Yaw(TileDirection.E), h.Client.LocalPose.Yaw);

        Assert.True(h.Client.TryGetRemotePose(goblin, out TilePose pose));
        Assert.Equal(TilePresenter.Yaw(TileDirection.W), pose.Yaw);
    }

    // A body mid step keeps the step's own facing, because a body turning toward what it is walking at while it walks
    // is a different feature and this one is about a body at rest. The second count is what makes the first
    // assertion mean something: it says the aim WOULD have moved the yaw on at least one of those frames.
    [Fact]
    public void A_stepping_body_keeps_its_step_facing()
    {
        using var h = new TileCombatHarness(TileMoveSimulatorTests.FlatWorld(), new TileCoord(20, 20, 0));
        long cow = h.Server.SpawnActor(new TileCoord(26, 20, 0),
            new TileActorSpawn(100, 4, TileDirection.S) { FootprintSize = 2 });
        h.Frames(12);
        h.Client.Queue(TileCommand.Attack(cow, TileMoveMode.Run));

        var centre = new Vector2(26.5f, 20.5f);
        int stepped = 0, wouldHaveTurned = 0;
        for (int i = 0; i < 60; i++)
        {
            h.Frames(1);
            TileMoveState r = h.Client.Prediction.RenderedState;
            if (!r.IsStepping || r.CombatTarget != cow) continue;
            stepped++;
            Assert.Equal(TilePresenter.Yaw(r.Facing), h.Client.LocalPose.Yaw);
            float aimed = TilePresenter.Yaw(new Vector2(r.Tile.X, r.Tile.Z), centre);
            if (MathF.Abs(aimed - TilePresenter.Yaw(r.Facing)) > 1e-3f) wouldHaveTurned++;
        }

        Assert.True(stepped > 0, "the body never stepped, so the rule was never exercised");
        Assert.True(wouldHaveTurned > 0, "the aim never differed from the step facing, so this pinned nothing");
    }

    // A target that stops resolving keeps the tile facing, which is the answer a stale lock gets after the thing it
    // named was deleted. The document resolver reads through, so removing the object is the whole change: the state
    // still names it, and the same pose answers differently a line later.
    [Fact]
    public void A_target_that_stops_resolving_falls_back_to_the_tile_facing()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileObject booth = doc.AddObject("bank_booth", 22, 20, 0, 0);
        using var h = new TileCombatHarness(doc, new TileCoord(20, 20, 0), predictObjectInteractions: true);
        h.Frames(8);

        TileMoveState s = h.Client.Prediction.PredictedState;
        s.Facing = TileDirection.N;
        s.InteractTarget = booth.Id;
        s.InteractDomain = TileInteractionDomain.AuthoredObject;
        h.Client.Prediction.Reset(s);

        // Two tiles due east of the player, so the aimed yaw is E while the tile facing says N.
        Assert.Equal(TilePresenter.Yaw(TileDirection.E), h.Client.LocalPose.Yaw);

        Assert.True(doc.RemoveObject(booth.Id));
        Assert.Equal(TilePresenter.Yaw(TileDirection.N), h.Client.LocalPose.Yaw);
    }

    // The game override, which is the room the issue asked for: a designated aim tile on a body whose centre is the
    // wrong place to look at. The resolver answers a 3x3 and nominates its south-west tile, and the pose points
    // there rather than at the middle of the square.
    [Fact]
    public void A_game_override_of_the_aim_point_is_honoured()
    {
        var hub = new InMemoryTransportHub();
        using INetTransport transport = hub.CreateClient();
        using var client = new TileWorldClient(transport, new TileWorldClientConfig
        {
            TickSeconds = TileCombatHarness.Tick,
            StepTicks = new TileStepTicks(walk: 4, run: 2),
        }, TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()), new AimTileTargets(),
            registry: TileProtocol.CreateRegistry());

        TileMoveState s = TileMoveState.At(new TileCoord(20, 20, 0), TileDirection.N);
        s.InteractTarget = AimTileTargets.Id;
        s.InteractDomain = TileInteractionDomain.AuthoredObject;
        client.Prediction.Reset(s);

        var me = new Vector2(20f, 20f);
        Assert.Equal(TilePresenter.Yaw(me, AimTileTargets.Aim), client.LocalPose.Yaw);
        Assert.NotEqual(TilePresenter.Yaw(me, new Vector2(25f, 21f)), client.LocalPose.Yaw);
    }

    // A 3x3 target whose aim point is NOT its centre, which is the override the interface exists for.
    sealed class AimTileTargets : ITileTargets
    {
        public const long Id = 42L;

        public static readonly Vector2 Aim = new(24f, 20f);

        public bool TryGetFootprint(long target, out TileRect footprint, out int plane)
        {
            footprint = default;
            plane = 0;
            if (target != Id) return false;
            footprint = new TileRect(24, 20, 3, 3);
            return true;
        }

        public bool TryGetAimPoint(long target, out Vector2 tilePlanar, out int plane)
        {
            tilePlanar = default;
            plane = 0;
            if (target != Id) return false;
            tilePlanar = Aim;
            return true;
        }
    }

    // One footprint, one id, nothing else: enough to exercise the interface's own default without a document or a
    // session behind it.
    sealed class RectTargets : ITileTargets
    {
        public const long Id = 7L;

        readonly TileRect rect;
        readonly int plane;

        public RectTargets(TileRect rect, int plane)
        {
            this.rect = rect;
            this.plane = plane;
        }

        public bool TryGetFootprint(long target, out TileRect footprint, out int targetPlane)
        {
            footprint = default;
            targetPlane = 0;
            if (target != Id) return false;
            footprint = rect;
            targetPlane = plane;
            return true;
        }
    }
}
