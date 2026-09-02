using System.Collections.Generic;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;
using static KhaozEngine.Tests.TileNetcode.TileWanderBehaviourTests;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// A combat record names an entity that may no longer be there, and until this shipped nothing said so. The held
/// target had <c>TargetResolved</c> and the two records had nothing, so the retaliate rule answered a net id
/// out of the past: the follow cleared the lock it set on the same tick, the rule re-issued the same doomed attack
/// on the next one, and the actor stood still for the whole retaliate window instead of wandering. Split from
/// <see cref="TileWanderBehaviourTests"/> because these are about the RESOLUTION rather than about the five rules,
/// and they share that file's fixture through the <c>using static</c> above.
/// </summary>
public class TileActorRecordResolveTests
{
    // Both records get a resolution answer, out of the same per-tick snapshot the target's tile comes from, so a
    // behaviour reading either one agrees with the stepper about who is still in the world.
    [Fact]
    public void The_context_reports_whether_the_damage_and_swing_records_still_resolve()
    {
        var hub = new InMemoryTransportHub();
        var scripted = new ScriptedBehaviour();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(30, 32, 0),
            behaviour: scripted);
        TileActorSpawner spawner = s.Actors.Add(Rat, new TileCoord(30, 30, 0));
        // The attacker is an ACTOR rather than a player, because leaving the world is then one call. A player who
        // logs out and whose linger lapses is the same fact seen from the session's end.
        long attacker = s.SpawnActor(new TileCoord(30, 31, 0), new TileActorSpawn(30, 10, TileDirection.S));
        s.Tick(Dt);
        long actor = spawner.ActorNetId;

        Damage(s, actor, attacker);
        s.Tick(Dt);
        TileActorContext seen = Last(scripted, actor);
        Assert.Equal(attacker, seen.LastDamagedBy);
        Assert.True(seen.LastDamagedByResolved);
        Assert.Equal(attacker, seen.LastAttackedBy);
        Assert.True(seen.LastAttackedByResolved);

        Assert.True(s.DespawnActor(attacker));
        s.Tick(Dt);
        seen = Last(scripted, actor);
        // The RECORD is left alone, which is the whole reason the answer is a second field rather than a sweep at
        // the despawn: who hit last is a fact about the past and a game may still want to read it.
        Assert.Equal(attacker, seen.LastDamagedBy);
        Assert.False(seen.LastDamagedByResolved);
        Assert.Equal(attacker, seen.LastAttackedBy);
        Assert.False(seen.LastAttackedByResolved);
    }

    // The consequence, through the shipped default behaviour. An attacker that left the world used to hold the
    // actor for the whole retaliate window: every tick took the Attack branch, so the wander never ran and the
    // Returning flag a leash walk home depends on was cleared on every one of them.
    [Fact]
    public void An_actor_whose_attacker_left_the_world_goes_back_to_wandering()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = Server(TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(30, 32, 0));
        TileActorSpawner spawner = s.Actors.Add(Rat, new TileCoord(30, 30, 0));
        long attacker = s.SpawnActor(new TileCoord(30, 31, 0), new TileActorSpawn(30, 10, TileDirection.S));
        s.Tick(Dt);
        long actor = spawner.ActorNetId;

        Damage(s, actor, attacker);
        Assert.True(s.DespawnActor(attacker));

        // Well inside the 40-tick retaliate window, so what is being read is the skip rather than the window
        // lapsing on its own.
        var tiles = new HashSet<TileCoord>();
        for (int i = 0; i < 20; i++)
        {
            s.Tick(Dt);
            Assert.True(s.TryGetActorState(actor, out TileMoveState st));
            tiles.Add(st.Tile);
        }

        Assert.True(tiles.Count > 1, "the rat stood still for the retaliate window chasing an attacker that left");
    }

    static TileActorContext Last(ScriptedBehaviour scripted, long netId)
    {
        for (int i = scripted.Seen.Count - 1; i >= 0; i--)
            if (scripted.Seen[i].NetId == netId) return scripted.Seen[i];
        Assert.Fail($"no decision was asked for actor {netId}");
        return default;
    }
}
