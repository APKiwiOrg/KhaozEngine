using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileWorldServerSessionTests
{
    const float Dt = 0.25f;

    static (TileWorldServer server, InMemoryTransportHub hub, INetTransport client) Up(TileCoord spawn)
    {
        var hub = new InMemoryTransportHub();
        TileWorldServer s = TileWorldServerTickTests.Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, spawn);
        INetTransport c = hub.CreateClient();
        return (s, hub, c);
    }

    static void Hello(INetTransport client, string subject)
    {
        var net = new NetClient(client, System.Text.Encoding.UTF8.GetBytes(subject));
        net.Poll();
    }

    [Fact]
    public void A_joining_connection_spawns_a_player_bound_to_its_verified_subject()
    {
        (TileWorldServer s, _, INetTransport c) = Up(new TileCoord(4, 4, 0));
        using (s)
        {
            var joined = new List<(int slot, string account)>();
            s.PlayerJoined += (slot, account) => joined.Add((slot, account));
            Hello(c, "acct-9");
            s.Poll();
            Assert.Single(joined);
            Assert.Equal("acct-9", joined[0].account);
            Assert.True(s.TryGetAccountId(joined[0].slot, out string account));
            Assert.Equal("acct-9", account);
            Assert.Equal(1, s.PlayerCount);
        }
    }

    [Fact]
    public void A_command_frame_off_the_wire_is_buffered_and_drained_one_per_tick()
    {
        (TileWorldServer s, _, INetTransport c) = Up(new TileCoord(4, 4, 0));
        using (s)
        {
            var net = new NetClient(c, Array.Empty<byte>());
            net.Poll();
            s.Poll();
            net.Send(TileProtocol.EncodeCommand(0, TileCommand.WalkTo(new TileCoord(4, 8, 0), TileMoveMode.Run)),
                NetChannelReliability.ReliableOrdered);
            net.Send(TileProtocol.EncodeCommand(1, TileCommand.None), NetChannelReliability.ReliableOrdered);
            s.Poll();
            s.Tick(Dt);
            s.Tick(Dt);
            Assert.True(s.TryGetPlayerState(0, out TileMoveState st));
            Assert.Equal(new TileCoord(4, 5, 0), st.Tile);
        }
    }

    [Fact]
    public void A_malformed_frame_and_an_over_budget_flood_are_dropped_and_counted()
    {
        (TileWorldServer s, _, INetTransport c) = Up(new TileCoord(4, 4, 0));
        using (s)
        {
            var net = new NetClient(c, Array.Empty<byte>());
            net.Poll();
            s.Poll();
            net.Send(new byte[] { 0xEE, 1, 2 }, NetChannelReliability.ReliableOrdered);
            s.Poll();
            Assert.Equal(1, s.DroppedCommandCount);

            // A full bucket at the burst allowance admits exactly CommandBurst of the 200 and drops the rest, on
            // top of the malformed frame already counted. Pinned exactly, because a "> 1" also passes on a limiter
            // that dropped one frame, and the count is what says a burst is SHED rather than the client dropped.
            for (int i = 0; i < 200; i++)
                net.Send(TileProtocol.EncodeCommand(i + 1, TileCommand.None), NetChannelReliability.ReliableOrdered);
            s.Poll();
            Assert.Equal(181, s.DroppedCommandCount);
        }
    }

    [Fact]
    public void A_goal_past_the_search_radius_is_dropped_rather_than_pathed()
    {
        (TileWorldServer s, _, _) = Up(new TileCoord(4, 4, 0));
        using (s)
        {
            s.SpawnPlayer(0, "a", "Ari");
            s.Enqueue(0, 0, TileCommand.WalkTo(new TileCoord(100000, 4, 0), TileMoveMode.Run));
            s.Tick(Dt);
            Assert.True(s.TryGetPlayerState(0, out TileMoveState st));
            Assert.True(st.Route.IsIdle);
            Assert.Equal(new TileCoord(4, 4, 0), st.Tile);
        }
    }

    [Fact]
    public void A_banned_account_is_refused_at_the_door()
    {
        var hub = new InMemoryTransportHub();
        IConnectionAuthenticator gate = ConnectionGate.Wrap(new AllowAllAuthenticator(), "tile-1", "hash-1",
            isBanned: subject => subject == "acct-banned");
        using var s = new TileWorldServer(hub.Server, TileWorldServerTickTests.Config(new TileCoord(4, 4, 0)),
            TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()), null, gate);
        INetTransport c = hub.CreateClient();
        var net = new NetClient(c, TileProtocol.BuildConnectToken("tile-1", "hash-1",
            System.Text.Encoding.UTF8.GetBytes("acct-banned")));
        net.Poll();
        s.Poll();
        net.Poll();
        Assert.Equal(0, s.PlayerCount);
        bool refused = false;
        while (net.TryDequeueEvent(out ClientSessionEvent ev))
            if (ev.Kind == ClientSessionEventKind.Rejected) { refused = true; Assert.Equal(HandshakeToken.BannedReason, ev.RejectReason); }
        Assert.True(refused);
    }

    [Fact]
    public void The_configs_own_ban_predicate_refuses_at_the_door_with_no_composed_gate()
    {
        var hub = new InMemoryTransportHub();
        using var s = new TileWorldServer(hub.Server,
            TileWorldServerTickTests.Config(new TileCoord(4, 4, 0)) with { IsBanned = a => a == "acct-banned" },
            TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld()));
        INetTransport banned = hub.CreateClient();
        var bannedNet = new NetClient(banned, System.Text.Encoding.UTF8.GetBytes("acct-banned"));
        bannedNet.Poll();
        s.Poll();
        bannedNet.Poll();
        Assert.Equal(0, s.PlayerCount);
        bool refused = false;
        while (bannedNet.TryDequeueEvent(out ClientSessionEvent ev))
            if (ev.Kind == ClientSessionEventKind.Rejected) { refused = true; Assert.Equal(HandshakeToken.BannedReason, ev.RejectReason); }
        Assert.True(refused);

        // The same door still admits everyone else, so the gate is a ban check rather than a closed server.
        Hello(hub.CreateClient(), "acct-ok");
        s.Poll();
        Assert.Equal(1, s.PlayerCount);
    }

    [Fact]
    public async Task A_rejoin_is_built_on_the_tile_the_player_left_rather_than_at_the_spawn()
    {
        var store = new InMemoryWorldStore();
        var hub = new InMemoryTransportHub();
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        using TileWorldServer s = TileWorldServerTickTests.Server(doc, hub.Server, new TileCoord(4, 4, 0));
        // The same world the head runs on, which is what lets the binding refuse a record this build has no ground
        // for instead of handing it to a door that throws.
        var persistence = new TileWorldPersistence(s, store, TileMoveSimulatorTests.Bake(doc));

        INetTransport first = hub.CreateClient();
        var firstNet = new NetClient(first, System.Text.Encoding.UTF8.GetBytes("acct-p"));
        firstNet.Poll();
        s.Poll();
        // The leave-save is skipped while the account is still guarded by an in-flight load, so let the
        // load-on-join land before walking the player somewhere and dropping them.
        await persistence.FlushAsync();
        s.SetPlayerState(s.JoinedSlots.Single(), TileMoveState.At(new TileCoord(9, 12, 0), TileDirection.N));
        hub.DisconnectClient(first);
        s.Poll();
        await persistence.FlushAsync();

        INetTransport second = hub.CreateClient();
        var secondNet = new NetClient(second, System.Text.Encoding.UTF8.GetBytes("acct-p"));
        secondNet.Poll();
        s.Poll();
        // Read straight after the join, with no tick and no restore applied: the entity was BUILT on the saved
        // tile off the hint, which is what keeps a quiet rejoin from reading to the client as a teleport.
        Assert.True(s.TryGetPlayerState(s.JoinedSlots.Single(), out TileMoveState st));
        Assert.Equal(new TileCoord(9, 12, 0), st.Tile);
    }

    [Fact]
    public void An_arrival_on_a_reach_tile_raises_the_interaction_exactly_once()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileObject booth = doc.AddObject("bank_booth", 10, 10, 0, 0);
        var hub = new InMemoryTransportHub();
        using var s = new TileWorldServer(hub.Server, TileWorldServerTickTests.Config(new TileCoord(5, 10, 0)),
            TileMoveSimulatorTests.Bake(doc), new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs),
            new AllowAllAuthenticator());
        var raised = new List<long>();
        s.OnInteract += (_, _, target) => raised.Add(target);
        s.SpawnPlayer(0, "a", "Ari");
        s.Enqueue(0, 0, TileCommand.Interact(booth.Id, TileMoveMode.Run));
        for (int i = 0; i < 30; i++) s.Tick(Dt);
        Assert.Single(raised);
        Assert.Equal(booth.Id, raised[0]);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState st));
        Assert.Equal(TileDirection.E, st.Facing);
    }

    [Fact]
    public void A_second_click_replaces_the_pending_action_so_only_the_last_one_fires()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileObject near = doc.AddObject("bank_booth", 7, 10, 0, 0);
        TileObject far = doc.AddObject("bank_booth", 14, 10, 0, 0);
        var hub = new InMemoryTransportHub();
        using var s = new TileWorldServer(hub.Server, TileWorldServerTickTests.Config(new TileCoord(5, 10, 0)),
            TileMoveSimulatorTests.Bake(doc), new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs),
            new AllowAllAuthenticator());
        var raised = new List<long>();
        s.OnInteract += (_, _, target) => raised.Add(target);
        s.SpawnPlayer(0, "a", "Ari");
        s.Enqueue(0, 0, TileCommand.Interact(near.Id, TileMoveMode.Run));
        s.Tick(Dt);
        s.Enqueue(0, 1, TileCommand.Interact(far.Id, TileMoveMode.Run));
        for (int i = 0; i < 40; i++) s.Tick(Dt);
        Assert.Single(raised);
        Assert.Equal(far.Id, raised[0]);
    }

    [Fact]
    public void A_walled_target_answers_with_cannot_reach_and_clears_the_action()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileObject booth = doc.AddObject("bank_booth", 10, 10, 0, 0);
        foreach ((int x, int z) in new[] { (9, 10), (11, 10), (10, 9), (10, 11) }) doc.AddObject("tree", x, z, 0, 0);
        var hub = new InMemoryTransportHub();
        using var s = new TileWorldServer(hub.Server, TileWorldServerTickTests.Config(new TileCoord(5, 10, 0)),
            TileMoveSimulatorTests.Bake(doc), new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs),
            new AllowAllAuthenticator());
        var refused = new List<long>();
        s.OnCannotReach += (_, target) => refused.Add(target);
        s.SpawnPlayer(0, "a", "Ari");
        s.Enqueue(0, 0, TileCommand.Interact(booth.Id, TileMoveMode.Run));
        s.Tick(Dt);
        Assert.Single(refused);
        Assert.Equal(booth.Id, refused[0]);
    }

    [Fact]
    public void A_pending_action_that_never_converges_is_refused_once_it_outlives_its_cap()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileObject booth = doc.AddObject("bank_booth", 12, 10, 0, 0);
        // Kept, because this IS the map the simulator steps: an edit made into it is seen by the next step, which
        // is the only way to put a blocker in front of a walking player.
        TileCollisionMap map = TileMoveSimulatorTests.Bake(doc);
        var hub = new InMemoryTransportHub();
        // Both knobs shrunk on purpose. The cap is MaxRouteSteps * (slowest step + 1), so at the defaults it is
        // 1024 ticks and a test would have to walk all of them.
        using var s = new TileWorldServer(hub.Server,
            TileWorldServerTickTests.Config(new TileCoord(4, 10, 0)) with
            {
                StepTicks = new TileStepTicks(walk: 1, run: 1),
                Move = new TileMoveOptions { MaxRouteSteps = 8 },
            },
            map, new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator());
        var raised = new List<long>();
        var refused = new List<long>();
        s.OnInteract += (_, _, target) => raised.Add(target);
        s.OnCannotReach += (_, target) => refused.Add(target);
        s.SpawnPlayer(0, "a", "Ari");
        s.Enqueue(0, 0, TileCommand.Interact(booth.Id, TileMoveMode.Walk));
        s.Tick(Dt);

        // ONE blocker at a time, moved each tick onto the tile the player is about to step into. Leaving them all
        // blocked walls the player in within a few ticks, the re-path then finds nothing and drops the target, and
        // the run lands on the SIBLING branch instead of on the cap. Cleared and moved, the detour is always open,
        // the route is rebuilt rather than emptied, and the action's age climbs past the ceiling.
        TileCoord? previous = null;
        TileMoveState walking = default;
        for (int i = 0; i < 60 && refused.Count == 0; i++)
        {
            Assert.True(s.TryGetPlayerState(0, out walking));
            if (!walking.Route.IsIdle)
            {
                if (previous is TileCoord p) map.Clear(new TileRect(p.X, p.Z, 1, 1), 0);
                map.Or(walking.Route.Next.X, walking.Route.Next.Z, 0, TileCollisionFlags.Blocked);
                previous = walking.Route.Next;
            }
            s.Tick(Dt);
        }

        // The CAP branch rather than its sibling: on the tick before the refusal the route was still alive AND the
        // state still named the target, which is exactly the state the target-dropped branch cannot be in.
        Assert.False(walking.Route.IsIdle);
        Assert.Equal(booth.Id, walking.InteractTarget);
        Assert.Equal(new[] { booth.Id }, refused.ToArray());
        Assert.Empty(raised);
        Assert.Equal(0, s.Actions.PendingCount);
        // The refusal takes the state's own record of the target with it, so the walk it refused cannot turn the
        // player toward the booth if it later happens to end on a reach tile.
        Assert.True(s.TryGetPlayerState(0, out TileMoveState after));
        Assert.Equal(0, after.InteractTarget);
    }

    [Fact]
    public void A_truncated_interact_route_is_refused_rather_than_raised_from_across_the_map()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileObject booth = doc.AddObject("bank_booth", 30, 10, 0, 0);
        var hub = new InMemoryTransportHub();
        using var s = new TileWorldServer(hub.Server,
            TileWorldServerTickTests.Config(new TileCoord(4, 10, 0)) with
                { Move = new TileMoveOptions { MaxRouteSteps = 3 } },
            TileMoveSimulatorTests.Bake(doc), new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs),
            new AllowAllAuthenticator());
        var raised = new List<long>();
        var refused = new List<long>();
        s.OnInteract += (_, _, target) => raised.Add(target);
        s.OnCannotReach += (_, target) => refused.Add(target);
        s.SpawnPlayer(0, "a", "Ari");
        s.Enqueue(0, 0, TileCommand.Interact(booth.Id, TileMoveMode.Run));
        for (int i = 0; i < 20; i++) s.Tick(Dt);

        // The route to the booth's reach tile is 25 steps and the cap cuts it at 3, so the walk ends 22 tiles
        // short of it. Raising the interaction there is a reach check a client aims by picking the target with the
        // longest path, so the answer has to be the refusal instead.
        Assert.True(s.TryGetPlayerState(0, out TileMoveState st));
        Assert.Equal(new TileCoord(7, 10, 0), st.Tile);
        Assert.Empty(raised);
        Assert.Equal(new[] { booth.Id }, refused.ToArray());
    }

    [Fact]
    public void A_target_that_stops_resolving_mid_walk_is_refused_rather_than_raised()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileObject booth = doc.AddObject("bank_booth", 10, 10, 0, 0);
        var targets = new RevocableTargets(new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs));
        var hub = new InMemoryTransportHub();
        using var s = new TileWorldServer(hub.Server, TileWorldServerTickTests.Config(new TileCoord(5, 10, 0)),
            TileMoveSimulatorTests.Bake(doc), targets, new AllowAllAuthenticator());
        var raised = new List<long>();
        var refused = new List<long>();
        s.OnInteract += (_, _, target) => raised.Add(target);
        s.OnCannotReach += (_, target) => refused.Add(target);
        s.SpawnPlayer(0, "a", "Ari");
        s.Enqueue(0, 0, TileCommand.Interact(booth.Id, TileMoveMode.Run));
        s.Tick(Dt);
        s.Tick(Dt);
        // Deleted while the player is still walking to it, which is the other door out of FaceTarget: the walk
        // ends on what WAS a reach tile, of an object that no longer exists.
        targets.Resolves = false;
        for (int i = 0; i < 20; i++) s.Tick(Dt);

        Assert.Empty(raised);
        Assert.Equal(new[] { booth.Id }, refused.ToArray());
    }

    [Fact]
    public void A_drain_notices_everyone_then_completes_after_its_grace()
    {
        (TileWorldServer s, _, _) = Up(new TileCoord(4, 4, 0));
        using (s)
        {
            s.SpawnPlayer(0, "a", "Ari");
            Assert.False(s.IsDraining);
            s.BeginDrain(TileServerReason.Draining, graceSeconds: 0.5f);
            Assert.True(s.IsDraining);
            Assert.False(s.IsDrainComplete);
            s.Tick(Dt);
            s.Tick(Dt);
            Assert.True(s.IsDrainComplete);
        }
    }

    [Fact]
    public void A_zero_grace_drain_is_not_complete_until_the_tick_that_closes_its_sessions()
    {
        (TileWorldServer s, _, _) = Up(new TileCoord(4, 4, 0));
        using (s)
        {
            var left = new List<string>();
            s.SpawnPlayer(0, "a", "Ari");
            s.PlayerLeaving += (_, account, _) => left.Add(account);

            s.BeginDrain(TileServerReason.Draining, graceSeconds: 0f);
            // The grace is spent the moment it was asked for, but nothing has been released yet. A head that read
            // completion here would flush and exit having saved nothing anyone did this session.
            Assert.True(s.IsDraining);
            Assert.False(s.IsDrainComplete);
            Assert.Empty(left);
            Assert.Equal(1, s.PlayerCount);

            s.Tick(Dt);
            Assert.True(s.IsDrainComplete);
            Assert.Equal(new[] { "a" }, left.ToArray());
            Assert.Equal(0, s.PlayerCount);
        }
    }

    [Fact]
    public void A_connection_arriving_after_a_completed_drain_is_dropped_rather_than_seated()
    {
        (TileWorldServer s, InMemoryTransportHub hub, _) = Up(new TileCoord(4, 4, 0));
        using (s)
        {
            s.BeginDrain(TileServerReason.Draining, graceSeconds: 0f);
            s.Tick(Dt);
            Assert.True(s.IsDrainComplete);

            var joined = new List<int>();
            s.PlayerJoined += (slot, _) => joined.Add(slot);
            Hello(hub.CreateClient(), "late");
            s.Poll();

            // The close latches, so a seat handed out now would never be released by anything.
            Assert.Empty(joined);
            Assert.Equal(0, s.PlayerCount);
        }
    }

    [Fact]
    public void A_completed_drain_releases_every_player_and_forgets_their_pending_actions()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileObject booth = doc.AddObject("bank_booth", 20, 10, 0, 0);
        var hub = new InMemoryTransportHub();
        using var s = new TileWorldServer(hub.Server, TileWorldServerTickTests.Config(new TileCoord(5, 10, 0)),
            TileMoveSimulatorTests.Bake(doc), new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs),
            new AllowAllAuthenticator());
        var left = new List<string>();
        s.SpawnPlayer(0, "a", "Ari");
        s.SpawnPlayer(1, "b", "Bex");
        s.PlayerLeaving += (_, account, _) => left.Add(account);
        s.Enqueue(0, 0, TileCommand.Interact(booth.Id, TileMoveMode.Walk));
        s.Tick(Dt);
        Assert.Equal(1, s.Actions.PendingCount);        // still walking to the booth when the drain starts

        s.BeginDrain(TileServerReason.Draining, graceSeconds: 0.25f);
        s.Tick(Dt);
        Assert.True(s.IsDrainComplete);
        // Every player leaves by the ordinary path, so a persistence layer files their final state, and the seat
        // they vacate keeps nothing: an action left behind would fire against whoever recycles the slot.
        Assert.Equal(new[] { "a", "b" }, left.OrderBy(a => a).ToArray());
        Assert.Equal(0, s.PlayerCount);
        Assert.Empty(s.JoinedSlots);
        Assert.Equal(0, s.Actions.PendingCount);
    }

    [Fact]
    public void Spawning_onto_an_occupied_slot_releases_the_account_that_held_it()
    {
        (TileWorldServer s, _, _) = Up(new TileCoord(4, 4, 0));
        using (s)
        {
            var left = new List<(string Account, TileCoord Tile)>();
            long first = s.SpawnPlayer(0, "acct-old", "Old");
            s.SetPlayerState(0, TileMoveState.At(new TileCoord(9, 12, 0), TileDirection.N));
            s.PlayerLeaving += (_, account, state) => left.Add((account, state.Tile));

            // The seat recycled without its Left ever being observed, which is what a dropped session event leaves
            // behind. The account that held it still has to leave by the ordinary path.
            long second = s.SpawnPlayer(0, "acct-new", "New");

            Assert.Equal(new[] { ("acct-old", new TileCoord(9, 12, 0)) }, left.ToArray());
            Assert.NotEqual(first, second);
            // And its entity is gone rather than orphaned in the cell that owned it, where nothing would point at
            // it and every player in interest would keep being served it.
            Assert.False(s.Host.TryGetOwner(first, out _, out _));
            Assert.True(s.Host.TryGetOwner(second, out _, out _));
            Assert.Equal(1, s.PlayerCount);
        }
    }

    [Fact]
    public void A_leaving_player_raises_its_final_state_and_frees_the_slot()
    {
        (TileWorldServer s, InMemoryTransportHub hub, INetTransport c) = Up(new TileCoord(4, 4, 0));
        using (s)
        {
            var net = new NetClient(c, Array.Empty<byte>());
            net.Poll();
            s.Poll();
            TileMoveState? final = null;
            s.PlayerLeaving += (_, _, state) => final = state;
            // Moved off the spawn first, or the assertion below passes just as well on a leave that handed back
            // TileMoveState.At(config.Spawn) instead of the state the player was actually standing on.
            s.SetPlayerState(s.JoinedSlots.Single(), TileMoveState.At(new TileCoord(9, 12, 0), TileDirection.N));
            hub.DisconnectClient(c);
            s.Poll();
            Assert.NotNull(final);
            Assert.Equal(new TileCoord(9, 12, 0), final!.Value.Tile);
            Assert.Equal(0, s.PlayerCount);
            Assert.Empty(s.JoinedSlots);
        }
    }

    // An ITileTargets whose answers can be switched off part way through a walk, which is what deleting or
    // despawning an object does to every id it had. Wraps the real seam rather than replacing it, so the resolving
    // case is still the shipped answer.
    sealed class RevocableTargets : ITileTargets
    {
        readonly ITileTargets inner;

        internal RevocableTargets(ITileTargets inner) => this.inner = inner;

        internal bool Resolves { get; set; } = true;

        public bool TryGetFootprint(long target, out TileRect footprint, out int plane)
        {
            footprint = default;
            plane = 0;
            return Resolves && inner.TryGetFootprint(target, out footprint, out plane);
        }
    }
}
