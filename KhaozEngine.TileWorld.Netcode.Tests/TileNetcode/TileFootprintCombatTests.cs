using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;
using static KhaozEngine.Tests.TileNetcode.TileCombatResolveTests;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>Combat between NxN bodies on the server. The roll asks <see cref="TileReach"/> with the target's whole
/// footprint and the attacker's own size, on the tiles the two bodies ENDED the tick on, so it fires exactly when the
/// two squares share an edge without overlapping. The chase stops on such an anchor and swings on arrival, a large
/// actor and a small player fight each other with the client predicting the approach, a mutual kill between two large
/// bodies kills both, and an entity interaction arrives on an in-range anchor. Every body is anchored on its
/// south-west tile and z counts north.</summary>
public class TileFootprintCombatTests
{
    const float Dt = 0.25f;

    static readonly TileCoord TargetAnchor = new(20, 20, 0);

    public static TheoryData<int, int> Pairings() => TileFootprintReachTests.Pairings();

    static TileActorSpawn Spawn(int size, ushort health = 100) =>
        new(health, 4, TileDirection.S) { FootprintSize = size };

    static TileCollisionMap OpenMap() => TileMoveSimulatorTests.Bake(TileMoveSimulatorTests.FlatWorld());

    // Geometry alone, independent of TileReach: the squares do not overlap and share a cardinal edge.
    static bool Touches(TileRect a, TileRect b)
    {
        if (!a.Intersect(b).IsEmpty) return false;
        bool xTouch = (a.X1 == b.X || b.X1 == a.X) && a.Z < b.Z1 && a.Z1 > b.Z;
        bool zTouch = (a.Z1 == b.Z || b.Z1 == a.Z) && a.X < b.X1 && a.X1 > b.X;
        return xTouch || zTouch;
    }

    // Every anchor in a ring one tile wider than the touching band, so the placements cover every overlap, every
    // in-range anchor, the four diagonal corners and a one-tile gap on each side. A fresh server per placement, so no
    // cooldown, lock or damage record crosses from one to the next.
    //
    // A LOCKED ATTACKER CHASES, and movement runs before the roll, so a placement out of range steps on the same tick
    // and may swing from where the step committed. The roll is therefore judged on the tile the attacker ENDED the
    // tick on, and the placement is judged by whether the swing came from it: an in-range anchor holds still and
    // swings from where it was put, and every other placement, overlap included, never swings from its own tile.
    [Theory, MemberData(nameof(Pairings))]
    public void The_roll_fires_exactly_when_the_footprints_are_in_range(int n, int m)
    {
        TileCollisionMap map = OpenMap();
        var targetRect = new TileRect(TargetAnchor.X, TargetAnchor.Z, m, m);
        int heldAndRolled = 0, overlapping = 0, movedAndRolled = 0, movedAndSilent = 0;

        for (int z = TargetAnchor.Z - n - 1; z <= TargetAnchor.Z + m + 1; z++)
            for (int x = TargetAnchor.X - n - 1; x <= TargetAnchor.X + m + 1; x++)
            {
                var placed = new TileCoord(x, z, 0);
                var hub = new InMemoryTransportHub();
                var rules = new FixedRules { Damage = 0 };
                using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server,
                    new TileCoord(5, 5, 0), rules);
                long target = s.SpawnActor(TargetAnchor, Spawn(m));
                long attacker = s.SpawnActor(placed, Spawn(n));
                Lock(s, attacker, target);

                s.Tick(Dt);

                Assert.True(s.TryGetActorState(attacker, out TileMoveState after));
                Assert.True(s.TryGetActorState(target, out TileMoveState still));
                Assert.Equal(TargetAnchor, still.Tile);
                bool inRangePlaced = TileReach.Contains(map, targetRect, 0, placed, n);
                bool inRangeEnded = TileReach.Contains(map, targetRect, 0, after.Tile, n);
                string where = $"size {n} on size {m}, placed {placed}, ended {after.Tile}";

                Assert.True(inRangePlaced == Touches(new TileRect(x, z, n, n), targetRect), where);
                Assert.True((inRangeEnded ? 1 : 0) == rules.Rolls.Count, $"{where}: {rules.Rolls.Count} rolls");
                Assert.True(inRangePlaced == rules.Rolls.Any(r => r.AttackerTile.Equals(placed)), where);
                if (inRangePlaced) Assert.Equal(placed, after.Tile);
                else Assert.NotEqual(placed, after.Tile);
                if (rules.Rolls.Count == 1)
                {
                    Assert.Equal(attacker, rules.Rolls[0].AttackerNetId);
                    Assert.Equal(after.Tile, rules.Rolls[0].AttackerTile);
                    Assert.Equal(TargetAnchor, rules.Rolls[0].TargetTile);
                }

                if (!new TileRect(x, z, n, n).Intersect(targetRect).IsEmpty) overlapping++;
                if (inRangePlaced) heldAndRolled++;
                else if (rules.Rolls.Count == 1) movedAndRolled++;
                else movedAndSilent++;
            }

