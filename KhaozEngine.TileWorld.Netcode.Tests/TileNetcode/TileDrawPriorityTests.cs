using System;
using System.Collections.Generic;
using KhaozEngine.Replication;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// ONE BODY PER TILE, the presentation rule <see cref="TileDrawPriority"/> applies. The local player owns their
/// own tile, and both tiles of a step in flight, while everywhere else the highest net id wins, which is the OSRS
/// PID ruling with a stable key.
/// <para>The pure cases run against <see cref="TileDrawPriority.Select"/> and its instance wrapper, because the
/// rule is a function of a roster and asserting it through a session would only test the session. The ones at the
/// end run the REAL client over a loopback, which is what says the roster the convenience builds is the one the
/// rule was designed against: the local player's PREDICTED tile, the tile their step is leaving, and each
/// remote's COMMITTED one.</para>
/// </summary>
public class TileDrawPriorityTests
{
    static (long NetId, TileCoord Tile)[] Actors(params (long NetId, TileCoord Tile)[] actors) => actors;

    [Fact]
    public void The_local_player_wins_their_own_tile_against_a_higher_net_id()
    {
        var tile = new TileCoord(10, 10, 0);
        var priority = new TileDrawPriority();

        priority.Rebuild(localNetId: 4, tile, localLeaving: null, Actors((99, tile)));

        Assert.True(priority.IsDrawn(4));
        Assert.False(priority.IsDrawn(99));
        Assert.Equal(1, priority.Count);
        Assert.True(priority.TryGetDrawn(tile, out long drawn));
        Assert.Equal(4L, drawn);
    }

    [Fact]
    public void The_highest_net_id_wins_a_tile_the_local_player_is_not_on()
    {
        var crowd = new TileCoord(12, 10, 0);
        var priority = new TileDrawPriority();

        // Deliberately not in id order, because the rule is a max rather than a last-one-wins.
        priority.Rebuild(localNetId: 4, new TileCoord(10, 10, 0), localLeaving: null,
            Actors((7, crowd), (31, crowd), (12, crowd)));

        Assert.True(priority.IsDrawn(31));
        Assert.False(priority.IsDrawn(7));
        Assert.False(priority.IsDrawn(12));
        Assert.True(priority.IsDrawn(4));                       // and the local player still has their own tile
        Assert.Equal(2, priority.Count);
    }

    [Fact]
    public void A_lone_actor_on_a_tile_is_drawn()
    {
        var priority = new TileDrawPriority();
        var alone = new TileCoord(20, 20, 0);

        priority.Rebuild(localNetId: 4, new TileCoord(10, 10, 0), localLeaving: null, Actors((7, alone)));

        Assert.True(priority.IsDrawn(7));
        Assert.True(priority.TryGetDrawn(alone, out long drawn));
        Assert.Equal(7L, drawn);
        // An id nobody handed in is not drawn, which is the same answer a hidden body gets: neither has a pose.
        Assert.False(priority.IsDrawn(8));
        Assert.False(priority.TryGetDrawn(new TileCoord(21, 20, 0), out _));
    }

    // The plane is part of the tile, so the same x and z one storey up is a different tile and hides nothing. A
    // rule keyed on (x, z) alone would draw one body for a whole tower, which is the failure this pins.
    [Fact]
    public void The_same_x_and_z_on_another_plane_is_a_different_tile_and_both_are_drawn()
    {
        var priority = new TileDrawPriority();

        priority.Rebuild(localNetId: 4, new TileCoord(10, 10, 0), localLeaving: null,
            Actors((7, new TileCoord(12, 10, 0)), (31, new TileCoord(12, 10, 1))));

        Assert.True(priority.IsDrawn(7));
        Assert.True(priority.IsDrawn(31));
        Assert.Equal(3, priority.Count);
    }

    // The local player's own tile is a tile like any other in this respect: a remote one floor up from them is not
    // stacked on them and is drawn.
    [Fact]
    public void A_remote_on_the_local_players_x_and_z_one_plane_up_is_still_drawn()
    {
        var priority = new TileDrawPriority();

        priority.Rebuild(localNetId: 4, new TileCoord(10, 10, 0), localLeaving: null,
            Actors((99, new TileCoord(10, 10, 1))));

        Assert.True(priority.IsDrawn(4));
        Assert.True(priority.IsDrawn(99));
    }

