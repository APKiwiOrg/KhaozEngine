using System.Collections.Generic;
using System.Text;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;
using static KhaozEngine.Tests.TileNetcode.TileCombatResolveTests;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// Spec section 13.3, the combat logout linger: a player in combat who disconnects is not removed at once, their
/// entity stays in world and attackable until the window lapses, and then persists and leaves through the ordinary
/// drain. Split from <see cref="TileCombatResolveTests"/> because these are SESSION-shaped (two accounts, a
/// transport drop, a reconnect) rather than hit-pipeline-shaped, and they share that file's fixture through the
/// <c>using static</c> above rather than keeping a second copy of it.
/// </summary>
public class TileCombatLingerTests
{
    const float Dt = 0.25f;

    // Connects one client and returns the slot it landed on plus its player's net id.
    static (int slot, long netId) Join(TileWorldServer s, InMemoryTransportHub hub, string account,
        out INetTransport transport)
    {
        transport = hub.CreateClient();
        var client = new NetClient(transport, Encoding.UTF8.GetBytes(account));
        client.Poll();
        s.Poll();
        foreach (int slot in s.JoinedSlots)
            if (s.TryGetAccountId(slot, out string held) && held == account)
            {
                Assert.True(s.TryGetPlayerNetId(slot, out long netId));
                return (slot, netId);
            }
        Assert.Fail($"no seat for {account}");
        return default;
    }

    // Section 13.3. A player in combat who disconnects is not removed at once: the entity LINGERS in world, still
    // attackable, until the window lapses, and then persists and leaves through the ordinary drain.
    //
    // The leave is driven by DROPPING THE TRANSPORT rather than by Kick, because Kick forces an immediate close:
    // an operator kick, a drain and a recycled seat all bypass the linger deliberately, since none of them is the
    // leaving player's decision. A dropped link is the one path that lingers, and it is the path the rule exists
    // for.
    [Fact]
    public void A_player_in_combat_who_leaves_lingers_attackable_and_then_drains_normally()
    {
        var hub = new InMemoryTransportHub();
        var rules = new FixedRules { Damage = 2, Ticks = 1 };
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(20, 21, 0), rules,
            combatLogoutTicks: 8);
        (int slot, long player) = Join(s, hub, "a", out INetTransport c);
        Assert.True(s.SetHealth(player, new TileHealth { Current = 100, Max = 100 }));
        long monster = s.SpawnActor(new TileCoord(20, 20, 0), new TileActorSpawn(100, 1, TileDirection.S));
        Lock(s, monster, player);
        // More than one tick, because a hit landing on tick zero is indistinguishable from never having been hit:
        // LastDamagedTick is zero for both.
        for (int i = 0; i < 3; i++) s.Tick(Dt);

        var left = new List<string>();
        s.PlayerLeaving += (_, account, _) => left.Add(account);

        hub.DisconnectClient(c);
        s.Poll();
        Assert.Empty(left);
        // The seat is still held, because the leave has been DEFERRED rather than run: the body is still stepped,
        // still served and still in the player index.
        Assert.Equal(1, s.PlayerCount);
        Assert.Equal(slot, Assert.Single(s.JoinedSlots));

        // The entity is still there and still being hit.
        Assert.True(s.TryGetHealth(player, out TileHealth before));
        for (int i = 0; i < 4; i++) s.Tick(Dt);
        Assert.True(s.TryGetHealth(player, out TileHealth during));
        Assert.True(during.Current < before.Current, "the lingering body is still attackable");
        Assert.Empty(left);

