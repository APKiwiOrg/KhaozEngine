using System.Collections.Generic;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>The public cancellation door for an interaction a game has deliberately abandoned.</summary>
public class TilePendingActionCancellationTests
{
    [Fact]
    public void Cancellation_while_approaching_keeps_the_walk_and_never_acts_or_refuses()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileObject booth = doc.AddObject("bank_booth", 28, 20, 0, 0);
        using var h = new TileCombatHarness(doc, new TileCoord(20, 20, 0));
        var acted = new List<long>();
        var refused = new List<long>();
        var notices = new List<string>();
        h.Server.OnInteract += (_, _, target) => acted.Add(target);
        h.Server.OnCannotReach += (_, target) => refused.Add(target);
        h.Client.NoticeReceived += notices.Add;
        h.Frames(8);

        h.Client.Queue(TileCommand.Interact(booth.Id, TileMoveMode.Run));
        TileMoveState approaching = default;
        for (int i = 0; i < 40; i++)
        {
            h.Frames(1);
            Assert.True(h.Server.TryGetPlayerState(0, out approaching));
            if (approaching.InteractTarget == booth.Id && !approaching.Route.IsIdle) break;
        }
        Assert.Equal(booth.Id, approaching.InteractTarget);
        Assert.False(approaching.Route.IsIdle);

        Assert.True(h.Server.CancelPendingAction(0));
        Assert.True(h.Server.TryGetPlayerState(0, out TileMoveState cancelled));
        TileMoveState expected = approaching;
        expected.InteractTarget = 0L;
        Assert.Equal(expected, cancelled);

        h.Frames(200);

        Assert.Empty(acted);
        Assert.Empty(refused);
        Assert.DoesNotContain(TileServerReason.CannotReach, notices);
        Assert.True(h.Server.TryGetPlayerState(0, out TileMoveState arrived));
        Assert.Equal(new TileCoord(27, 20, 0), arrived.Tile);
        Assert.True(arrived.Route.IsIdle);
        Assert.Equal(0L, arrived.InteractTarget);
    }

    [Fact]
    public void Missing_empty_and_already_cancelled_seats_return_false()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileObject booth = doc.AddObject("bank_booth", 28, 20, 0, 0);
        var hub = new InMemoryTransportHub();
        using TileWorldServer server = TileWorldServerTickTests.Server(doc, hub.Server, new TileCoord(20, 20, 0));
        server.SpawnPlayer(0, "a", "Ari");

        Assert.False(server.CancelPendingAction(-1));
        Assert.False(server.CancelPendingAction(0));
        Assert.False(server.CancelPendingAction(99));
        server.Actions.Issue(99, booth.Id, server.TickCount);
        Assert.False(server.CancelPendingAction(99));
        Assert.True(server.Actions.TryPeek(99, out _));

        server.Enqueue(0, 0, TileCommand.Interact(booth.Id, TileMoveMode.Run));
        server.Tick(TileCombatHarness.Tick);
        Assert.True(server.CancelPendingAction(0));
        Assert.False(server.CancelPendingAction(0));
    }

    [Fact]
    public void Cancellation_preserves_the_combat_lock_and_cooldown()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        TileObject booth = doc.AddObject("bank_booth", 28, 20, 0, 0);
        var hub = new InMemoryTransportHub();
        using TileWorldServer server = TileWorldServerTickTests.Server(doc, hub.Server, new TileCoord(20, 20, 0));
        long player = server.SpawnPlayer(0, "a", "Ari");
        long opponent = server.SpawnActor(new TileCoord(22, 20, 0),
            new TileActorSpawn(20, AttackTicks: 4, TileDirection.W));
        server.Enqueue(0, 0, TileCommand.Interact(booth.Id, TileMoveMode.Run));
        server.Tick(TileCombatHarness.Tick);
        Assert.True(server.TryGetPlayerState(0, out TileMoveState armed));
        armed.CombatTarget = opponent;
        server.SetPlayerState(0, armed);
        Assert.True(server.DelayAttack(player, 7));
        Assert.True(server.TryGetCombatState(player, out TileCombatState beforeCombat));

        Assert.True(server.CancelPendingAction(0));

        Assert.True(server.TryGetPlayerState(0, out TileMoveState after));
        Assert.Equal(opponent, after.CombatTarget);
        Assert.Equal(0L, after.InteractTarget);
        Assert.Equal(armed.Route, after.Route);
        Assert.True(server.TryGetCombatState(player, out TileCombatState afterCombat));
        Assert.Equal(beforeCombat, afterCombat);
    }
}