    // A rebuild is FROM SCRATCH, which is the property that makes a per-frame call correct. The hidden body has to
    // come back the moment the actor that was hiding it steps off, with no per-actor bookkeeping anywhere.
    [Fact]
    public void A_remote_that_leaves_a_tile_restores_the_one_it_was_hiding()
    {
        var shared = new TileCoord(12, 10, 0);
        var away = new TileCoord(13, 10, 0);
        var local = new TileCoord(10, 10, 0);
        var priority = new TileDrawPriority();

        priority.Rebuild(4, local, localLeaving: null, Actors((7, shared), (31, shared)));
        Assert.False(priority.IsDrawn(7));

        priority.Rebuild(4, local, localLeaving: null, Actors((7, shared), (31, away)));

        Assert.True(priority.IsDrawn(7));
        Assert.True(priority.IsDrawn(31));
        Assert.Equal(3, priority.Count);
    }

    // No local player at all, which is the pre-join client: nothing claims localTile and the crowd settles itself.
    [Fact]
    public void The_sentinel_local_net_id_claims_no_tile()
    {
        var tile = new TileCoord(10, 10, 0);
        var priority = new TileDrawPriority();

        priority.Rebuild(TileDrawPriority.NoLocalPlayer, tile, localLeaving: null, Actors((7, tile), (31, tile)));

        Assert.True(priority.IsDrawn(31));
        Assert.False(priority.IsDrawn(7));
        Assert.False(priority.IsDrawn(TileDrawPriority.NoLocalPlayer));
        Assert.Equal(1, priority.Count);
    }

    // THE STEP'S OTHER TILE. A step commits its destination on the tick it starts and the body glides in over the
    // rest of it, so a local player claiming the destination alone leaves the tile they are walking out of, with
    // their own body still on it, to the highest net id standing there. Both tiles are claimed instead.
    [Fact]
    public void The_local_player_claims_the_tile_they_are_stepping_out_of()
    {
        var leaving = new TileCoord(9, 10, 0);
        var entering = new TileCoord(10, 10, 0);
        var priority = new TileDrawPriority();

        priority.Rebuild(localNetId: 50, entering, leaving, Actors((99, leaving)));

        Assert.True(priority.IsDrawn(50));
        Assert.False(priority.IsDrawn(99));                     // the higher id loses the tile it is standing on
        Assert.True(priority.TryGetDrawn(leaving, out long behind));
        Assert.Equal(50L, behind);
        Assert.True(priority.TryGetDrawn(entering, out long ahead));
        Assert.Equal(50L, ahead);
        // TWO tiles, ONE body: Drawn is a set of net ids, which is why Count is not a tile count.
        Assert.Equal(1, priority.Count);

        // And the claim lasts exactly the step: the tile is settled by net id again the moment the body lands.
        priority.Rebuild(localNetId: 50, entering, localLeaving: null, Actors((99, leaving)));

        Assert.True(priority.IsDrawn(50));
        Assert.True(priority.IsDrawn(99));
        Assert.True(priority.TryGetDrawn(leaving, out behind));
        Assert.Equal(99L, behind);
    }

    // A packed net id from a high node is NEGATIVE, so "no local player" is a sentinel rather than a sign. Gating
    // on `>= 0` would drop the local player's own claim for every node from 32768 up, which is the one actor this
    // rule may never lose.
    [Fact]
    public void A_packed_negative_net_id_still_claims_the_local_players_tiles()
    {
        long packed = NetIdAllocator.Pack(nodeId: 40000, counter: 1);
        Assert.True(packed < 0, "the premise: a high node id packs to a negative net id");

        var leaving = new TileCoord(9, 10, 0);
        var entering = new TileCoord(10, 10, 0);
        var priority = new TileDrawPriority();

        priority.Rebuild(packed, entering, leaving, Actors((99, entering), (7, leaving)));

        Assert.True(priority.IsDrawn(packed));
        Assert.False(priority.IsDrawn(99));
        Assert.False(priority.IsDrawn(7));
        Assert.Equal(1, priority.Count);
    }

    // The static core with the caller's own buffers, which is what the instance methods are over the top of. Both
    // are cleared on entry, so a caller reusing dirty buffers gets this rebuild's answer and not a union with the
    // last one.
    [Fact]
    public void The_static_core_clears_the_buffers_it_is_handed()
    {
        var tile = new TileCoord(5, 5, 0);
        var winners = new Dictionary<TileCoord, long> { [new TileCoord(99, 99, 0)] = 1234 };
        var drawn = new HashSet<long> { 1234 };

        TileDrawPriority.Select(TileDrawPriority.NoLocalPlayer, default, localLeaving: null,
            new (long, TileCoord)[] { (7, tile) }, winners, drawn);

        Assert.Equal(new[] { 7L }, drawn);
        Assert.Single(winners);
        Assert.Equal(7L, winners[tile]);
    }

