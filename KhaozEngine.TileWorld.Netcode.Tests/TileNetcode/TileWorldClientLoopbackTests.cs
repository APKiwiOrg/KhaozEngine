using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileWorldClientLoopbackTests
{
    const float Tick = 0.25f;
    const float Frame = 0.05f;
    static readonly TileStepTicks Ticks = new(walk: 4, run: 2);

    // A pose names the tile CENTRE, so the half tile comes back off on the way to a tile coordinate. The tests
    // below are all about WHICH TILES a remote was drawn on, which the centring does not move, so they read the
    // same numbers they always did with the offset undone here rather than in every assertion.
    static float DrawnTileX(float worldX) => TileWorldSpace.TileX(worldX, 1f) - 0.5f;

    static float DrawnTileZ(float worldZ) => TileWorldSpace.TileZ(worldZ, 1f) - 0.5f;

    sealed class Harness : IDisposable
    {
        public readonly TileWorldServer Server;
        public readonly TileWorldClient Client;
        readonly InMemoryTransportHub hub;
        readonly INetTransport clientTransport;
        float serverAccum;

        // goalRadius is handed to BOTH heads, because that is the contract: the client mirrors the server's own
        // refusal of an out-of-range goal, and a test that set it on one head would be testing the mismatch.
        // gameComponents is handed to BOTH registries for the same reason goalRadius goes to both heads: a game
        // component registered on one side only is skipped on the way in, silently, which is the whole of #700.
        public Harness(TileWorldDocument serverDoc, TileWorldDocument clientDoc, TileCoord spawn, float clientPhase,
            int goalRadius = TilePathfinder.DefaultMaxRadius, Action<ReplicationRegistry>? gameComponents = null)
        {
            hub = new InMemoryTransportHub();
            Server = new TileWorldServer(hub.Server,
                TileWorldServerTickTests.Config(spawn) with { MaxGoalRadius = goalRadius },
                TileMoveSimulatorTests.Bake(serverDoc),
                new TileDocumentTargets(serverDoc, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator(),
                TileProtocol.CreateRegistry(gameComponents));
            clientTransport = hub.CreateClient();
            Client = new TileWorldClient(clientTransport, new TileWorldClientConfig
            {
                TickSeconds = Tick,
                StepTicks = Ticks,
                MaxGoalRadius = goalRadius,
            }, TileMoveSimulatorTests.Bake(clientDoc), registry: TileProtocol.CreateRegistry(gameComponents));
            // Phase the client's command tick off the server's, which is the loopback lesson: two hosts stepping
            // in lockstep hide every ordering bug a real client's independent clock exposes.
            Client.Tick(clientPhase);
            Client.Poll();
        }

        public void Frames(int count)
        {
            for (int i = 0; i < count; i++)
            {
                Client.Tick(Frame);
                Server.Poll();
                serverAccum += Frame;
                while (serverAccum >= Tick)
                {
                    serverAccum -= Tick;
                    Server.Tick(Tick);
                }
                Client.Poll();
                Client.AdvancePresentation(Frame);
            }
        }

        /// <summary>Drops the client's transport, which is how a real link dies. The server's own Disconnect is a
        /// no-op on this hub, so a kick alone never reaches the client as a dropped session.</summary>
        public void Drop() => hub.DisconnectClient(clientTransport);

        public void Dispose() { Client.Dispose(); Server.Dispose(); }
    }

    [Fact]
    public void A_clean_walk_costs_zero_corrections()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        using var h = new Harness(doc, TileMoveSimulatorTests.FlatWorld(), new TileCoord(10, 10, 0), clientPhase: 0.13f);
        h.Frames(4);
        Assert.True(h.Client.IsJoined);

        h.Client.Queue(TileCommand.WalkTo(new TileCoord(10, 18, 0), TileMoveMode.Run));
        h.Frames(120);

        Assert.Equal(new TileCoord(10, 18, 0), h.Client.Prediction.PredictedState.Tile);
        Assert.True(h.Server.TryGetPlayerState(0, out TileMoveState server));
        Assert.Equal(new TileCoord(10, 18, 0), server.Tile);
        Assert.Equal(0, h.Client.CorrectionCount);
        Assert.Equal(0, h.Client.SnapCount);
    }

    // Changing direction while already moving is the commonest thing a player does, and the splice that answers it
    // belongs in a loopback test rather than only in a simulator one: the client predicts the splice a whole tick
    // before the server applies it, so a splice depending on anything beyond the state and the command would show
    // up here as a correction, and two heads resolving the inherited step differently would show up as a cut.
    [Fact]
    public void A_re_click_mid_step_costs_no_correction_and_no_snap()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        using var h = new Harness(doc, TileMoveSimulatorTests.FlatWorld(), new TileCoord(10, 10, 0), clientPhase: 0.13f);
        h.Frames(4);
        Assert.True(h.Client.IsJoined);

        // Walk, because a four tick step leaves a wide window to click inside. A run would too, at two ticks, but
        // the wide one is what makes the click reliably land part way through rather than on a boundary.
        h.Client.Queue(TileCommand.WalkTo(new TileCoord(10, 18, 0), TileMoveMode.Walk));

        int progressAtClick = -1;
        for (int i = 0; i < 60 && progressAtClick < 0; i++)
        {
            h.Frames(1);
            TileMoveState p = h.Client.Prediction.PredictedState;
            if (p.Route.IsIdle || p.StepTicks == 0) continue;
            progressAtClick = p.StepTicks;
            h.Client.Queue(TileCommand.WalkTo(new TileCoord(18, 10, 0), TileMoveMode.Walk));
        }
        // The whole point of the test is that the click lands on a state with progress to inherit. A boundary
        // click exercises the old path and would pass for the wrong reason.
        Assert.InRange(progressAtClick, 1, 3);

        h.Frames(400);

        Assert.True(h.Server.TryGetPlayerState(0, out TileMoveState server));
        Assert.Equal(new TileCoord(18, 10, 0), server.Tile);
        Assert.Equal(new TileCoord(18, 10, 0), h.Client.Prediction.PredictedState.Tile);
        Assert.Equal(0, h.Client.CorrectionCount);
        Assert.Equal(0, h.Client.SnapCount);
    }

    // The blocker sits on the tile the client's OWN pathfinder picks as the first step of the walk, which is what
    // makes the two heads step different ways on the very first tick. Further down the route it would cost nothing
    // at all, and the test below pins that as the deliberate property it is.
    [Fact]
    public void One_blocker_the_client_cannot_see_costs_exactly_one_snap_and_then_agreement()
    {
        TileWorldDocument serverDoc = TileMoveSimulatorTests.FlatWorld();
        serverDoc.AddObject("tree", 10, 11, 0, 0);                     // only the SERVER knows about this
        using var h = new Harness(serverDoc, TileMoveSimulatorTests.FlatWorld(), new TileCoord(10, 10, 0), 0.13f);
        h.Frames(4);

        h.Client.Queue(TileCommand.WalkTo(new TileCoord(10, 18, 0), TileMoveMode.Run));
        h.Frames(160);

        Assert.True(h.Server.TryGetPlayerState(0, out TileMoveState server));
        Assert.Equal(server.Tile, h.Client.Prediction.PredictedState.Tile);
        Assert.Equal(new TileCoord(10, 18, 0), server.Tile);
        Assert.True(h.Client.CorrectionCount >= 1);
        Assert.Equal(1, h.Client.SnapCount);
    }

    // The other half of the blocker story, and the reason the one above has to put its tree underfoot. The route is
    // AUTHORITATIVE and rides the owner-only channel, so the first snapshot to acknowledge the click replaces the
    // client's own pathfinder result with the server's. A blocker the client cannot see, but which the server
    // routed around before the client ever walked that far, is therefore free: the client never predicts a step
    // toward it, so there is nothing to correct and nothing to cut.
    [Fact]
    public void A_blocker_further_along_the_route_costs_nothing_because_the_authoritative_route_arrives_first()
    {
        TileWorldDocument serverDoc = TileMoveSimulatorTests.FlatWorld();
        serverDoc.AddObject("tree", 10, 14, 0, 0);
        using var h = new Harness(serverDoc, TileMoveSimulatorTests.FlatWorld(), new TileCoord(10, 10, 0), 0.13f);
        h.Frames(4);

        h.Client.Queue(TileCommand.WalkTo(new TileCoord(10, 18, 0), TileMoveMode.Run));
        h.Frames(20);

        // Mid walk, and this is the load-bearing assertion: the client is walking the DETOUR it cannot see,
        // because the route it is predicting along is the server's rather than the one its own pathfinder built.
        // Sampled here rather than compared against the server's tile, because a healthy client is AHEAD: its
        // command tick fires before the server's, so it has predicted one tick the server has not applied yet. The
        // two agree at every reconciliation, which is what CorrectionCount below actually measures.
        Assert.DoesNotContain(new TileCoord(10, 14, 0), h.Client.Prediction.PredictedState.Route.Tiles);

        h.Frames(140);
        Assert.True(h.Server.TryGetPlayerState(0, out TileMoveState server));
        Assert.Equal(new TileCoord(10, 18, 0), server.Tile);
        Assert.Equal(new TileCoord(10, 18, 0), h.Client.Prediction.PredictedState.Tile);
        Assert.Equal(0, h.Client.CorrectionCount);
        Assert.Equal(0, h.Client.SnapCount);
    }

    [Fact]
    public void A_remote_glides_across_a_step_rather_than_jumping_between_squares()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        using var h = new Harness(doc, TileMoveSimulatorTests.FlatWorld(), new TileCoord(10, 10, 0), 0.13f);
        h.Frames(4);
        long remote = h.Server.SpawnPlayer(slot: 1, "remote", "Rem");
        h.Server.Enqueue(1, 0, TileCommand.WalkTo(new TileCoord(12, 14, 0), TileMoveMode.Run));
        h.Frames(20);

        var zs = new List<float>();
        for (int i = 0; i < 20; i++)
        {
            h.Frames(1);
            if (h.Client.TryGetRemotePose(remote, out TilePose pose)) zs.Add(pose.Position.Z);
        }
        Assert.True(zs.Count > 10);
        Assert.True(zs.Distinct().Count() > zs.Count / 2, "a remote must move on most frames, not once per tick");
        Assert.True(zs.Zip(zs.Skip(1), (a, b) => MathF.Abs(b - a)).All(d => d <= 1f),
            "a remote must never jump more than a tile between frames");
    }

    // The run toggle rides on EVERY command rather than on the click that started the walk, so a client sending
    // TileCommand.None between clicks (Continue at WALK) holds a run for exactly one tick and then quietly drops
    // out of it. Both halves are pinned here: the toggle survives a whole route, and a change to it lands at the
    // start of the next STEP rather than restarting the walk.
    [Fact]
    public void The_run_toggle_rides_every_tick_and_a_change_lands_at_the_next_step()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        using var h = new Harness(doc, TileMoveSimulatorTests.FlatWorld(), new TileCoord(10, 10, 0), 0.13f);
        h.Frames(4);

        h.Client.Queue(TileCommand.WalkTo(new TileCoord(10, 20, 0), TileMoveMode.Run));
        Assert.Equal(TileMoveMode.Run, h.Client.RunMode);      // the click's own mode becomes the held toggle
        h.Frames(40);

        Assert.True(h.Server.TryGetPlayerState(0, out TileMoveState running));
        Assert.Equal(TileMoveMode.Run, running.Mode);
        Assert.Equal(2, running.StepTotal);
        Assert.NotEqual(new TileCoord(10, 10, 0), running.Tile);

        h.Client.RunMode = TileMoveMode.Walk;                  // the head's run button, released mid route
        h.Frames(40);

        Assert.True(h.Server.TryGetPlayerState(0, out TileMoveState walking));
        Assert.Equal(TileMoveMode.Walk, walking.Mode);
        Assert.Equal(4, walking.StepTotal);
        Assert.NotEqual(new TileCoord(10, 20, 0), walking.Tile);   // still walking, the route was not cancelled
        Assert.Equal(0, h.Client.CorrectionCount);
    }

    [Fact]
    public void A_notice_raises_its_token_and_the_typed_event_that_belongs_to_it()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        using var h = new Harness(doc, TileMoveSimulatorTests.FlatWorld(), new TileCoord(10, 10, 0), 0.13f);
        h.Frames(4);

        var tokens = new List<string>();
        int cannotReach = 0;
        h.Client.NoticeReceived += tokens.Add;
        h.Client.CannotReach += () => cannotReach++;

        h.Server.BroadcastNotice(TileServerReason.CannotReach);
        h.Server.BroadcastNotice("game:bank-closed");
        h.Frames(2);

        Assert.Equal(new[] { TileServerReason.CannotReach, "game:bank-closed" }, tokens);
        Assert.Equal(1, cannotReach);
    }

    [Fact]
    public void An_opaque_game_message_crosses_in_both_directions_untouched()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        using var h = new Harness(doc, TileMoveSimulatorTests.FlatWorld(), new TileCoord(10, 10, 0), 0.13f);
        h.Frames(4);

        byte[]? fromServer = null;
        ushort serverKind = 0;
        h.Client.OnGameMessage += (kind, payload) => { serverKind = kind; fromServer = payload.ToArray(); };
        byte[]? fromClient = null;
        ushort clientKind = 0;
        h.Server.OnGameMessage += (_, kind, payload) => { clientKind = kind; fromClient = payload.ToArray(); };

        h.Server.SendGameMessageTo(0, 77, new byte[] { 1, 2, 3 });
        h.Client.SendGameMessage(9, new byte[] { 4, 5 });
        h.Frames(2);

        Assert.Equal(77, serverKind);
        Assert.Equal(new byte[] { 1, 2, 3 }, fromServer);
        Assert.Equal(9, clientKind);
        Assert.Equal(new byte[] { 4, 5 }, fromClient);
    }

    [Fact]
    public void A_kick_arrives_as_its_reason_and_the_dropped_link_as_a_disconnect()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        using var h = new Harness(doc, TileMoveSimulatorTests.FlatWorld(), new TileCoord(10, 10, 0), 0.13f);
        h.Frames(4);
        Assert.True(h.Client.IsJoined);

        string? token = null;
        int dropped = 0;
        h.Client.NoticeReceived += t => token = t;
        h.Client.Disconnected += () => dropped++;

        h.Server.Kick(0, TileServerReason.Kicked);
        h.Frames(2);
        Assert.Equal(TileServerReason.Kicked, token);          // the reason lands BEFORE the link goes

        h.Drop();
        h.Frames(2);

        Assert.Equal(TileServerReason.Kicked, token);
        Assert.Equal(1, dropped);
        Assert.False(h.Client.IsJoined);
    }

    // A goal beyond the reach bound is NOT dropped on the way out: the server answers it by rewriting the command
    // to a Continue at the mode it carried, so the tick still applies the run toggle. The client mirrors that
    // rewrite before it predicts. Without the mirror it would predict a walk the server never started, and every
    // click past the bound would cost a correction and a cut.
    [Fact]
    public void A_goal_past_the_reach_bound_is_rewritten_the_same_way_on_both_heads()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        using var h = new Harness(doc, TileMoveSimulatorTests.FlatWorld(), new TileCoord(10, 10, 0), 0.13f,
            goalRadius: 3);
        h.Frames(4);

        h.Client.Queue(TileCommand.WalkTo(new TileCoord(10, 30, 0), TileMoveMode.Run));
        h.Frames(40);

        // Sent rather than dropped, so the run toggle it carried still rode in.
        Assert.Equal(0, h.Client.DroppedClickCount);
        Assert.True(h.Server.TryGetPlayerState(0, out TileMoveState server));
        Assert.Equal(new TileCoord(10, 10, 0), server.Tile);
        Assert.Equal(new TileCoord(10, 10, 0), h.Client.Prediction.PredictedState.Tile);
        Assert.Equal(TileMoveMode.Run, server.Mode);
        Assert.Equal(0, h.Client.CorrectionCount);
        Assert.Equal(0, h.Client.SnapCount);

        // A goal INSIDE the bound still walks, so the bound refuses rather than disables.
        h.Client.Queue(TileCommand.WalkTo(new TileCoord(10, 13, 0), TileMoveMode.Run));
        h.Frames(40);
        Assert.True(h.Server.TryGetPlayerState(0, out TileMoveState walked));
        Assert.Equal(new TileCoord(10, 13, 0), walked.Tile);
        Assert.Equal(0, h.Client.CorrectionCount);
    }

    // A remote that turns on the spot has to turn on screen. TileMoveSimulator.BeginInteract sets Facing with no
    // tile change and no step progress for a ZERO-STEP interact, which is the ordinary click on the thing you are
    // already standing next to, so a receiver that only resampled a remote on movement would draw that player
    // facing their last step until they next walked.
    [Fact]
    public void A_remote_that_turns_on_the_spot_is_redrawn_facing_the_new_way()
    {
        TileWorldDocument serverDoc = TileMoveSimulatorTests.FlatWorld();
        TileObject booth = serverDoc.AddObject("bank_booth", 13, 10, 0, 0);
        TileWorldDocument clientDoc = TileMoveSimulatorTests.FlatWorld();
        clientDoc.AddObject("bank_booth", 13, 10, 0, 0);
        using var h = new Harness(serverDoc, clientDoc, new TileCoord(10, 10, 0), 0.13f);
        h.Frames(4);
        long remote = h.Server.SpawnPlayer(slot: 1, "remote", "Rem");
        h.Server.SetPlayerState(1, TileMoveState.At(new TileCoord(12, 10, 0), TileDirection.S));
        h.Frames(24);

        Assert.True(h.Client.TryGetRemotePose(remote, out TilePose before));
        Assert.Equal(TilePresenter.Yaw(TileDirection.S), before.Yaw, 5);

        h.Server.Enqueue(1, 0, TileCommand.Interact(booth.Id, TileMoveMode.Run));
        h.Frames(24);

        Assert.True(h.Server.TryGetPlayerState(1, out TileMoveState turned));
        Assert.Equal(new TileCoord(12, 10, 0), turned.Tile);        // it never stepped
        Assert.NotEqual(TileDirection.S, turned.Facing);            // and it did turn
        Assert.True(h.Client.TryGetRemotePose(remote, out TilePose after));
        Assert.Equal(TilePresenter.Yaw(turned.Facing), after.Yaw, 5);
        Assert.Equal(before.Position, after.Position);              // without moving on screen
    }

    // A server teleport and a mispredicted step both CUT, and a head has to tell them apart: one means "you are
    // somewhere else now" (snap the camera, re-centre everything keyed to the player), the other means "you
    // guessed a step wrong". Both halves are pinned here, in one session, so neither can quietly start reporting
    // the other.
    [Fact]
    public void A_server_teleport_raises_its_own_event_and_a_mispredicted_step_does_not()
    {
        TileWorldDocument serverDoc = TileMoveSimulatorTests.FlatWorld();
        serverDoc.AddObject("tree", 10, 11, 0, 0);                  // only the SERVER knows about this
        using var h = new Harness(serverDoc, TileMoveSimulatorTests.FlatWorld(), new TileCoord(10, 10, 0), 0.13f);
        int teleports = 0;
        h.Client.Teleported += () => teleports++;

        h.Frames(12);
        Assert.True(h.Client.IsJoined);
        Assert.Equal(1, teleports);                                 // the seed, which has no prior position

        h.Client.Queue(TileCommand.WalkTo(new TileCoord(10, 18, 0), TileMoveMode.Run));
        h.Frames(160);
        Assert.Equal(1, h.Client.SnapCount);                        // the hidden tree cut the walk exactly once
        Assert.Equal(1, teleports);                                 // and a cut step is not a teleport

        h.Server.SetPlayerState(0, TileMoveState.At(new TileCoord(20, 20, 0), TileDirection.S), teleport: true);
        h.Frames(12);
        Assert.Equal(2, teleports);
        Assert.Equal(new TileCoord(20, 20, 0), h.Client.Prediction.PredictedState.Tile);
    }

    // A remote is drawn between the two tiles of the step it is TAKING, both of which ride the everyone channel
    // now. The route is still owner-only, and still not needed: the pair says where the body is outright, so the
    // receiver reconstructs nothing and guesses nothing. The tempting alternative, extrapolating forward along
    // Facing, fails here twice over: Facing is rewritten toward an interaction target while a step is in flight,
    // and it would never draw the honest in-between position at all.
    [Fact]
    public void A_remote_is_drawn_between_the_tile_it_left_and_the_tile_it_is_committed_to()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        using var h = new Harness(doc, TileMoveSimulatorTests.FlatWorld(), new TileCoord(10, 10, 0), 0.13f);
        h.Frames(4);
        long remote = h.Server.SpawnPlayer(slot: 1, "remote", "Rem");
        h.Server.SetPlayerState(1, TileMoveState.At(new TileCoord(12, 10, 0), TileDirection.S));
        h.Frames(24);

        h.Server.Enqueue(1, 0, TileCommand.WalkTo(new TileCoord(12, 11, 0), TileMoveMode.Run));
        var drawn = new List<float>();
        bool sawTheGlide = false;
        for (int i = 0; i < 60; i++)
        {
            h.Frames(1);
            if (!h.Client.TryGetRemotePose(remote, out TilePose pose)) continue;
            float tileZ = DrawnTileZ(pose.Position.Z);
            drawn.Add(tileZ);
            if (tileZ <= 10.001f || tileZ >= 10.999f) continue;
            sawTheGlide = true;
            // Mid glide, and the tile the body is walking into is one the server committed before the client ever
            // started drawing the walk into it. That is the lead commit seen end to end: the authority owns the
            // destination, the picture catches up.
            Assert.True(h.Server.TryGetPlayerState(1, out TileMoveState live));
            Assert.Equal(new TileCoord(12, 11, 0), live.Tile);
        }

        Assert.True(sawTheGlide, "a remote must be drawn between the two tiles, not snapped from one to the other");
        Assert.All(drawn, z => Assert.InRange(z, 9.999f, 11.001f));
        Assert.Equal(11f, drawn[^1], 3);                            // and it parks on the tile it is standing on
    }

    // The corner is where a forward guess gives itself away. A receiver extrapolating along Facing, or along the
    // last step's direction, would draw the remote past x = 16, out of the corridor it walked, and then snatch it
    // back. The glide only ever runs between the two tiles of the step actually being taken, so every drawn point
    // lies on the walked path.
    [Fact]
    public void At_a_corner_the_glide_follows_the_step_actually_taken()
    {
        const float Eps = 0.001f;
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        using var h = new Harness(doc, TileMoveSimulatorTests.FlatWorld(), new TileCoord(10, 10, 0), 0.13f);
        h.Frames(4);
        long remote = h.Server.SpawnPlayer(slot: 1, "remote", "Rem");
        // Facing EAST from the start, which is the direction of the first leg. A forward guess is therefore still
        // inside the corridor while the remote stands and while it walks east, and gives itself away at the corner
        // and nowhere else, which is the case this test is here for.
        h.Server.SetPlayerState(1, TileMoveState.At(new TileCoord(12, 10, 0), TileDirection.E));
        h.Frames(24);

        var drawn = new List<(float X, float Z)>();
        void Walk(int seq, TileCoord goal, int frames)
        {
            h.Server.Enqueue(1, seq, TileCommand.WalkTo(goal, TileMoveMode.Run));
            for (int i = 0; i < frames; i++)
            {
                h.Frames(1);
                if (h.Client.TryGetRemotePose(remote, out TilePose pose))
                    drawn.Add((DrawnTileX(pose.Position.X), DrawnTileZ(pose.Position.Z)));
            }
        }

        Walk(0, new TileCoord(16, 10, 0), 60);
        Assert.True(h.Server.TryGetPlayerState(1, out TileMoveState east));
        Assert.Equal(new TileCoord(16, 10, 0), east.Tile);          // the first leg finished before the second one
        Walk(1, new TileCoord(16, 13, 0), 80);

        foreach ((float x, float z) in drawn)
        {
            Assert.InRange(x, 12f - Eps, 16f + Eps);
            Assert.InRange(z, 10f - Eps, 13f + Eps);
            Assert.True(MathF.Abs(z - 10f) < Eps || MathF.Abs(x - 16f) < Eps,
                $"drawn at ({x}, {z}), which is off the L the remote actually walked");
        }
        Assert.Equal(16f, drawn[^1].X, 3);
        Assert.Equal(13f, drawn[^1].Z, 3);
    }

    // Two tiles that are not one step apart are not a step: a teleport, a plane change, or a remote that left the
    // interest set and came back somewhere else. Sliding between them would draw the avatar walking over every
    // tile in the gap, so the glide CUTS and the remote appears on its tile. The state itself says so now, because
    // every discontinuous placement goes through TileMoveState.At and seeds the glide origin onto the tile.
    [Fact]
    public void A_remote_that_reappears_more_than_one_step_away_cuts_to_its_tile()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        using var h = new Harness(doc, TileMoveSimulatorTests.FlatWorld(), new TileCoord(10, 10, 0), 0.13f);
        h.Frames(4);
        long remote = h.Server.SpawnPlayer(slot: 1, "remote", "Rem");
        h.Server.SetPlayerState(1, TileMoveState.At(new TileCoord(12, 10, 0), TileDirection.S));
        h.Frames(24);

        h.Server.SetPlayerState(1, TileMoveState.At(new TileCoord(12, 20, 0), TileDirection.S), teleport: true);
        var drawn = new List<float>();
        for (int i = 0; i < 40; i++)
        {
            h.Frames(1);
            if (h.Client.TryGetRemotePose(remote, out TilePose pose))
                drawn.Add(DrawnTileZ(pose.Position.Z));
        }

        // Every frame drew it on one tile or the other, never on the ten tiles between them.
        Assert.All(drawn, z => Assert.True(MathF.Abs(z - 10f) < 0.001f || MathF.Abs(z - 20f) < 0.001f,
            $"drawn at tile z {z}, which is between the two tiles rather than on either"));
        Assert.Equal(20f, drawn[^1], 3);
    }

    // The OTHER half of the cut rule: a pair of tiles that IS one step apart in x and z but sits on two different
    // planes. Gliding between those would draw the avatar walking one tile sideways and one storey down through the
    // floor it is standing on. The wire cannot even spell it now, because the glide's origin takes the tile's own
    // plane, and a teleport seeds the origin onto the tile besides. The VIEWER moves with the remote on purpose:
    // the serve is plane filtered, so a remote that changed floor on its own would leave the viewer's interest set
    // and be resampled as a first sighting rather than as a step at all.
    [Fact]
    public void A_remote_that_changes_plane_one_step_away_cuts_rather_than_gliding_between_floors()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        using var h = new Harness(doc, TileMoveSimulatorTests.FlatWorld(), new TileCoord(10, 10, 0), 0.13f);
        h.Frames(4);
        long remote = h.Server.SpawnPlayer(slot: 1, "remote", "Rem");
        h.Server.SetPlayerState(1, TileMoveState.At(new TileCoord(12, 10, 0), TileDirection.S));
        h.Frames(24);
        Assert.True(h.Client.TryGetRemotePose(remote, out TilePose onTheGroundFloor));
        Assert.Equal(0f, onTheGroundFloor.Position.Y, 3);

        // Both written in the same gap between ticks, so no serve ever sees the two on different planes and the
        // remote stays in the viewer's interest across the change. That is what puts the pair in front of GlideFrom.
        h.Server.SetPlayerState(0, TileMoveState.At(new TileCoord(10, 10, 1), TileDirection.S), teleport: true);
        h.Server.SetPlayerState(1, TileMoveState.At(new TileCoord(12, 11, 1), TileDirection.S), teleport: true);

        float planeHeight = h.Client.Presenter.PlaneHeight;
        var drawn = new List<(float Y, float Z)>();
        for (int i = 0; i < 40; i++)
        {
            h.Frames(1);
            if (h.Client.TryGetRemotePose(remote, out TilePose pose))
                drawn.Add((pose.Position.Y, DrawnTileZ(pose.Position.Z)));
        }

        Assert.NotEmpty(drawn);
        Assert.All(drawn, p => Assert.True(
            (MathF.Abs(p.Y) < 0.001f && MathF.Abs(p.Z - 10f) < 0.001f)
            || (MathF.Abs(p.Y - planeHeight) < 0.001f && MathF.Abs(p.Z - 11f) < 0.001f),
            $"drawn at height {p.Y}, tile z {p.Z}, which is between the two floors rather than on either"));
        Assert.Equal(planeHeight, drawn[^1].Y, 3);
        Assert.Equal(11f, drawn[^1].Z, 3);
    }

    // A remote that walks out of the area of interest stops being served, and the client's own per-remote
    // bookkeeping has to let go of it too: a sample nothing refreshes would keep the departed player on screen,
    // frozen on the last tile anybody saw them on, for the rest of the session.
    [Fact]
    public void A_remote_that_leaves_the_interest_set_stops_being_presented()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        using var h = new Harness(doc, TileMoveSimulatorTests.FlatWorld(), new TileCoord(10, 10, 0), 0.13f);
        h.Frames(4);
        long remote = h.Server.SpawnPlayer(slot: 1, "remote", "Rem");
        h.Server.SetPlayerState(1, TileMoveState.At(new TileCoord(12, 10, 0), TileDirection.S));
        h.Frames(24);
        Assert.Contains(remote, h.Client.RemoteNetIds);
        Assert.True(h.Client.TryGetRemotePose(remote, out _));

        // Well past the 15 tile interest radius, still inside the one region this world has.
        h.Server.SetPlayerState(1, TileMoveState.At(new TileCoord(60, 60, 0), TileDirection.S), teleport: true);
        h.Frames(24);

        Assert.DoesNotContain(remote, h.Client.RemoteNetIds);
        Assert.False(h.Client.TryGetRemotePose(remote, out _));
    }

    [Fact]
    public void A_click_off_the_loaded_map_is_dropped_before_it_is_ever_sent()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        using var h = new Harness(doc, TileMoveSimulatorTests.FlatWorld(), new TileCoord(10, 10, 0), 0.13f);
        h.Frames(4);

        h.Client.Queue(TileCommand.WalkTo(new TileCoord(5000, 5000, 0), TileMoveMode.Run));
        h.Client.Queue(TileCommand.WalkTo(new TileCoord(10, 12, 9), TileMoveMode.Run));
        h.Frames(20);

        Assert.Equal(2, h.Client.DroppedClickCount);
        Assert.Equal(new TileCoord(10, 10, 0), h.Client.Prediction.PredictedState.Tile);
    }

    [Fact]
    public void A_refusal_at_the_door_surfaces_its_reason_token_and_never_joins()
    {
        var hub = new InMemoryTransportHub();
        IConnectionAuthenticator gate = ConnectionGate.Wrap(new AllowAllAuthenticator(), "tile-2", "hash-2");
        using var server = new TileWorldServer(hub.Server, TileWorldServerTickTests.Config(new TileCoord(1, 1, 0)),
            TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()), null, gate);
        using var client = new TileWorldClient(hub.CreateClient(),
            new TileWorldClientConfig { TickSeconds = Tick, StepTicks = Ticks },
            TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()), null,
            TileProtocol.BuildConnectToken("tile-1", "hash-2", Array.Empty<byte>()));

        string? refused = null;
        client.RefusedAtDoor += r => refused = r;
        client.Poll();
        server.Poll();
        client.Poll();

        Assert.False(client.IsJoined);
        Assert.True(HandshakeToken.TryParseIncompatibleVersion(refused, out string required));
        Assert.Equal("tile-2", required);
    }

    // A game's OWN replicated component, the seam #700 was about. The server constructor has always taken a
    // registry. The client built one internally and had no way to be handed the matching one, so a game component
    // was skipped on the way in by the unknown-extension forward-compat path, silently, with no workaround (View is
    // get-only). The pair is symmetric now, and this test is what says the components actually arrive.
    struct Bounty : IComponent
    {
        public int Gold;
    }

    const ushort BountyTypeId = TileProtocol.FirstGameTypeId;

    static void RegisterBounty(ReplicationRegistry reg) =>
        reg.Register<Bounty>(BountyTypeId, (v, w) => w.Write(v.Gold), r => new Bounty { Gold = r.ReadInt32() });

    [Fact]
    public void A_game_component_registered_on_both_heads_arrives_on_the_client()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        using var h = new Harness(doc, TileMoveSimulatorTests.FlatWorld(), new TileCoord(10, 10, 0), 0.13f,
            gameComponents: RegisterBounty);
        h.Frames(4);
        long remote = h.Server.SpawnPlayer(slot: 1, "remote", "Rem");
        h.Server.SetPlayerState(1, TileMoveState.At(new TileCoord(12, 10, 0), TileDirection.S));
        Assert.True(h.Server.Host.TryGetOwner(remote, out CellSim cell, out Entity owned));
        cell.World.Set(owned, new Bounty { Gold = 4242 });
        h.Frames(24);

        Assert.True(h.Client.View.TryGetEntity(remote, out Entity mirrored));
        Assert.True(h.Client.World.TryGet(mirrored, out Bounty bounty));
        Assert.Equal(4242, bounty.Gold);
    }

    // The other half of the same seam, and the reason the parameter had to exist rather than the registry being
    // guessed: a client built with the DEFAULT registry skips the component instead of failing, so a game that
    // forgot to pass its own sees empty entities and no error anywhere.
    [Fact]
    public void A_game_component_the_client_never_registered_is_skipped_rather_than_breaking_the_snapshot()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        using var h = new Harness(doc, TileMoveSimulatorTests.FlatWorld(), new TileCoord(10, 10, 0), 0.13f);
        h.Frames(4);
        long remote = h.Server.SpawnPlayer(slot: 1, "remote", "Rem");
        h.Server.SetPlayerState(1, TileMoveState.At(new TileCoord(12, 10, 0), TileDirection.S));
        Assert.True(h.Server.Host.TryGetOwner(remote, out CellSim cell, out Entity owned));
        // Registered on the SERVER only, which is what the client ctor's registry parameter exists to prevent.
        h.Server.Registry.Register<Bounty>(BountyTypeId, (v, w) => w.Write(v.Gold), r => new Bounty { Gold = r.ReadInt32() });
        cell.World.Set(owned, new Bounty { Gold = 4242 });
        h.Frames(24);

        Assert.True(h.Client.View.TryGetEntity(remote, out Entity mirrored));
        Assert.False(h.Client.World.TryGet(mirrored, out Bounty _));
        // The rest of the snapshot still lands, so nothing about this is loud.
        Assert.True(h.Client.World.TryGet(mirrored, out TileMoveState mirroredState));
        Assert.Equal(new TileCoord(12, 10, 0), mirroredState.Tile);
    }
}
