using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// The SERVER half of the combat target seam, driven through the real wiring rather than through a fake resolver:
/// the 0c refresh, <c>TileEntityTargets</c> over the live cells, the follow inside the movement pass, and the pending
/// action a command abandons. <c>TileCombatTargetTests</c> drives the rules over its own fake target space, which is
/// what makes a chase reproducible tick by tick, and this class is what proves the same rules are wired to the
/// resolver that actually ships.
/// </summary>
public class TileCombatServerTests
{
    const float Dt = 0.25f;

    // A player chasing an actor that is MOVING, with nothing faked. This is the case the whole design exists for and
    // the one a fake resolver cannot pin: the tile the follow reads has to come off the entity through the 0c
    // refresh, on every tick, or the attacker paths once at the click and then walks to where the actor USED to be.
    [Fact]
    public void A_player_chases_a_moving_actor_through_the_servers_own_target_space()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileCollisionMap map = TileMoveSimulatorTests.Bake(doc);
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = TileWorldServerTickTests.Server(doc, hub.Server, new TileCoord(10, 10, 0));
        s.SpawnPlayer(0, "a", "Ari");
        long actor = s.SpawnActor(new TileCoord(10, 20, 0), new TileActorSpawn(30, 10, TileDirection.S));

        // The actor walks away north for the whole run, the player runs after it. Latched once: a spent latch falls
        // back to Continue at the mode the step left, so the route rides on by itself.
        s.Actors.Command(actor, TileCommand.WalkTo(new TileCoord(10, 30, 0), TileMoveMode.Walk));
        s.Enqueue(0, seq: 0, TileCommand.Attack(actor, TileMoveMode.Run));
        for (int i = 0; i < 40; i++) s.Tick(Dt);

