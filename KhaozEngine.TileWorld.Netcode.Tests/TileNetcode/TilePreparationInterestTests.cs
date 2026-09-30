using System;
using System.Collections.Generic;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TilePreparationInterestTests
{
    [Fact]
    public void Entering_interest_receives_the_current_schedule()
    {
        using var f = new PreparationDeliveryScenario(spawn: new TileCoord(40, 20, 0));
        (long attacker, _) = f.Fight();
        f.Step();
        Assert.False(f.Client.TryGetCombatPreparation(attacker, out _));
        f.Move(f.Client.LocalNetId, new TileCoord(25, 20, 0));
        f.Step();
        Assert.True(f.Server.TryGetCombatPreparation(attacker, out var server));
        Assert.True(f.Client.TryGetCombatPreparation(attacker, out var client));
        Assert.Equal(server, client);
    }

    [Fact]
    public void Terminal_on_interest_exit_reaches_the_previous_viewer()
    {
        using var f = new PreparationDeliveryScenario();
        (long attacker, long target) = f.Fight();
        f.Step();
        var ended = new List<CombatPreparationEnded>();
        f.Client.CombatPreparationEnded += ended.Add;
        f.Move(f.Client.LocalNetId, new TileCoord(50, 20, 0));
        Assert.True(f.Server.DespawnActor(target));
        f.Step();
        Assert.Equal(attacker, Assert.Single(ended).AttackerNetId);
        Assert.False(f.Client.TryGetCombatPreparation(attacker, out _));
    }

    [Fact]
    public void A_border_ghost_does_not_delay_the_owner_schedule()
    {
        using var f = new PreparationDeliveryScenario(spawn: new TileCoord(63, 20, 0));
        (long attacker, _) = f.Fight(new TileCoord(64, 21, 0));
        Assert.True(f.Server.Host.TryGetOwner(attacker, out var actorCell, out _));
        Assert.True(f.Server.Host.TryGetOwner(f.Client.LocalNetId, out var viewerCell, out _));
        Assert.NotSame(actorCell, viewerCell);
        f.Step();
        Assert.True(f.Server.TryGetCombatPreparation(attacker, out var server));
        Assert.True(f.Client.TryGetCombatPreparation(attacker, out var client));
        Assert.Equal(server, client);
        Assert.Equal(f.Server.TickCount - 1, client.PrepareTick);
    }

    [Theory]
    [InlineData(20, 20)]
    [InlineData(20, 23)]
    public void Either_participant_in_interest_is_enough(int x, int z)
    {
        using var f = new PreparationDeliveryScenario(spawn: new TileCoord(x, z, 0), radius: 1);
        (long attacker, long target) = f.Fight();
        f.Step();
        Assert.Equal(z == 20, f.Client.View.Entities.ContainsKey(attacker));
        Assert.Equal(z == 23, f.Client.View.Entities.ContainsKey(target));
        Assert.True(f.Client.TryGetCombatPreparation(attacker, out _));
    }

    [Fact]
    public void Cancellation_followed_by_replacement_keeps_new_identity_and_delivers_old_reason_once()
    {
        using var f = new PreparationDeliveryScenario();
        (long attacker, _) = f.Fight();
        (long replacement, _) = f.Fight();
        f.Step();
        Assert.True(f.Client.TryGetCombatPreparation(attacker, out var before));
        var ended = new List<CombatPreparationEnded>();
        f.Client.CombatPreparationEnded += ended.Add;
        TileCombatResolveTests.Lock(f.Server, attacker, replacement);
        f.Step();
        Assert.True(f.Client.TryGetCombatPreparation(attacker, out var after));
        Assert.Equal(before.AttackId + 1, after.AttackId);
        Assert.Equal(before.AttackId, Assert.Single(ended).AttackId);
        Assert.Equal(TileCombatPreparationEndReason.TargetChanged, ended[0].Reason);
    }

    [Fact]
    public void Disconnect_clears_preparation_state_before_callback_and_reconnect_starts_clean()
    {
        using var f = new PreparationDeliveryScenario();
        (long attacker, _) = f.Fight();
        f.Step();
        bool disconnected = false;
        f.Client.Disconnected += () =>
        {
            disconnected = true;
            Assert.False(f.Client.TryGetCombatPreparation(attacker, out _));
        };
        f.Server.Kick(0, TileServerReason.Kicked);
        f.Client.Poll();
        Assert.True(disconnected);
        var next = f.AddClient();
        Assert.True(next.IsJoined);
        Assert.True(next.TryGetCombatPreparation(attacker, out var current));
        Assert.True(f.Server.TryGetCombatPreparation(attacker, out var authoritative));
        Assert.Equal(authoritative, current);
    }

    [Fact]
    public void Removed_remote_followed_by_delayed_terminal_never_recreates_a_sample()
    {
        var ledger = new TileCombatPreparationLedger();
        var preparation = Sample(1, 1);
        ledger.ApplyState(new(10, new[] { preparation }));
        ledger.Forget(1);
        Assert.False(ledger.TryGet(1, out _));
        ledger.ApplyState(new(13, Array.Empty<TileCombatPreparation>()));
        var batch = ledger.ApplyTerminals(new(13, new[] { Result(1, 1) }));
        Assert.Single(batch.Results);
        Assert.False(ledger.TryGet(1, out _));
        Assert.Empty(ledger.ApplyTerminals(new(13, new[] { Result(1, 1) })).Results);
        ledger.ApplyState(new(10, new[] { preparation }));
        Assert.False(ledger.TryGet(1, out _));
    }

    [Fact]
    public void Retired_sample_terminal_remains_deliverable_but_stale_frames_never_resurrect_it()
    {
        var ledger = new TileCombatPreparationLedger();
        ledger.ApplyState(new(10, new[] { Sample(1, 1) }));
        ledger.ApplyState(new(13, Array.Empty<TileCombatPreparation>()));
        Assert.Single(ledger.ApplyTerminals(new(13, new[] { Result(1, 1) })).Results);
        ledger.ApplyState(new(14, Array.Empty<TileCombatPreparation>()));
        Assert.Empty(ledger.ApplyTerminals(new(13, new[] { Result(1, 1) })).Results);
        ledger.ApplyState(new(10, new[] { Sample(1, 1) }));
        Assert.False(ledger.TryGet(1, out _));
    }

    [Fact]
    public void Older_revision_at_a_newer_tick_does_not_replace_the_current_sample()
    {
        var ledger = new TileCombatPreparationLedger();
        var newer = Sample(1, 1) with { Revision = 2, ImpactTick = 16 };
        ledger.ApplyState(new(10, new[] { newer }));
        ledger.ApplyState(new(11, new[] { Sample(1, 1) }));
        Assert.True(ledger.TryGet(1, out var actual));
        Assert.Equal(newer, actual);
        ledger.ApplyState(new(13, new[] { newer }));
        Assert.Empty(ledger.ApplyTerminals(new(13, new[] { Result(1, 1) })).Results);
    }

    [Fact]
    public void Newer_attempt_state_does_not_consume_an_older_attempt_terminal_at_the_same_tick()
    {
        var ledger = new TileCombatPreparationLedger();
        ledger.ApplyState(new(10, new[] { Sample(1, 1) }));
        ledger.ApplyState(new(13, new[] { Sample(1, 2) with { PrepareTick = 24, ImpactTick = 27 } }));
        Assert.Single(ledger.ApplyTerminals(new(13, new[] { Result(1, 1) })).Results);
        Assert.True(ledger.TryGet(1, out var active));
        Assert.Equal(2UL, active.AttackId);
    }

    [Fact]
    public void Lifecycle_history_is_bounded_when_terminal_only_attackers_churn()
    {
        var ledger = new TileCombatPreparationLedger();
        for (int tick = 1; tick <= 1000; tick++)
        {
            ledger.ApplyState(new(tick, Array.Empty<TileCombatPreparation>()));
            var terminal = Result(tick, 1) with { ImpactTick = tick };
            Assert.Single(ledger.ApplyTerminals(new(tick, new[] { terminal })).Results);
            Assert.Equal(1, ledger.HistoryCount);
            Assert.Empty(ledger.ApplyTerminals(new(tick - 1, new[] { terminal with { ImpactTick = tick - 1 } })).Results);
        }
        ledger.Clear();
        Assert.Equal(0, ledger.HistoryCount);
    }

    [Fact]
    public void Remote_removal_clears_the_sample_before_a_delayed_terminal_without_recreating_it()
    {
        using var f = new PreparationDeliveryScenario();
        (long attacker, _) = f.Fight();
        f.Step();
        Assert.True(f.Client.TryGetCombatPreparation(attacker, out _));
        Assert.True(f.Server.DespawnActor(attacker));
        var ended = new List<CombatPreparationEnded>();
        f.Client.CombatPreparationEnded += ended.Add;
        f.Step();
        Assert.False(f.Client.TryGetCombatPreparation(attacker, out _));
        Assert.Single(ended);
        foreach (byte[] frame in f.Payloads())
            if (frame[0] == 5) f.Inject(frame);
        f.Client.Poll();
        Assert.Single(ended);
        Assert.False(f.Client.TryGetCombatPreparation(attacker, out _));
    }

    static TileCombatPreparation Sample(long attacker, ulong id) => new(attacker, 99, id, 1, 7, 10, 13, 1, 14);
    static TileCombatTerminal Result(long attacker, ulong id) => new(attacker, 99, id, 1, 7, 13,
        TileCombatTerminalKind.Resolved, TileCombatPreparationEndReason.None, 5, 7, 1);
}
