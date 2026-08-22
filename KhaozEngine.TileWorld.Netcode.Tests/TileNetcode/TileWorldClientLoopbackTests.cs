using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileWorldClientLoopbackTests
{
    const float Tick = 0.25f;
    const float Frame = 0.05f;
    static readonly TileStepTicks Ticks = new(walk: 4, run: 2);

    sealed class Harness : IDisposable
    {
        public readonly TileWorldServer Server;
        public readonly TileWorldClient Client;
        readonly InMemoryTransportHub hub;
        readonly INetTransport clientTransport;
        float serverAccum;

        // goalRadius is handed to BOTH heads, because that is the contract: the client mirrors the server's own
        // refusal of an out-of-range goal, and a test that set it on one head would be testing the mismatch.
        public Harness(TileWorldDocument serverDoc, TileWorldDocument clientDoc, TileCoord spawn, float clientPhase,
            int goalRadius = TilePathfinder.DefaultMaxRadius)
        {
            hub = new InMemoryTransportHub();
            Server = new TileWorldServer(hub.Server,
                TileWorldServerTickTests.Config(spawn) with { MaxGoalRadius = goalRadius },
                TileMoveSimulatorTests.Bake(serverDoc),
                new TileDocumentTargets(serverDoc, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator());
            clientTransport = hub.CreateClient();
            Client = new TileWorldClient(clientTransport, new TileWorldClientConfig
            {
                TickSeconds = Tick,
                StepTicks = Ticks,
                MaxGoalRadius = goalRadius,
            }, TileMoveSimulatorTests.Bake(clientDoc));
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
}