    // THE PLAYTEST VERDICT ITSELF, through the real client: a body standing on the local player's tile shows the
    // player and nothing else. The stack is built on the SERVER and read back through the client's own roster, so
    // this is the one test that says the convenience reads the predicted local tile and the committed remote one
    // rather than something that merely agrees with them in a made-up roster.
    [Fact]
    public void A_remote_standing_on_the_local_player_is_hidden_behind_them_through_the_real_client()
    {
        using var loop = new TileRemoteReadTests.Loop();
        loop.Join();
        long remote = loop.Server.SpawnPlayer(slot: 1, "remote", "Rem");
        // The local player spawns at (10, 10, 0), and a net id is handed out in increasing order, so this remote
        // is ABOVE them on the raw rule and only the local-player clause can hide it.
        loop.Server.SetPlayerState(1, TileMoveState.At(new TileCoord(10, 10, 0), TileDirection.S));
        loop.Frames(24);

        Assert.True(remote > loop.Client.LocalNetId, "the remote must outrank the local player for this to bite");
        Assert.True(loop.Client.TryGetRemoteTile(remote, out TileCoord theirs));
        Assert.Equal(loop.Client.Prediction.PredictedState.Tile, theirs);   // genuinely stacked

        var priority = new TileDrawPriority();
        priority.Rebuild(loop.Client);

        Assert.True(priority.IsDrawn(loop.Client.LocalNetId));
        Assert.False(priority.IsDrawn(remote));
        Assert.Equal(1, priority.Count);
        Assert.True(priority.TryGetDrawn(theirs, out long shown));
        Assert.Equal(loop.Client.LocalNetId, shown);
    }

    // THE SAME VERDICT WHILE WALKING, through the real client, which is the case the rule missed while it claimed
    // one tile. A step commits its destination on the tick it starts, so for the whole of the step the drawn body
    // is still leaving a tile somebody else can be standing on, and at commit the two bodies draw at the same
    // world position. The remote outranks the player on the raw rule and must still be hidden.
    [Fact]
    public void A_remote_on_the_tile_the_local_player_is_stepping_out_of_is_hidden_through_the_real_client()
    {
        using var loop = new TileRemoteReadTests.Loop();
        loop.Join();
        long remote = loop.Server.SpawnPlayer(slot: 1, "remote", "Rem");
        TileCoord origin = loop.Client.Prediction.PredictedState.Tile;
        loop.Server.SetPlayerState(1, TileMoveState.At(origin, TileDirection.S));
        loop.Frames(24);
        Assert.True(remote > loop.Client.LocalNetId, "the remote must outrank the local player for this to bite");

        // Walk away, and stop in the MIDDLE of the first step: the body is between the two tiles, and the one it
        // is walking out of is the remote's.
        loop.Client.Queue(
            TileCommand.WalkTo(new TileCoord(origin.X, origin.Z + 4, origin.Plane), TileMoveMode.Walk));
        TileMoveState local = default;
        for (int i = 0; i < 300; i++)
        {
            loop.Step();
            local = loop.Client.Prediction.PredictedState;
            if (local.IsStepping && local.StepFrom.Equals(origin) && local.StepTicks * 2 >= local.StepTotal) break;
        }

        Assert.True(local.IsStepping, "the local player never started a step to be judged mid-flight");
        Assert.Equal(origin, local.StepFrom);
        Assert.NotEqual(origin, local.Tile);
        Assert.True(loop.Client.TryGetRemoteTile(remote, out TileCoord theirs));
        Assert.Equal(origin, theirs);                            // the remote is on the tile being vacated

        var priority = new TileDrawPriority();
        priority.Rebuild(loop.Client);

        Assert.True(priority.IsDrawn(loop.Client.LocalNetId));
        Assert.False(priority.IsDrawn(remote));
        Assert.Equal(1, priority.Count);                         // two tiles, one body
        Assert.True(priority.TryGetDrawn(origin, out long shown));
        Assert.Equal(loop.Client.LocalNetId, shown);
        Assert.True(priority.TryGetDrawn(local.Tile, out shown));
        Assert.Equal(loop.Client.LocalNetId, shown);
    }