        Assert.True(s.TryGetPlayerState(0, out TileMoveState player));
        Assert.True(s.TryGetActorState(actor, out TileMoveState prey));
        Assert.NotEqual(new TileCoord(10, 20, 0), prey.Tile);              // it really did run
        Assert.Equal(actor, player.CombatTarget);
        // In reach by the package's own rule, never a hand-rolled adjacency test, and NOT standing on it.
        Assert.True(TileReach.Contains(map, new TileRect(prey.Tile.X, prey.Tile.Z, 1, 1), prey.Tile.Plane,
            player.Tile));
        Assert.NotEqual(prey.Tile, player.Tile);
    }

    // An ACTOR attacking a player, which is the same follow read from the other side and the direction task 5's
    // behaviour will drive. The actor is commanded once and chases from then on, so this also pins that the follow
    // survives the latch being spent.
    [Fact]
    public void An_actor_chases_a_walking_player_through_the_same_seam()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileCollisionMap map = TileMoveSimulatorTests.Bake(doc);
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = TileWorldServerTickTests.Server(doc, hub.Server, new TileCoord(10, 10, 0));
        long player = s.SpawnPlayer(0, "a", "Ari");
        long actor = s.SpawnActor(new TileCoord(10, 16, 0), new TileActorSpawn(30, 10, TileDirection.S));

        s.Enqueue(0, seq: 0, TileCommand.WalkTo(new TileCoord(20, 10, 0), TileMoveMode.Walk));
        s.Actors.Command(actor, TileCommand.Attack(player, TileMoveMode.Run));
        for (int i = 0; i < 40; i++) s.Tick(Dt);

        Assert.True(s.TryGetActorState(actor, out TileMoveState hunter));
        Assert.True(s.TryGetPlayerState(0, out TileMoveState quarry));
        Assert.Equal(player, hunter.CombatTarget);
        Assert.True(TileReach.Contains(map, new TileRect(quarry.Tile.X, quarry.Tile.Z, 1, 1), quarry.Tile.Plane,
            hunter.Tile));
    }

    // The resolver itself, over the cells the server actually hands it. Both kinds of entity resolve to their
    // COMMITTED tile, and the three ids that name nothing all answer false.
    //
    // Zero is the interesting one and it is not a formality: 0 is TileMoveState.CombatTarget's "not fighting"
    // sentinel, so an entity answering under it would make every idle entity permanently locked onto something. It
    // cannot happen because net ids count from 1 (TileWorldServer.Actors.cs says so where it returns 0 for a full
    // cell), and this is where that fact is pinned rather than assumed.
    [Fact]
    public void The_entity_target_space_resolves_both_kinds_of_entity_and_refuses_the_ids_that_name_nothing()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = TileWorldServerTickTests.Server(
            TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(10, 10, 0));
        long player = s.SpawnPlayer(0, "a", "Ari");
        long actor = s.SpawnActor(new TileCoord(14, 10, 0), new TileActorSpawn(30, 10, TileDirection.S));

        var targets = new TileEntityTargets();
        targets.Refresh(new List<CellSim>(s.Host.Cells));

        Assert.True(targets.TryGetFootprint(player, out TileRect playerRect, out int playerPlane));
        Assert.Equal(new TileRect(10, 10, 1, 1), playerRect);
        Assert.Equal(0, playerPlane);
        Assert.True(targets.TryGetFootprint(actor, out TileRect actorRect, out int actorPlane));
        Assert.Equal(new TileRect(14, 10, 1, 1), actorRect);
        Assert.Equal(0, actorPlane);

        Assert.False(targets.TryGetFootprint(0L, out _, out _));
        Assert.False(targets.TryGetFootprint(long.MinValue, out _, out _));
        Assert.False(targets.TryGetFootprint(long.MaxValue, out _, out _));

        // The snapshot is a snapshot. A step committed after the refresh is invisible until the next one, which is
        // the property the follow's order-independence rests on: every read inside one tick is a keyed lookup into a
        // map that was fully built before the movement pass began.
        s.Enqueue(0, 0, TileCommand.WalkTo(new TileCoord(10, 14, 0), TileMoveMode.Run));
        s.Tick(Dt);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState moved));
        Assert.Equal(new TileCoord(10, 11, 0), moved.Tile);
        Assert.True(targets.TryGetFootprint(player, out TileRect stale, out _));
        Assert.Equal(new TileRect(10, 10, 1, 1), stale);
        targets.Refresh(new List<CellSim>(s.Host.Cells));
        Assert.True(targets.TryGetFootprint(player, out TileRect fresh, out _));
        Assert.Equal(new TileRect(10, 11, 1, 1), fresh);
    }

    // A GHOST is excluded by construction, so a border mirror can never answer under the owned entity's net id with
    // a tile the owner has already left. What the follow does with the false answer is rule 2, "dead, despawned or
    // out of view", so it CLEARS the lock, which is correct for a mirror of something the owning cell is following
    // anyway.
    //
    // A MIGRATING entity is the other case and gets the opposite answer, held for TileEntityTargets'
    // MigratingGraceRefreshes rather than dropped. Pinned here at a grace of ZERO, which is the drop, so this test
    // keeps stating the exclusion rule and the sibling below states the window. Driven over a cell built by hand,
    // because the in-process link completes migrate, ack and release inside one ProcessHandoffs and therefore never
    // leaves anything Migrating at a 0c boundary.
    [Fact]
    public void A_ghost_is_not_a_target_and_a_migrating_entity_is_not_one_either_at_a_zero_grace()
    {
        var cell = new CellSim(new CellCoord(0, 0), Dt, TileProtocol.CreateRegistry(), interestCellSize: 8f);
        Entity owned = cell.World.Spawn();
        cell.World.Set(owned, new NetId(11L));
        cell.World.Set(owned, TileMoveState.At(new TileCoord(3, 4, 0), TileDirection.N));
        Entity ghost = cell.World.Spawn();
        cell.World.Set(ghost, new NetId(22L));
        cell.World.Set(ghost, TileMoveState.At(new TileCoord(5, 6, 0), TileDirection.N));
        cell.World.Set(ghost, new Ghost { Source = new CellCoord(1, 0) });
        Entity leaving = cell.World.Spawn();
        cell.World.Set(leaving, new NetId(33L));
        cell.World.Set(leaving, TileMoveState.At(new TileCoord(7, 8, 0), TileDirection.N));
        cell.World.Set(leaving, new Migrating { Destination = new CellCoord(1, 0) });

        var targets = new TileEntityTargets(migratingGraceRefreshes: 0);
        targets.Refresh(new List<CellSim> { cell });

        Assert.True(targets.TryGetFootprint(11L, out TileRect rect, out _));
        Assert.Equal(new TileRect(3, 4, 1, 1), rect);
        Assert.False(targets.TryGetFootprint(22L, out _, out _));
        Assert.False(targets.TryGetFootprint(33L, out _, out _));
    }

    // THE MIGRATING WINDOW, which is zero in process and is not zero on a networked link. The in-process ICellLink
    // completes migrate, ack and release inside one ProcessHandoffs, so nothing is ever observed Migrating at the
    // tick step that refreshes this. A networked one spans calls by design, and the source cell holds the entity
    // frozen until the ack arrives. Excluding it outright made it unresolvable for that whole stretch, the follow's
    // rule 2 reads an unresolvable target as dead, despawned or out of view, and every fight in the world would
    // break the moment its target crossed a region boundary.
    //
    // A frozen entity's tile IS its pre-handoff tile, so holding it answers with the truth rather than with a
    // guess. BOUNDED, because a handshake that never completes must not hold a lock forever: past the window the
    // id stops resolving and the ordinary rule 2 answer applies again.
    //
    // Driven over a cell built by hand for the same reason the sibling above is: no in-process handoff can produce
    // this state at a call boundary.
    [Fact]
    public void A_migrating_entity_answers_its_pre_handoff_tile_for_a_bounded_window_and_then_stops()
    {
        var cell = new CellSim(new CellCoord(0, 0), Dt, TileProtocol.CreateRegistry(), interestCellSize: 8f);
        Entity crosser = cell.World.Spawn();
        cell.World.Set(crosser, new NetId(44L));
        TileMoveState state = TileMoveState.At(new TileCoord(7, 8, 0), TileDirection.N);
        state.CombatTarget = 11L;
        cell.World.Set(crosser, state);
        var cells = new List<CellSim> { cell };

        var targets = new TileEntityTargets(migratingGraceRefreshes: 2);
        targets.Refresh(cells);
        Assert.True(targets.TryGetFootprint(44L, out TileRect owned, out int plane));
        Assert.Equal(new TileRect(7, 8, 1, 1), owned);
        Assert.Equal(0, plane);
        Assert.Equal(44L, targets.TargetedBy(11L));

        // The owner has serialized it, sent the Migrate and frozen it. On a networked link it stays like this.
        cell.World.Set(crosser, new Migrating { Destination = new CellCoord(1, 0) });
        for (int refresh = 1; refresh <= 2; refresh++)
        {
            targets.Refresh(cells);
            Assert.True(targets.TryGetFootprint(44L, out TileRect held, out _),
                $"refresh {refresh} of the window still answers");
            Assert.Equal(new TileRect(7, 8, 1, 1), held);
            Assert.Equal(44L, targets.TargetedBy(11L));
        }

        targets.Refresh(cells);
        Assert.False(targets.TryGetFootprint(44L, out _, out _));
        Assert.Equal(0L, targets.TargetedBy(11L));
    }

    // Admit's half of the mutual exclusion: an APPLIED attack abandons the pending action, exactly as an applied
    // walk does, because the simulator clears the state's own InteractTarget on one and an entry that outlived it
    // would fire the moment the chase happened to pass a reach tile of the thing the player walked away from.
    //
    // A CROSS-PLANE attack abandons nothing, and that is the same asymmetry the interact case has: the simulator
    // drops that command whole, so there is nothing for the queue to be a record of.
    [Fact]
    public void An_applied_attack_clears_the_pending_action_and_a_cross_plane_one_leaves_it_alone()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileObject booth = doc.AddObject("bank_booth", 10, 20, 0, 0);
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = TileWorldServerTickTests.Server(doc, hub.Server, new TileCoord(10, 10, 0));
        s.SpawnPlayer(0, "a", "Ari");
        long upstairs = s.SpawnActor(new TileCoord(12, 10, 1), new TileActorSpawn(30, 10, TileDirection.S));
        long beside = s.SpawnActor(new TileCoord(14, 10, 0), new TileActorSpawn(30, 10, TileDirection.S));

        s.Enqueue(0, 0, TileCommand.Interact(booth.Id, TileMoveMode.Walk));
        s.Tick(Dt);
        Assert.Equal(1, s.Actions.PendingCount);

        s.Enqueue(0, 1, TileCommand.Attack(upstairs, TileMoveMode.Walk));
        s.Tick(Dt);
        Assert.Equal(1, s.Actions.PendingCount);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState refused));
        Assert.Equal(booth.Id, refused.InteractTarget);
        Assert.Equal(0L, refused.CombatTarget);

        s.Enqueue(0, 2, TileCommand.Attack(beside, TileMoveMode.Walk));
        s.Tick(Dt);
        Assert.Equal(0, s.Actions.PendingCount);
        Assert.True(s.TryGetPlayerState(0, out TileMoveState locked));
        Assert.Equal(beside, locked.CombatTarget);
        Assert.Equal(0L, locked.InteractTarget);
    }
}
