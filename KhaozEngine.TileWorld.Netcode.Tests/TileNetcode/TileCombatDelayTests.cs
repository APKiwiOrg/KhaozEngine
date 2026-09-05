using System.Text;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;
using static KhaozEngine.Tests.TileNetcode.TileCombatResolveTests;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// <c>TileWorldServer.DelayAttack</c>: the public writer for the swing cooldown, which is how a game charges attack
/// time for something that is not a swing (eating, a potion, a stun). Split from
/// <see cref="TileCombatResolveTests"/> because it is a WRITE against the cadence rather than a hit-pipeline case,
/// and it shares that file's fixture through the <c>using static</c> above rather than keeping a second copy.
/// </summary>
public class TileCombatDelayTests
{
    const float Dt = 0.25f;

    // Connects one client and returns its player's net id. Same shape as the linger tests' Join, and here for the
    // one thing only a player can exercise: a player carries NO TileCombatState until the combat pass has something
    // to write for them, which is the create branch DelayAttack has and ForgetAttacker does not.
    static long JoinPlayer(TileWorldServer s, InMemoryTransportHub hub, string account)
    {
        INetTransport transport = hub.CreateClient();
        var client = new NetClient(transport, Encoding.UTF8.GetBytes(account));
        client.Poll();
        s.Poll();
        foreach (int slot in s.JoinedSlots)
            if (s.TryGetAccountId(slot, out string held) && held == account)
            {
                Assert.True(s.TryGetPlayerNetId(slot, out long netId));
                return netId;
            }
        Assert.Fail($"no seat for {account}");
        return 0L;
    }

    // The headline rule, measured rather than asserted about: the swing that WOULD have landed on a known tick lands
    // exactly the delay's own number of ticks later. Both runs are the same fight from the same start, so nothing
    // but the delay differs between them.
    [Fact]
    public void A_fighting_attacker_delayed_by_three_swings_exactly_three_ticks_later()
    {
        Assert.Equal(5, SecondSwingTick(delay: 0));
        Assert.Equal(8, SecondSwingTick(delay: 3));
        Assert.Equal(6, SecondSwingTick(delay: 1));

        // The tick index the SECOND swing of a cadence-4 fight resolves on, with `delay` ticks added to the wait
        // immediately after the first one. Undelayed that is tick 5: the first swing is tick 1, which sets the wait
        // to 4, and ticks 2 to 5 run it 3, 2, 1, 0.
        static int SecondSwingTick(byte delay)
        {
            var hub = new InMemoryTransportHub();
            var rules = new FixedRules { Damage = 1, Ticks = 4 };
            using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0), rules);
            long a = s.SpawnActor(new TileCoord(20, 20, 0), new TileActorSpawn(1000, 4, TileDirection.S));
            long b = s.SpawnActor(new TileCoord(20, 21, 0), new TileActorSpawn(1000, 4, TileDirection.S));
            Lock(s, a, b);

            s.Tick(Dt);
            Assert.Single(rules.Rolls);
            Assert.True(s.DelayAttack(a, delay));