    // Two server-owned bodies on ONE tile, neither of them the local player: exactly one is drawn, and it is the
    // higher net id.
    [Fact]
    public void Two_remotes_on_one_tile_draw_exactly_the_higher_net_id_through_the_real_client()
    {
        using var loop = new TileRemoteReadTests.Loop();
        loop.Join();
        long first = loop.Server.SpawnPlayer(slot: 1, "one", "One");
        long second = loop.Server.SpawnPlayer(slot: 2, "two", "Two");
        Assert.True(second > first, "net ids are handed out in increasing order");

        var shared = new TileCoord(12, 10, 0);
        loop.Server.SetPlayerState(1, TileMoveState.At(shared, TileDirection.S));
        loop.Server.SetPlayerState(2, TileMoveState.At(shared, TileDirection.S));
        loop.Frames(24);

        Assert.Contains(first, loop.Client.RemoteNetIds);
        Assert.Contains(second, loop.Client.RemoteNetIds);

        var priority = new TileDrawPriority();
        priority.Rebuild(loop.Client);

        Assert.True(priority.IsDrawn(second));
        Assert.False(priority.IsDrawn(first));
        Assert.True(priority.IsDrawn(loop.Client.LocalNetId));   // on their own tile, one square away
        Assert.Equal(2, priority.Count);

        // And the hidden one comes back when it steps off, with no bookkeeping between the two rebuilds.
        loop.Server.SetPlayerState(2, TileMoveState.At(new TileCoord(14, 10, 0), TileDirection.S));
        loop.Frames(24);
        priority.Rebuild(loop.Client);

        Assert.True(priority.IsDrawn(first));
        Assert.True(priority.IsDrawn(second));
        Assert.Equal(3, priority.Count);
    }

    // The collect door the convenience is built on, pinned on its own: it answers what TryGetRemoteTile answers,
    // for everybody, and it never carries the local player.
    [Fact]
    public void The_collect_door_hands_back_every_remote_on_its_committed_tile()
    {
        using var loop = new TileRemoteReadTests.Loop();
        loop.Join();
        long remote = loop.Server.SpawnPlayer(slot: 1, "remote", "Rem");
        loop.Server.SetPlayerState(1, TileMoveState.At(new TileCoord(12, 10, 0), TileDirection.S));
        loop.Frames(24);

        var buffer = new List<(long NetId, TileCoord Tile)> { (1234, new TileCoord(99, 99, 0)) };
        loop.Client.CollectRemoteTiles(buffer);

        Assert.Single(buffer);                                   // cleared first, so the junk entry is gone
        Assert.Equal(remote, buffer[0].NetId);
        Assert.True(loop.Client.TryGetRemoteTile(remote, out TileCoord expected));
        Assert.Equal(expected, buffer[0].Tile);
        Assert.DoesNotContain(buffer, a => a.NetId == loop.Client.LocalNetId);
    }
}


/// <summary>
/// THE WEIGHTS, which are the same rule spread over the step the body is actually walking. A tile commits when a
/// step STARTS, so an actor judged the frame its tile changes is judged a whole step before its body arrives, and
/// the hard boolean this class used to answer made that visible as a pop: enemies walking onto the player vanished
/// in the open. The cases here pin the crossing itself, and they run against the roster overloads, because the
/// pace is a function of a roster and a progress. The two at the end run the REAL client, which is what says the
/// progress the priority is paced by is the one the body is drawn at.
/// </summary>
public class TileDrawPriorityFadeTests
{
    const float Frame = 1f / 60f;

    static (long NetId, TileCoord Tile, float StepProgress)[] Steps(
        params (long NetId, TileCoord Tile, float StepProgress)[] actors) => actors;

    static readonly TileCoord Local = new(0, 0, 0);