        Assert.Equal(4 * (m + n - 1), heldAndRolled);
        Assert.Equal((m + n - 1) * (m + n - 1), overlapping);
        Assert.True(movedAndRolled > 0 && movedAndSilent > 0,
            $"moved and rolled {movedAndRolled}, moved and silent {movedAndSilent}");
    }

    // Eight clear tiles between the two bodies. Every tick before the first swing ends out of range, and the tick the
    // swing lands is the tick the chase commits the step that makes the squares touch.
    [Theory, MemberData(nameof(Pairings))]
    public void A_chase_closes_and_the_first_roll_lands(int n, int m)
    {
        TileCollisionMap map = OpenMap();
        var targetRect = new TileRect(TargetAnchor.X, TargetAnchor.Z, m, m);
        var hub = new InMemoryTransportHub();
        var rules = new FixedRules { Damage = 1 };
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0), rules);
        long target = s.SpawnActor(TargetAnchor, Spawn(m));
        long attacker = s.SpawnActor(new TileCoord(TargetAnchor.X - n - 8, TargetAnchor.Z, 0), Spawn(n));
        Lock(s, attacker, target);

        int rolledAt = -1;
        TileMoveState arrived = default;
        for (int i = 0; i < 80 && rolledAt < 0; i++)
        {
            s.Tick(Dt);
            Assert.True(s.TryGetActorState(attacker, out TileMoveState after));
            if (rules.Rolls.Count == 0)
            {
                Assert.False(TileReach.Contains(map, targetRect, 0, after.Tile, n),
                    $"tick {i}: in range on {after.Tile} and nothing swung");
                continue;
            }
            rolledAt = i;
            arrived = after;
        }

        Assert.True(rolledAt > 0, "the chase never swung");
        TileAttackContext roll = Assert.Single(rules.Rolls);
        Assert.Equal(attacker, roll.AttackerNetId);
        Assert.Equal(target, roll.TargetNetId);
        Assert.Equal(arrived.Tile, roll.AttackerTile);
        var attackerRect = new TileRect(arrived.Tile.X, arrived.Tile.Z, n, n);
        Assert.True(Touches(attackerRect, targetRect), $"swung from {arrived.Tile}");
        Assert.True(TileReach.Contains(map, targetRect, 0, arrived.Tile, n));
        Assert.Equal(new TileCoord(TargetAnchor.X - n, TargetAnchor.Z, 0), arrived.Tile);
        TileCombatEvent blow = Assert.Single(s.CombatEventsThisTick);
        Assert.True(blow.Landed);
        Assert.True(s.TryGetHealth(target, out TileHealth hp));
        Assert.Equal(99, hp.Current);
    }

    // The player starts on (20, 20), east of a 2x2 on (17, 20). Its in-range anchor is (19, 20), one step away, and a
    // one-tile answer for the body would walk it on to (18, 20), inside the body. The actor never moves, because its
    // swing from (17, 20) reaches the player through its own east column.
    [Fact]
    public void A_large_actor_retaliates_against_a_small_player_and_the_player_back()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileCollisionMap map = TileMoveSimulatorTests.Bake(doc);
        using var h = new TileCombatHarness(doc, new TileCoord(20, 20, 0));
        var rules = new FixedRules { Damage = 1, Ticks = 4 };
        h.Server.CombatRules = rules;
        h.Server.Actors.Behaviour = new TileWanderBehaviour(map);
        var home = new TileCoord(17, 20, 0);
        TileActorSpawner spawner = h.Server.Actors.Add(new TileActorDefinition
        {
            Id = "cow", MaxHealth = 100, WanderRadius = 0, LeashRadius = 10, FootprintSize = 2,
        }, home);
        h.Frames(8);
        long player = h.Client.LocalNetId;
        Assert.True(h.Server.SetHealth(player, new TileHealth { Current = 100, Max = 100 }));
        long actor = spawner.ActorNetId;
        for (int i = 0; i < 20 && !h.Client.TryGetLatestRemoteFootprint(actor, out _, out _); i++) h.Frames(1);
        Assert.True(h.Client.TryGetLatestRemoteFootprint(actor, out TileRect seen, out _));
        Assert.Equal(new TileRect(17, 20, 2, 2), seen);

        h.Client.Queue(TileCommand.Attack(actor, TileMoveMode.Run));
        for (int i = 0; i < 40 && !BothRolled(); i++) h.Frames(1);

        Assert.True(BothRolled(), $"{rules.Rolls.Count} rolls");
        // The fight goes on while every snapshot of the approach reaches the client, so a prediction that walked on
        // past the server's anchor has been corrected by the time it is counted.
        h.Frames(20);
        TileAttackContext swing = rules.Rolls.First(r => r.AttackerNetId == player);
        TileAttackContext answer = rules.Rolls.First(r => r.AttackerNetId == actor);
        Assert.Equal(new TileCoord(19, 20, 0), swing.AttackerTile);
        Assert.Equal(home, swing.TargetTile);
        Assert.Equal(home, answer.AttackerTile);
        Assert.Equal(new TileCoord(19, 20, 0), answer.TargetTile);
        Assert.True(rules.Rolls.IndexOf(swing) < rules.Rolls.IndexOf(answer), "the player swung first");

        Assert.True(h.Server.TryGetActorState(actor, out TileMoveState cow));
        Assert.Equal(player, cow.CombatTarget);
        Assert.Equal(home, cow.Tile);
        Assert.True(h.Server.TryGetActorState(player, out TileMoveState server));
        Assert.Equal(actor, server.CombatTarget);
        Assert.Equal(server.Tile, h.Client.Prediction.PredictedState.Tile);
        Assert.True(TileReach.Contains(map, new TileRect(17, 20, 2, 2), 0, server.Tile, 1));
        Assert.Equal(0, h.Client.CorrectionCount);
        Assert.Equal(0, h.Client.SnapCount);

        bool BothRolled() => rules.Rolls.Any(r => r.AttackerNetId == player)
            && rules.Rolls.Any(r => r.AttackerNetId == actor);
    }

    // A 2x2 on the east edge of a 3x3, where neither anchor is cardinally adjacent to the other body's anchor, so a
    // roll that read either size as one tile would leave both alive. Both swings are rolled before either lands, so
    // both die whichever of the two was spawned first.
    [Fact]
    public void A_mutual_kill_between_two_large_bodies_kills_both()
    {
        foreach (bool reversed in new[] { false, true })
        {
            var hub = new InMemoryTransportHub();
            var rules = new FixedRules { Damage = 5 };
            using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0),
                rules);
            var largeAt = new TileCoord(20, 20, 0);
            var smallAt = new TileCoord(23, 19, 0);
            long large, small;
            if (reversed)
            {
                small = s.SpawnActor(smallAt, Spawn(2, health: 5));
                large = s.SpawnActor(largeAt, Spawn(3, health: 5));
            }
            else
            {
                large = s.SpawnActor(largeAt, Spawn(3, health: 5));
                small = s.SpawnActor(smallAt, Spawn(2, health: 5));
            }
            Lock(s, large, small);
            Lock(s, small, large);
            var deaths = new List<(long dead, long killer, TileCoord tile)>();
            s.OnDied += (dead, killer, _) =>
            {
                Assert.True(s.TryGetActorState(dead, out TileMoveState corpse));
                deaths.Add((dead, killer, corpse.Tile));
            };

            s.Tick(Dt);

            Assert.Equal(2, s.CombatEventsThisTick.Count);
            Assert.All(s.CombatEventsThisTick, ev => Assert.True(ev.Killed));
            Assert.Equal(2, deaths.Count);
            Assert.Contains((large, small, largeAt), deaths);
            Assert.Contains((small, large, smallAt), deaths);
            Assert.Equal(0, s.ActorCount);
        }
    }

    // The two footprints the roll is handed are the two the reach check above it read: the attacker's through its own
    // simulator's FootprintOf, the target's off its own state (TileWorldServer.Combat.cs, the TileReach.Contains call
    // right before the Roll). Both orders of the asymmetry, since a rule reading them measures from one and against
    // the other.
    [Theory, InlineData(1, 2), InlineData(2, 1)]
    public void The_roll_is_handed_both_committed_footprints(int n, int m)
    {
        var hub = new InMemoryTransportHub();
        var rules = new FixedRules { Damage = 0 };
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(5, 5, 0),
            rules);
        long target = s.SpawnActor(TargetAnchor, Spawn(m));
        // Touching the target's west edge, which is an in-range anchor for either size, so the swing fires from the
        // tile the spawn put the body on and no step moves it first.
        var attackerAt = new TileCoord(TargetAnchor.X - n, TargetAnchor.Z, 0);
        long attacker = s.SpawnActor(attackerAt, Spawn(n));
        Lock(s, attacker, target);

        s.Tick(Dt);

        TileAttackContext roll = Assert.Single(rules.Rolls);
        Assert.Equal(attackerAt, roll.AttackerTile);
        Assert.Equal(TargetAnchor, roll.TargetTile);
        Assert.Equal(new TileRect(attackerAt.X, attackerAt.Z, n, n), roll.AttackerFootprint);
        Assert.Equal(new TileRect(TargetAnchor.X, TargetAnchor.Z, m, m), roll.TargetFootprint);
        // Each footprint is anchored on the tile beside it, which is the pairing a rule measuring edge to edge reads.
        Assert.Equal(roll.AttackerTile.X, roll.AttackerFootprint.X);
        Assert.Equal(roll.AttackerTile.Z, roll.AttackerFootprint.Z);
        Assert.Equal(roll.TargetTile.X, roll.TargetFootprint.X);
        Assert.Equal(roll.TargetTile.Z, roll.TargetFootprint.Z);
        Assert.True(Touches(roll.AttackerFootprint, roll.TargetFootprint));
    }

    // The two footprints are TRAILING and DEFAULTED, so a context a game builds by hand with the seven original
    // positional arguments still compiles and says it carries no geometry.
    [Fact]
    public void A_hand_built_context_keeps_the_seven_argument_positional_shape()
    {
        var context = new TileAttackContext(1L, new TileCoord(2, 3, 0), new TileHealth { Current = 4, Max = 5 }, 6L,
            new TileCoord(7, 8, 0), new TileHealth { Current = 9, Max = 10 }, 11L);

        Assert.True(context.AttackerFootprint.IsEmpty);
        Assert.True(context.TargetFootprint.IsEmpty);
    }

    // Approached from the east, where the nearest one-tile reach tile of the body's anchor, (27, 20), is inside the
    // body. The arrival is judged where the server raises it, off the player's own state at that moment.
    [Fact]
    public void An_entity_interaction_with_a_large_actor_arrives_on_an_in_range_anchor()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileCollisionMap map = TileMoveSimulatorTests.Bake(doc);
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = TileWorldServerTickTests.Server(doc, hub.Server, new TileCoord(33, 20, 0));
        s.SpawnPlayer(0, "a", "Ari");
        long actor = s.SpawnActor(new TileCoord(26, 20, 0), Spawn(3));
        var body = new TileRect(26, 20, 3, 3);
        var arrivals = new List<TileMoveState>();
        var refused = new List<long>();
        s.OnInteractEntity += (slot, _, target) =>
        {
            Assert.Equal(actor, target);
            Assert.True(s.TryGetPlayerState(slot, out TileMoveState at));
            arrivals.Add(at);
        };
        s.OnCannotReach += (_, target) => refused.Add(target);

        s.Enqueue(0, 0, TileCommand.InteractEntity(actor, TileMoveMode.Run));
        for (int i = 0; i < 40 && arrivals.Count == 0; i++) s.Tick(Dt);

        TileMoveState arrived = Assert.Single(arrivals);
        Assert.Empty(refused);
        Assert.True(TileReach.Contains(map, body, 0, arrived.Tile, 1), $"arrived on {arrived.Tile}");
        Assert.True(arrived.Footprint.Intersect(body).IsEmpty);
        Assert.Equal(new TileCoord(29, 20, 0), arrived.Tile);
        Assert.Equal(TileDirection.W, arrived.Facing);
    }
}
