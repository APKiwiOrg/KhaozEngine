using System.Collections.Generic;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileCombatAdmissionTests
{
    const float Dt = 0.25f;

    sealed class AdmissionRules : ITileCombatRules
    {
        public bool AllowTargets;
        public readonly List<(long attacker, long target)> Admissions = new();
        public readonly List<TileAttackContext> Rolls = new();

        public bool CanAttack(long attackerNetId, long targetNetId)
        {
            Admissions.Add((attackerNetId, targetNetId));
            return AllowTargets;
        }

        public TileAttackOutcome Roll(in TileAttackContext context)
        {
            Rolls.Add(context);
            return TileAttackOutcome.Hit(1);
        }

        public byte AttackTicks(long attackerNetId) => 4;
    }

    static TileWorldServer Server(InMemoryTransportHub hub, AdmissionRules rules)
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        return TileCombatResolveTests.Server(doc, hub.Server, new TileCoord(5, 5, 0), rules);
    }

    [Fact]
    public void A_refused_player_target_never_locks_chases_or_rolls_and_an_allowed_target_still_attacks()
    {
        var hub = new InMemoryTransportHub();
        var rules = new AdmissionRules();
        using TileWorldServer server = Server(hub, rules);
        long player = server.SpawnPlayer(0, "a", "Ari");
        long target = server.SpawnActor(new TileCoord(5, 6, 0), new TileActorSpawn(30, 4, TileDirection.S));
        Assert.True(server.SetHealth(player, new TileHealth { Current = 30, Max = 30 }));

        server.Enqueue(0, 0, TileCommand.Attack(target, TileMoveMode.Run));
        server.Tick(Dt);

        Assert.Equal(new[] { (player, target) }, rules.Admissions);
        Assert.True(server.TryGetPlayerState(0, out TileMoveState refused));
        Assert.Equal(new TileCoord(5, 5, 0), refused.Tile);
        Assert.Equal(0L, refused.CombatTarget);
        Assert.Empty(rules.Rolls);
        Assert.Empty(server.CombatEventsThisTick);

        rules.AllowTargets = true;
        server.Enqueue(0, 1, TileCommand.Attack(target, TileMoveMode.Run));
        server.Tick(Dt);

        Assert.True(server.TryGetPlayerState(0, out TileMoveState allowed));
        Assert.Equal(target, allowed.CombatTarget);
        Assert.Single(rules.Rolls);
    }

    [Fact]
    public void A_refused_latched_actor_attack_is_spent_without_a_lock_or_roll()
    {
        var hub = new InMemoryTransportHub();
        var rules = new AdmissionRules();
        using TileWorldServer server = Server(hub, rules);
        long player = server.SpawnPlayer(0, "a", "Ari");
        long actor = server.SpawnActor(new TileCoord(5, 6, 0), new TileActorSpawn(30, 4, TileDirection.S));
        Assert.True(server.SetHealth(player, new TileHealth { Current = 30, Max = 30 }));

        server.Actors.Command(actor, TileCommand.Attack(player, TileMoveMode.Run));
        server.Tick(Dt);

        Assert.Equal(new[] { (actor, player) }, rules.Admissions);
        Assert.Equal(0, server.Actors.PendingCommandCount);
        Assert.True(server.TryGetActorState(actor, out TileMoveState refused));
        Assert.Equal(new TileCoord(5, 6, 0), refused.Tile);
        Assert.Equal(0L, refused.CombatTarget);
        Assert.Empty(rules.Rolls);
    }
}