    // THE OWNER'S COMPLAINT, in one test. A body walking onto an occupied tile loses that tile on the frame the
    // step commits, and its own body is a whole step away from it at that moment. So the weight is still 1 as the
    // step starts, falls with the walk, and reaches 0 exactly as the body comes to rest under the winner.
    [Fact]
    public void A_body_stepping_onto_an_occupied_tile_falls_from_one_to_zero_across_the_step()
    {
        var busy = new TileCoord(12, 10, 0);
        var behind = new TileCoord(11, 10, 0);
        var priority = new TileDrawPriority();

        // Standing apart, both drawn, so the fall below is a CHANGE rather than a first sighting.
        priority.Rebuild(4, Local, null, Steps((7, behind, 1f), (31, busy, 1f)), Frame);
        Assert.Equal(1f, priority.Weight(7), 5);

        // The step commits: 7 is judged on busy from here, and its body has not moved yet.
        priority.Rebuild(4, Local, null, Steps((7, busy, 0f), (31, busy, 1f)), Frame);
        Assert.Equal(1f, priority.Weight(7), 5);
        Assert.True(priority.IsDrawn(7), "hidden on the frame the tile changed is the pop this removes");

        float last = 1f;
        foreach (float progress in new[] { 0.2f, 0.4f, 0.6f, 0.8f })
        {
            priority.Rebuild(4, Local, null, Steps((7, busy, progress), (31, busy, 1f)), Frame);
            float weight = priority.Weight(7);
            Assert.True(weight < last, $"the weight did not fall at {progress}: {last} then {weight}");
            // It rides the STEP, so the weight is what is left of the walk into the tile it lost.
            Assert.Equal(1f - progress, weight, 4);
            Assert.Equal(1f, priority.Weight(31), 5);       // the winner is untouched throughout
            last = weight;
        }

        priority.Rebuild(4, Local, null, Steps((7, busy, 1f), (31, busy, 1f)), Frame);
        Assert.Equal(0f, priority.Weight(7), 5);            // gone the moment it comes to rest there
        Assert.False(priority.IsDrawn(7));
        Assert.Equal(1f, priority.Weight(31), 5);
        Assert.True(priority.TryGetDrawn(busy, out long owner));
        Assert.Equal(31L, owner);                           // and the tile was never owned by anyone else
    }

    // A body that loses while STANDING STILL has no step to spend the fade across, because it is not the one that
    // moved. The winner walked onto it, or teleported onto it, so the fixed window takes over.
    [Fact]
    public void A_body_that_loses_a_tile_at_rest_fades_over_the_fixed_window()
    {
        var shared = new TileCoord(12, 10, 0);
        var priority = new TileDrawPriority { FadeSeconds = 0.2f };
        Assert.Equal(0.2f, priority.FadeSeconds, 5);

        priority.Rebuild(4, Local, null, Steps((7, shared, 1f)), Frame);
        Assert.Equal(1f, priority.Weight(7), 5);

        // 31 arrives on the tile already at rest, so nothing is stepping and the window is the only clock.
        foreach (float expected in new[] { 0.75f, 0.5f, 0.25f, 0f })
        {
            priority.Rebuild(4, Local, null, Steps((7, shared, 1f), (31, shared, 1f)), dt: 0.05f);
            Assert.Equal(expected, priority.Weight(7), 4);
            Assert.Equal(expected > 0f, priority.IsDrawn(7));
        }

        Assert.Equal(1f, priority.Weight(31), 5);           // the arrival is drawn whole from its first frame
    }

