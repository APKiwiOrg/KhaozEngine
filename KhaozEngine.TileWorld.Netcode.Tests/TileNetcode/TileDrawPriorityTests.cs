using System;
using System.Collections.Generic;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// ONE BODY PER TILE, the presentation rule <see cref="TileDrawPriority"/> applies. The local player owns their
/// own tile and everywhere else the highest net id wins, which is the OSRS PID ruling with a stable key.
/// <para>The pure cases run against <see cref="TileDrawPriority.Select"/> and its instance wrapper, because the
/// rule is a function of a roster and asserting it through a session would only test the session. The last two
/// run the REAL client over a loopback, which is what says the roster the convenience builds is the one the rule
/// was designed against: the local player's PREDICTED tile and each remote's COMMITTED one.</para>
/// </summary>
public class TileDrawPriorityTests
{
    static (long NetId, TileCoord Tile)[] Actors(params (long NetId, TileCoord Tile)[] actors) => actors;

    [Fact]
    public void The_local_player_wins_their_own_tile_against_a_higher_net_id()
    {
        var tile = new TileCoord(10, 10, 0);
        var priority = new TileDrawPriority();

        priority.Rebuild(localNetId: 4, tile, Actors((99, tile)));

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
        priority.Rebuild(localNetId: 4, new TileCoord(10, 10, 0), Actors((7, crowd), (31, crowd), (12, crowd)));

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

        priority.Rebuild(localNetId: 4, new TileCoord(10, 10, 0), Actors((7, alone)));

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

        priority.Rebuild(localNetId: 4, new TileCoord(10, 10, 0),
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

        priority.Rebuild(localNetId: 4, new TileCoord(10, 10, 0), Actors((99, new TileCoord(10, 10, 1))));

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

        priority.Rebuild(4, local, Actors((7, shared), (31, shared)));
        Assert.False(priority.IsDrawn(7));

        priority.Rebuild(4, local, Actors((7, shared), (31, away)));

        Assert.True(priority.IsDrawn(7));
        Assert.True(priority.IsDrawn(31));
        Assert.Equal(3, priority.Count);
    }

    // No local player at all, which is the pre-join client: nothing claims localTile and the crowd settles itself.
    [Fact]
    public void A_negative_local_net_id_claims_no_tile()
    {
        var tile = new TileCoord(10, 10, 0);
        var priority = new TileDrawPriority();

        priority.Rebuild(localNetId: -1, tile, Actors((7, tile), (31, tile)));

        Assert.True(priority.IsDrawn(31));
        Assert.False(priority.IsDrawn(7));
        Assert.False(priority.IsDrawn(-1));
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

        TileDrawPriority.Select(localNetId: -1, default, new (long, TileCoord)[] { (7, tile) }, winners, drawn);

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