            for (int tick = 2; tick <= 40; tick++)
            {
                s.Tick(Dt);
                if (rules.Rolls.Count > 1) return tick;
            }
            Assert.Fail("the delayed attacker never swung again");
            return 0;
        }
    }

    // The create branch, and the case it exists for: an idle PLAYER carries no combat state at all, so a delay that
    // did nothing for them would be a bite that is free right up until the attack command lands. The command is
    // issued AFTER the delay here, which is the ordering that would swing straight through a dropped write.
    [Fact]
    public void An_idle_player_delayed_then_told_to_attack_waits_out_the_delay_first()
    {
        var hub = new InMemoryTransportHub();
        var rules = new FixedRules { Damage = 1, Ticks = 4 };
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(20, 20, 0), rules);
        long player = JoinPlayer(s, hub, "eater");
        Assert.True(s.SetHealth(player, new TileHealth { Current = 100, Max = 100 }));
        long monster = s.SpawnActor(new TileCoord(20, 21, 0), new TileActorSpawn(100, 4, TileDirection.S));

        // Nothing has ever written one, which is what makes this the create branch.
        Assert.False(s.TryGetCombatState(player, out TileCombatState _));
        Assert.True(s.DelayAttack(player, 3));
        Assert.True(s.TryGetCombatState(player, out TileCombatState created));
        Assert.Equal(3, created.CooldownRemaining);
        // The delay and nothing else: no cadence is invented, so the first swing still asks the rules for one.
        Assert.Equal(0, created.AttackTicks);

        Lock(s, player, monster);
        for (int i = 0; i < 3; i++)
        {
            s.Tick(Dt);
            // The monster stands still and never locks back (no behaviour is wired), so every roll here is the
            // player's, and there must not be one until the delay has run out.
            if (i < 2) Assert.Empty(rules.Rolls);
        }

        TileAttackContext roll = Assert.Single(rules.Rolls);
        Assert.Equal(player, roll.AttackerNetId);
        Assert.Equal(monster, roll.TargetNetId);
    }

    [Fact]
    public void An_unknown_net_id_is_refused_and_writes_nothing()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0),
            new FixedRules());
        long actor = s.SpawnActor(new TileCoord(20, 20, 0), new TileActorSpawn(100, 4, TileDirection.S));

        Assert.False(s.DelayAttack(actor + 5000, 4));
        // The real actor beside it is untouched, so the refusal wrote nothing anywhere.
        Assert.True(s.TryGetCombatState(actor, out TileCombatState combat));
        Assert.Equal(0, combat.CooldownRemaining);
    }

    // A zero reports the entity is there and writes nothing, so a caller can use it as an existence check.
    [Fact]
    public void A_zero_delay_is_a_no_op_that_still_answers_for_the_entity()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0),
            new FixedRules());
        long actor = s.SpawnActor(new TileCoord(20, 20, 0), new TileActorSpawn(100, 4, TileDirection.S));
        Assert.True(s.DelayAttack(actor, 7));

        Assert.True(s.DelayAttack(actor, 0));
        Assert.True(s.TryGetCombatState(actor, out TileCombatState combat));
        Assert.Equal(7, combat.CooldownRemaining);
        Assert.False(s.DelayAttack(actor + 5000, 0));
    }

    // Saturation rather than a wrap, which is the one failure a stun must not have: 200 + 200 wrapping to 144 would
    // be a shorter delay than the first call alone asked for.
    [Fact]
    public void The_wait_saturates_at_two_hundred_and_fifty_five()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0),
            new FixedRules());
        long actor = s.SpawnActor(new TileCoord(20, 20, 0), new TileActorSpawn(100, 4, TileDirection.S));

        Assert.True(s.DelayAttack(actor, 200));
        Assert.True(s.DelayAttack(actor, 200));
        Assert.True(s.TryGetCombatState(actor, out TileCombatState combat));
        Assert.Equal(255, combat.CooldownRemaining);

        Assert.True(s.DelayAttack(actor, 255));
        Assert.True(s.TryGetCombatState(actor, out TileCombatState held));
        Assert.Equal(255, held.CooldownRemaining);
    }

    // The delay is a WAIT rather than a freeze: it runs down once per tick like any other cadence, so a stunned
    // entity is not stunned forever because nothing else pokes it.
    [Fact]
    public void The_delay_runs_down_once_per_tick()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0),
            new FixedRules());
        long actor = s.SpawnActor(new TileCoord(20, 20, 0), new TileActorSpawn(100, 4, TileDirection.S));
        Assert.True(s.DelayAttack(actor, 5));

        for (int expected = 4; expected >= 0; expected--)
        {
            s.Tick(Dt);
            Assert.True(s.TryGetCombatState(actor, out TileCombatState combat));
            Assert.Equal(expected, combat.CooldownRemaining);
        }

        // Floors rather than going negative or wrapping, which is the pre-existing rule this must not have broken.
        s.Tick(Dt);
        Assert.True(s.TryGetCombatState(actor, out TileCombatState floored));
        Assert.Equal(0, floored.CooldownRemaining);
    }
}