    // And out the other side. A body that walks out from under the winner rises across the step it is walking, so
    // it is whole again exactly as it lands clear rather than appearing on the tile it left.
    [Fact]
    public void A_body_stepping_off_a_lost_tile_rises_from_zero_to_one_across_the_step()
    {
        var shared = new TileCoord(12, 10, 0);
        var clear = new TileCoord(13, 10, 0);
        var priority = new TileDrawPriority { FadeSeconds = 0.1f };

        priority.Rebuild(4, Local, null, Steps((7, shared, 1f)), Frame);
        for (int i = 0; i < 4; i++)
            priority.Rebuild(4, Local, null, Steps((7, shared, 1f), (31, shared, 1f)), dt: 0.05f);
        Assert.Equal(0f, priority.Weight(7), 5);            // fully behind the winner before it moves

        float last = -1f;
        foreach (float progress in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
        {
            priority.Rebuild(4, Local, null, Steps((7, clear, progress), (31, shared, 1f)), Frame);
            float weight = priority.Weight(7);
            Assert.True(weight > last, $"the weight did not rise at {progress}: {last} then {weight}");
            Assert.Equal(progress, weight, 4);
            last = weight;
        }

        Assert.Equal(1f, priority.Weight(7), 5);
        Assert.Equal(1f, priority.Weight(31), 5);
    }

    // The winner never fades, whichever way the loser is going, and the local player never fades at all: they are
    // drawn unconditionally, so there is no state for their own body to cross.
    [Fact]
    public void The_winner_and_the_local_player_stay_at_one_throughout()
    {
        var shared = new TileCoord(12, 10, 0);
        var priority = new TileDrawPriority();

        priority.Rebuild(4, Local, null, Steps((7, shared, 1f), (31, shared, 1f)), Frame);
        for (int quarter = 0; quarter <= 4; quarter++)
        {
            priority.Rebuild(4, Local, null, Steps((7, shared, quarter / 4f), (31, shared, 1f)), Frame);
            Assert.Equal(1f, priority.Weight(31), 5);
            Assert.Equal(1f, priority.Weight(4), 5);
            Assert.True(priority.IsDrawn(4));
        }
    }

    // A body that leaves takes its crossing with it. The state is keyed by net id and swept in the rebuild that
    // stops listing the body, so a returning actor starts on its answer rather than resuming a fade from before it
    // left, which is the same rule a first sighting gets.
    [Fact]
    public void The_state_of_a_departed_body_is_pruned_rather_than_resumed()
    {
        var shared = new TileCoord(12, 10, 0);
        var priority = new TileDrawPriority { FadeSeconds = 0.2f };

        priority.Rebuild(4, Local, null, Steps((7, shared, 1f)), Frame);
        priority.Rebuild(4, Local, null, Steps((7, shared, 1f), (31, shared, 1f)), dt: 0.1f);
        Assert.Equal(0.5f, priority.Weight(7), 4);          // mid-crossing, which is the state worth losing

        priority.Rebuild(4, Local, null, Steps((31, shared, 1f)), Frame);
        Assert.Equal(0f, priority.Weight(7), 5);
        Assert.False(priority.IsDrawn(7));
        Assert.DoesNotContain(7L, priority.Drawn);

        // Back on a tile of its own. Resumed state would rise from 0.5 over the window, so a whole body here is
        // the sweep having happened.
        priority.Rebuild(4, Local, null, Steps((7, new TileCoord(20, 20, 0), 1f), (31, shared, 1f)), Frame);
        Assert.Equal(1f, priority.Weight(7), 5);
    }

    // The compatibility contract, on every frame of a crossing rather than at its ends.
    [Fact]
    public void IsDrawn_is_the_weight_above_zero_on_every_frame_of_a_crossing()
    {
        var busy = new TileCoord(12, 10, 0);
        var behind = new TileCoord(11, 10, 0);
        var priority = new TileDrawPriority();
        priority.Rebuild(4, Local, null, Steps((7, behind, 1f), (31, busy, 1f)), Frame);

        // Stepped as a whole number of tenths, because accumulating 0.1f overshoots and never lands on the end
        // of the step, which is the one frame this has to see.
        for (int tenth = 0; tenth <= 10; tenth++)
        {
            priority.Rebuild(4, Local, null, Steps((7, busy, tenth / 10f), (31, busy, 1f)), Frame);
            Assert.Equal(priority.Weight(7) > 0f, priority.IsDrawn(7));
            // A body being drawn is in the drawn set, faders included, so the two ways to ask cannot disagree.
            Assert.Equal(priority.IsDrawn(7), InDrawn(priority, 7L));
        }

        Assert.Equal(0f, priority.Weight(7), 5);
        Assert.False(InDrawn(priority, 7L));
        Assert.Equal(2, priority.Count);                    // the winner and the local player
    }

    // The overloads with no dt are the pre-weight rule verbatim, for a head that cannot fade a body at all. Every
    // weight lands on 0 or 1 in one frame, which is what keeps an existing caller working unchanged.
    [Fact]
    public void The_overloads_without_a_dt_cut_instead_of_fading()
    {
        var busy = new TileCoord(12, 10, 0);
        var behind = new TileCoord(11, 10, 0);
        var priority = new TileDrawPriority();

        priority.Rebuild(4, Local, null, Actors((7, behind), (31, busy)));
        Assert.Equal(1f, priority.Weight(7), 5);

        priority.Rebuild(4, Local, null, Actors((7, busy), (31, busy)));
        Assert.Equal(0f, priority.Weight(7), 5);            // no crossing, whatever its step is doing
        Assert.False(priority.IsDrawn(7));
        Assert.Equal(2, priority.Count);
    }

    static (long NetId, TileCoord Tile)[] Actors(params (long NetId, TileCoord Tile)[] actors) => actors;

    // Drawn is interface-typed, so it is walked rather than queried: the same reason the draw loop walks a
    // collected list instead of it.
    static bool InDrawn(TileDrawPriority priority, long netId)
    {
        foreach (long id in priority.Drawn)
            if (id == netId) return true;
        return false;
    }

    // A zero window cuts the cases that have no step to ride, and is the knob a head with no alpha at all sets.
    [Fact]
    public void A_zero_fade_window_cuts_a_body_that_loses_at_rest()
    {
        var shared = new TileCoord(12, 10, 0);
        var priority = new TileDrawPriority { FadeSeconds = 0f };

        priority.Rebuild(4, Local, null, Steps((7, shared, 1f)), Frame);
        priority.Rebuild(4, Local, null, Steps((7, shared, 1f), (31, shared, 1f)), Frame);

        Assert.Equal(0f, priority.Weight(7), 5);
    }

    [Fact]
    public void The_fade_window_defaults_to_a_quarter_second_and_refuses_a_negative_one()
    {
        var priority = new TileDrawPriority();
        Assert.Equal(TileDrawPriority.DefaultFadeSeconds, priority.FadeSeconds, 5);
        Assert.Equal(0.25f, TileDrawPriority.DefaultFadeSeconds, 5);

        Assert.Throws<ArgumentOutOfRangeException>(() => priority.FadeSeconds = -0.1f);
        Assert.Throws<ArgumentOutOfRangeException>(() => priority.FadeSeconds = float.NaN);
        Assert.Equal(TileDrawPriority.DefaultFadeSeconds, priority.FadeSeconds, 5);
    }

    // THROUGH THE REAL CLIENT, and this is the pair that says the priority is paced by the progress the BODY is
    // drawn at rather than by a number a roster made up. A remote placed on the local player's tile while standing
    // still crosses over the window, and is drawn the whole way.
    [Fact]
    public void A_remote_placed_on_the_local_player_fades_out_through_the_real_client()
    {
        using var loop = new TileRemoteReadTests.Loop();
        loop.Join();
        long remote = loop.Server.SpawnPlayer(slot: 1, "remote", "Rem");
        loop.Server.SetPlayerState(1, TileMoveState.At(new TileCoord(12, 10, 0), TileDirection.S));
        loop.Frames(24);

        var priority = new TileDrawPriority { FadeSeconds = 0.2f };
        priority.Rebuild(loop.Client, Frame);
        Assert.Equal(1f, priority.Weight(remote), 5);

        TileCoord mine = loop.Client.Prediction.PredictedState.Tile;
        loop.Server.SetPlayerState(1, TileMoveState.At(mine, TileDirection.S), teleport: true);
        loop.Frames(24);
        Assert.True(loop.Client.TryGetRemoteTile(remote, out TileCoord theirs));
        Assert.Equal(mine, theirs);                          // genuinely stacked, so it genuinely lost the tile

        foreach (float expected in new[] { 0.75f, 0.5f, 0.25f, 0f })
        {
            priority.Rebuild(loop.Client, dt: 0.05f);
            Assert.Equal(expected, priority.Weight(remote), 4);
            Assert.Equal(expected > 0f, priority.IsDrawn(remote));
        }

        Assert.Equal(1f, priority.Weight(loop.Client.LocalNetId), 5);
    }

    // And the walking case through the real client, which is the one the owner reported: a remote mid-step onto the
    // local player's tile is drawn part way through the whole walk in, on the same fraction its body glides at, and
    // is gone once that step has run out.
    [Fact]
    public void A_remote_walking_onto_the_local_player_is_drawn_the_whole_way_in()
    {
        using var loop = new TileRemoteReadTests.Loop();
        loop.Join();
        long remote = loop.Server.SpawnPlayer(slot: 1, "remote", "Rem");
        TileCoord mine = loop.Client.Prediction.PredictedState.Tile;
        var from = new TileCoord(mine.X + 1, mine.Z, mine.Plane);
        loop.Server.SetPlayerState(1, TileMoveState.At(from, TileDirection.W));
        loop.Frames(24);

        var priority = new TileDrawPriority();
        priority.Rebuild(loop.Client, Frame);
        Assert.Equal(1f, priority.Weight(remote), 5);

        // A step in flight onto the player: committed to their tile, body still a whole step away from it. The
        // server accepts this shape because it is one the simulator itself produces.
        TileMoveState stepping = TileMoveState.At(mine, TileDirection.W);
        stepping.StepFrom = from;
        stepping.StepTicks = 0;
        stepping.StepTotal = 4;
        loop.Server.SetPlayerState(1, stepping);

        bool sawTheStep = false, sawPartway = false;
        float last = 1f;
        for (int i = 0; i < 120; i++)
        {
            loop.Step();
            priority.Rebuild(loop.Client, Frame);
            float weight = priority.Weight(remote);
            Assert.True(weight <= last + 1e-4f, $"the weight rose mid-walk: {last} then {weight}");
            last = weight;
            if (!loop.Client.TryGetRemoteTile(remote, out TileCoord tile) || !tile.Equals(mine)) continue;
            sawTheStep = true;
            if (weight is > 0.05f and < 0.95f) sawPartway = true;
            if (weight == 0f) break;
        }

        Assert.True(sawTheStep, "the remote never reached the local player's tile");
        Assert.True(sawPartway, "the remote went straight from drawn to hidden, which is the pop this removes");
        Assert.Equal(0f, priority.Weight(remote), 5);
        Assert.False(priority.IsDrawn(remote));
        Assert.True(priority.TryGetDrawn(mine, out long owner));
        Assert.Equal(loop.Client.LocalNetId, owner);
    }

    // The bulk read the priority is paced by, pinned on its own: one call, the same tile CollectRemoteTiles gives
    // and the fraction the body is drawn at, with a standing remote reading as a completed step.
    [Fact]
    public void The_step_progress_reads_agree_with_the_collected_tiles()
    {
        using var loop = new TileRemoteReadTests.Loop();
        loop.Join();
        long remote = loop.Server.SpawnPlayer(slot: 1, "remote", "Rem");
        loop.Server.SetPlayerState(1, TileMoveState.At(new TileCoord(12, 10, 0), TileDirection.S));
        loop.Frames(24);

        var buffer = new List<(long NetId, TileCoord Tile, float StepProgress)>
            { (1234, new TileCoord(99, 99, 0), 0.5f) };
        loop.Client.CollectRemoteSteps(buffer);

        Assert.Single(buffer);                               // cleared first, so the junk entry is gone
        Assert.Equal(remote, buffer[0].NetId);
        Assert.True(loop.Client.TryGetRemoteTile(remote, out TileCoord expected));
        Assert.Equal(expected, buffer[0].Tile);
        Assert.Equal(1f, buffer[0].StepProgress, 5);         // standing, so the step into that tile is complete

        Assert.True(loop.Client.TryGetRemoteStepProgress(remote, out float progress));
        Assert.Equal(1f, progress, 5);
        Assert.False(loop.Client.TryGetRemoteStepProgress(loop.Client.LocalNetId, out _));
        Assert.False(loop.Client.TryGetRemoteStepProgress(123456789L, out _));
    }
}

/// <summary>
/// The allocation half, serialized in the AllocSensitive collection so byte counting is not disturbed by parallel
/// test threads, and split from <see cref="TileDrawPriorityTests"/> so the behavioural tests there keep the
/// assembly's parallelism. Same shape as <c>TileRemoteReadAllocationTests</c>, which carries the long note on why
/// the smallest of several passes is the honest reading.
/// </summary>
[Collection("AllocSensitive")]
public class TileDrawPriorityAllocationTests
{
    /// <summary>
    /// A rebuild costs nothing once its buffers have grown. It runs once a frame for the life of the session, so a
    /// rebuild that allocated would put the whole draw rule on the GC rather than on the frame.
    /// </summary>
    [Fact]
    public void A_rebuild_allocates_nothing_once_its_buffers_have_grown()
    {
        using var loop = new TileRemoteReadTests.Loop();
        loop.Join();
        loop.Server.SpawnPlayer(slot: 1, "one", "One");
        loop.Server.SpawnPlayer(slot: 2, "two", "Two");
        var shared = new TileCoord(12, 10, 0);
        loop.Server.SetPlayerState(1, TileMoveState.At(shared, TileDirection.S));
        loop.Server.SetPlayerState(2, TileMoveState.At(shared, TileDirection.S));
        loop.Frames(30);

        var priority = new TileDrawPriority();
        // Warm up the JIT AND grow every buffer: the list, the dictionary's buckets, the set's, and the one
        // ValueCollection the dictionary caches on first ask. That growth is the "after warm-up" the claim carries.
        for (int i = 0; i < 200; i++) priority.Rebuild(loop.Client);
        Assert.Equal(2, priority.Count);

        // THE BEST OF SEVERAL PASSES, for the reason the remote reads' own allocation test spells out: the thread
        // counter is only accurate to one allocation context, so a background collection retiring a context inside
        // the window charges this thread for bytes it never allocated. A rebuild that genuinely allocated would do
        // so on every pass and cannot hide behind the minimum.
        const int iterations = 5000;
        const int passes = 5;
        long allocated = long.MaxValue;
        for (int pass = 0; pass < passes; pass++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < iterations; i++) priority.Rebuild(loop.Client);
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(2, priority.Count);            // sanity: it really did rebuild a two tile answer every time
        Assert.True(allocated < 4096,
            $"the rebuild allocated {allocated} bytes over {iterations} frames, which is not a free rebuild");
    }
}