        for (int i = 0; i < 8; i++) s.Tick(Dt);
        Assert.Equal(new[] { "a" }, left);
        Assert.False(s.TryGetHealth(player, out _));
        Assert.Equal(0, s.PlayerCount);
    }

    // THE WINDOW ENDING IN A KILL rather than in a lapse, which is the whole point of leaving the body attackable
    // and is the one path nothing else covers. The death has to reach the game with the REAL slot (its connection
    // is gone, its seat is not), the corpse must not be rolled against for the rest of the window, and the linger
    // still has to expire on its own schedule and file the post-death state through the ordinary leave.
    [Fact]
    public void A_lingering_player_can_be_killed_and_the_window_still_expires_through_the_ordinary_leave()
    {
        var hub = new InMemoryTransportHub();
        var rules = new FixedRules { Damage = 2, Ticks = 1 };
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(20, 21, 0), rules,
            combatLogoutTicks: 8);
        (int slot, long player) = Join(s, hub, "a", out INetTransport c);
        Assert.True(s.SetHealth(player, new TileHealth { Current = 100, Max = 100 }));
        long monster = s.SpawnActor(new TileCoord(20, 20, 0), new TileActorSpawn(100, 1, TileDirection.S));
        Lock(s, monster, player);
        for (int i = 0; i < 3; i++) s.Tick(Dt);

        var deaths = new List<(long dead, long killer, int deadSlot)>();
        var left = new List<string>();
        s.OnDied += (dead, killer, deadSlot) => deaths.Add((dead, killer, deadSlot));
        s.PlayerLeaving += (_, account, _) => left.Add(account);

        hub.DisconnectClient(c);
        s.Poll();
        Assert.Equal(1, s.PlayerCount);

        // The blow that finishes them, inside the window.
        rules.Damage = 500;
        s.Tick(Dt);

        (long dead, long killerId, int deadSlot) = Assert.Single(deaths);
        Assert.Equal(player, dead);
        Assert.Equal(monster, killerId);
        Assert.Equal(slot, deadSlot);
        Assert.Empty(left);

        // The corpse is not swung at again: the roll phase skips a target already at zero.
        int rollsAtDeath = rules.Rolls.Count;
        for (int i = 0; i < 3; i++) s.Tick(Dt);
        Assert.Equal(rollsAtDeath, rules.Rolls.Count);

        // And the window still lapses on its own schedule, through the same leave a lapsed window always uses.
        for (int i = 0; i < 6; i++) s.Tick(Dt);
        Assert.Equal(new[] { "a" }, left);
        Assert.Equal(0, s.PlayerCount);
        Assert.False(s.TryGetActorState(player, out _));
    }

    // A FIGHT OF MISSES IS STILL A FIGHT. Ruling 13.3 says the window is held by "a combat event touched them", and
    // the player this rule exists to stop escaping is precisely the one being attacked who has NOT clicked back: no
    // lock of their own, and nothing on the record if only the damage counts. Every swing here misses, so no health
    // moves at all, and pulling the plug must still leave the body standing.
    [Fact]
    public void A_player_taking_nothing_but_misses_is_still_in_combat_and_still_lingers()
    {
        var hub = new InMemoryTransportHub();
        var rules = new FixedRules { Land = false, Ticks = 1 };
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(20, 21, 0), rules,
            combatLogoutTicks: 8);
        (_, long player) = Join(s, hub, "a", out INetTransport c);
        Assert.True(s.SetHealth(player, new TileHealth { Current = 100, Max = 100 }));
        long monster = s.SpawnActor(new TileCoord(20, 20, 0), new TileActorSpawn(100, 1, TileDirection.S));
        Lock(s, monster, player);
        for (int i = 0; i < 3; i++) s.Tick(Dt);

        // Three swings, three misses, no health moved, and the player holds no lock of their own.
        Assert.Equal(3, rules.Rolls.Count);
        Assert.True(s.TryGetHealth(player, out TileHealth full));
        Assert.Equal(100, full.Current);
        Assert.True(s.TryGetActorState(player, out TileMoveState st));
        Assert.Equal(0L, st.CombatTarget);

        var left = new List<string>();
        s.PlayerLeaving += (_, account, _) => left.Add(account);

        hub.DisconnectClient(c);
        s.Poll();
        Assert.Empty(left);
        Assert.Equal(1, s.PlayerCount);
        Assert.True(s.TryGetActorState(player, out _), "a fight of misses still holds the body in world");

        for (int i = 0; i < 9; i++) s.Tick(Dt);
        Assert.Equal(new[] { "a" }, left);
        Assert.Equal(0, s.PlayerCount);
    }

    // ONE ACCOUNT, ONE BODY, and the linger is the first thing in the tree that could break it. The seat is held at
    // the TileWorld layer for the window, but NetServer released it the moment the link dropped, so the account is
    // no longer known to the duplicate-session gate and a reconnect is handed the LOWEST free slot. Land that
    // reconnect on a slot BELOW the lingering body's and SpawnPlayer's own seat-recycle guard never fires, because
    // it guards the new slot rather than the account.
    //
    // Two seats for one account is not merely untidy: the whole persistence layer keys a record on the ACCOUNT, so
    // the deferred leave would later file the PRE-DROP state over the live session's record and silently lose
    // whatever happened after the reconnect.
    [Fact]
    public void A_rejoin_during_the_linger_ends_the_body_it_belongs_to_rather_than_seating_the_account_twice()
    {
        var hub = new InMemoryTransportHub();
        var rules = new FixedRules { Damage = 2, Ticks = 1 };
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(20, 21, 0), rules,
            combatLogoutTicks: 20);
        // Slot 0 is taken by an unrelated account, so the account under test is seated on slot 1 and its reconnect
        // is handed slot 0 back. That is the whole shape of the bug: a one-player test always gets its own slot back.
        (int fillerSlot, _) = Join(s, hub, "filler", out INetTransport fillerLink);
        Assert.Equal(0, fillerSlot);
        (int beeSlot, long beeBody) = Join(s, hub, "bee", out INetTransport beeLink);
        Assert.Equal(1, beeSlot);

        Assert.True(s.SetHealth(beeBody, new TileHealth { Current = 100, Max = 100 }));
        long monster = s.SpawnActor(new TileCoord(20, 20, 0), new TileActorSpawn(100, 1, TileDirection.S));
        Lock(s, monster, beeBody);
        for (int i = 0; i < 3; i++) s.Tick(Dt);

        var lifecycle = new List<string>();
        s.PlayerLeaving += (slot, account, _) => lifecycle.Add($"left {slot} {account}");
        s.PlayerJoined += (slot, account) => lifecycle.Add($"joined {slot} {account}");

        // The link dies mid fight, so the body lingers on slot 1.
        hub.DisconnectClient(beeLink);
        s.Poll();
        Assert.Empty(lifecycle);
        Assert.True(s.TryGetActorState(beeBody, out _), "the lingering body is still in world");

        // The unrelated player logs out cleanly, which frees slot 0 at both layers.
        hub.DisconnectClient(fillerLink);
        s.Poll();
        Assert.Equal(new[] { "left 0 filler" }, lifecycle);
        lifecycle.Clear();

        // The reconnect, onto the lower free seat.
        (int rejoinSlot, long rejoinBody) = Join(s, hub, "bee", out INetTransport _);
        Assert.Equal(0, rejoinSlot);
        Assert.NotEqual(beeBody, rejoinBody);

        // ONE seat, ONE body, and the lingering one is the one that ended.
        Assert.Equal(1, s.PlayerCount);
        Assert.Equal(rejoinSlot, Assert.Single(s.JoinedSlots));
        Assert.False(s.TryGetActorState(beeBody, out _), "the lingering body ended when its own account came back");
        Assert.True(s.TryGetActorState(rejoinBody, out _));
        // ORDERED, exactly as NetServer's own duplicate-session gate orders it: the old session's leave (and
        // therefore its save) is raised BEFORE the new session's join and its load.
        Assert.Equal(new[] { "left 1 bee", "joined 0 bee" }, lifecycle);

        // Past the window the deferred leave must not fire a second time, and above all must not raise
        // PlayerLeaving with the pre-drop state over the account's live session.
        lifecycle.Clear();
        for (int i = 0; i < 25; i++) s.Tick(Dt);
        Assert.Empty(lifecycle);
        Assert.Equal(1, s.PlayerCount);
        Assert.True(s.TryGetActorState(rejoinBody, out _), "the live session survived the lingering seat's expiry");
    }
}
