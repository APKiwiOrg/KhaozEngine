using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Netcode;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TilePreparationDeliveryTests
{
    [Fact]
    public void Successor_snapshot_does_not_consume_the_previous_terminal()
    {
        using var f = new PreparationDeliveryScenario();
        (long attacker, _) = f.Fight();
        int awards = 0, legacy = 0;
        var results = new List<PreparedCombatEvent>();
        f.Server.OnCombatEvent += _ => awards++;
        f.Client.CombatEvent += _ => legacy++;
        f.Client.PreparedCombatEvent += results.Add;
        f.Step();
        Assert.True(f.Client.TryGetCombatPreparation(attacker, out var old));
        f.Through(old.ImpactTick - 1);
        f.Wire.Sent.Clear();
        f.Step();
        Assert.Equal(new byte[] { 0, 4, 5 }, f.Payloads().Select(x => x[0]));
        Assert.All(f.Wire.Sent, x => Assert.Equal(NetChannelReliability.ReliableOrdered, x.Reliability));
        Assert.Equal(1, awards);
        Assert.Equal(0, legacy);
        Assert.Equal(old.AttackId, Assert.Single(results).AttackId);
        Assert.True(f.Client.TryGetCombatPreparation(attacker, out var next));
        Assert.Equal(old.AttackId + 1, next.AttackId);
    }

    [Fact]
    public void Prepared_callbacks_can_reenter_poll_without_loss_or_duplication()
    {
        using var f = new PreparationDeliveryScenario();
        f.Fight(); f.Fight();
        var delivered = new List<(long, ulong)>();
        bool reentered = false;
        f.Client.PreparedCombatEvent += e =>
        {
            delivered.Add((e.Outcome.AttackerNetId, e.AttackId));
            if (!reentered) { reentered = true; f.Client.Poll(); }
        };
        f.Through(f.Server.TickCount + 18, poll: false);
        byte[][] frames = f.Payloads().Where(x => x[0] == 5).ToArray();
        f.Client.Poll();
        Assert.Equal(4, delivered.Count);
        Assert.Equal(4, delivered.Distinct().Count());
        foreach (byte[] frame in frames) f.Inject(frame);
        f.Client.Poll();
        Assert.Equal(4, delivered.Count);
    }

    [Fact]
    public void Disabled_mode_emits_only_legacy_combat_frames()
    {
        using var f = new PreparationDeliveryScenario(enabled: false);
        f.Fight();
        int legacy = 0, prepared = 0;
        f.Client.CombatEvent += _ => legacy++;
        f.Client.PreparedCombatEvent += _ => prepared++;
        f.Step();
        Assert.Equal(1, legacy);
        Assert.Equal(0, prepared);
        Assert.DoesNotContain(f.Payloads(), x => x[0] is 4 or 5);
    }

    [Fact]
    public void Repeated_complete_states_notify_at_their_tick_and_old_revisions_cannot_replace_new()
    {
        using var f = new PreparationDeliveryScenario();
        (long attacker, _) = f.Fight();
        var ticks = new List<long>();
        f.Client.CombatPreparationsChanged += ticks.Add;
        f.Step();
        byte[] original = f.Payloads().Last(x => x[0] == 4);
        Assert.True(f.Server.DelayAttack(attacker, 2));
        f.Step();
        Assert.True(f.Client.TryGetCombatPreparation(attacker, out var revised));
        Assert.Equal(2U, revised.Revision);
        f.Inject(original);
        f.Client.Poll();
        Assert.True(f.Client.TryGetCombatPreparation(attacker, out var after));
        Assert.Equal(revised, after);
        Assert.Equal(2, ticks.Count);
        f.Step();
        Assert.Equal(3, ticks.Count);
    }

    [Fact]
    public void Malformed_partial_set_preserves_published_state_and_clears_assembly()
    {
        using var f = new PreparationDeliveryScenario();
        (long attacker, _) = f.Fight();
        f.Step();
        Assert.True(f.Client.TryGetCombatPreparation(attacker, out var before));
        long tick = f.Server.TickCount;
        var record = before with { ImpactTick = tick + 3, PrepareTick = tick };
        f.Inject(TileProtocol.EncodePreparationChunk(new(tick, 0, 2), new[] { record }, 0, 1));
        f.Inject(new byte[] { 4, 99 });
        f.Client.Poll();
        Assert.Equal(1, f.Client.RejectedCombatPreparationFrameCount);
        Assert.True(f.Client.TryGetCombatPreparation(attacker, out var after));
        Assert.Equal(before, after);
        f.Inject(TileProtocol.EncodePreparationChunk(new(tick, 0, 1), new[] { record }, 0, 1));
        f.Client.Poll();
        Assert.True(f.Client.TryGetCombatPreparation(attacker, out after));
        Assert.Equal(record, after);
    }

    [Fact]
    public void All_chunks_at_one_tick_apply_atomically_and_deliver_every_terminal_once()
    {
        using var f = new PreparationDeliveryScenario();
        long tick = f.Server.TickCount;
        var records = Enumerable.Range(1, 256).Select(i => new TileCombatPreparation(1000 + i, 999,
            1, 1, 7, tick, tick + 3, 1, 14)).ToArray();
        f.Inject(TileProtocol.EncodePreparationChunk(new(tick, 0, 2), records, 0, 255));
        f.Client.Poll();
        Assert.False(f.Client.TryGetCombatPreparation(1001, out _));
        f.Inject(TileProtocol.EncodePreparationChunk(new(tick, 1, 2), records, 255, 1));
        f.Client.Poll();
        Assert.True(f.Client.TryGetCombatPreparation(1256, out _));
        var terminals = records.Select(x => new TileCombatTerminal(x.AttackerNetId, x.TargetNetId,
            x.AttackId, x.Revision, x.PresentationKey, tick + 3, TileCombatTerminalKind.Resolved,
            TileCombatPreparationEndReason.None, 5, 7, 1)).ToArray();
        int delivered = 0;
        f.Client.PreparedCombatEvent += _ => delivered++;
        f.Inject(TileProtocol.EncodePreparationChunk(new(tick + 3, 0, 1), Array.Empty<TileCombatPreparation>(), 0, 0));
        f.Inject(TileProtocol.EncodePreparationTerminalChunk(new(tick + 3, 0, 2), terminals, 0, 255));
        f.Client.Poll();
        Assert.Equal(0, delivered);
        f.Inject(TileProtocol.EncodePreparationTerminalChunk(new(tick + 3, 1, 2), terminals, 255, 1));
        f.Client.Poll();
        Assert.Equal(256, delivered);
        Assert.False(f.Client.TryGetCombatPreparation(1001, out _));
    }

    [Fact]
    public void Oversized_state_set_is_refused_atomically_for_only_its_viewer()
    {
        using var f = new PreparationDeliveryScenario();
        TileWorldClient other = f.AddClient();
        (long attacker, _) = f.Fight();
        f.Step();
        Assert.True(f.Client.TryGetCombatPreparation(attacker, out _));
        f.Wire.Sent.Clear();
        string? notice = null;
        f.Client.NoticeReceived += reason => notice = reason;
        Assert.False(f.Server.SendPreparationFrameSet(0, new Oversized<TileCombatPreparation>(), Array.Empty<TileCombatTerminal>()));
        f.Client.Poll();
        Assert.DoesNotContain(f.Payloads(), x => x[0] is 4 or 5);
        Assert.Equal(1L, f.Server.RejectedCombatPreparationFrameSetCount);
        Assert.Equal(new NetConnectionId(1), Assert.Single(f.Wire.Disconnected));
        Assert.Equal(TileServerReason.CombatPreparationOverflow, notice);
        Assert.False(f.Client.IsJoined);
        Assert.False(f.Client.TryGetCombatPreparation(attacker, out _));
        f.Through(f.Server.TickCount + 4);
        Assert.True(other.IsJoined);
        Assert.NotEmpty(f.Rules.Rolls);
        Assert.True(other.TryGetCombatPreparation(attacker, out _));
    }

    [Fact]
    public void Oversized_terminal_set_is_refused_before_even_the_empty_state_is_sent()
    {
        using var f = new PreparationDeliveryScenario();
        f.Wire.Sent.Clear();
        Assert.False(f.Server.SendPreparationFrameSet(0, Array.Empty<TileCombatPreparation>(), new Oversized<TileCombatTerminal>()));
        Assert.DoesNotContain(f.Payloads(), x => x[0] is 4 or 5);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    [InlineData(65280, 1)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void Enabled_boot_refuses_invalid_minimum_budgets(int players, int actors)
    {
        var hub = new InMemoryTransportHub();
        var doc = TileMoveSimulatorTests.FlatWorld(4, new RegionCoord(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TileWorldServer(hub.Server,
            TileWorldServerTickTests.Config(new TileCoord(20, 20, 0)) with
            { MaxPlayers = players, MaxActorsPerCell = actors, CombatPreparationRules = new PreparationScenario.Profiles() },
            TileMoveSimulatorTests.Bake(doc), new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator()));
    }

    [Fact]
    public void Interleaved_non_preparation_frame_refuses_the_pending_set()
    {
        using var f = new PreparationDeliveryScenario();
        long tick = f.Server.TickCount;
        var first = new TileCombatPreparation(1001, 999, 1, 1, 7, tick, tick + 3, 1, 14);
        var second = first with { AttackerNetId = 1002 };
        f.Inject(TileProtocol.EncodePreparationChunk(new(tick, 0, 2), new[] { first }, 0, 1));
        f.Inject(TileProtocol.EncodeNotice("test:interleaved"));
        f.Inject(TileProtocol.EncodePreparationChunk(new(tick, 1, 2), new[] { second }, 0, 1));
        f.Client.Poll();
        Assert.False(f.Client.TryGetCombatPreparation(1001, out _));
        Assert.False(f.Client.TryGetCombatPreparation(1002, out _));
        Assert.Equal(2, f.Client.RejectedCombatPreparationFrameCount);
    }

    [Fact]
    public void Phase_offset_harness_adopts_only_when_explicitly_enabled()
    {
        var spawn = new TileCoord(20, 20, 0);
        var doc = TileMoveSimulatorTests.FlatWorld(4, new RegionCoord(0, 0));
        using var h = new TileCombatHarness(doc, spawn,
            config: TileWorldServerTickTests.Config(spawn) with { CombatPreparationRules = new PreparationScenario.Profiles() },
            combatPreparationEnabled: true);
        h.Server.CombatRules = new PreparationScenario.FixedRules();
        h.Frames(6);
        Assert.True(h.Server.SetHealth(h.Client.LocalNetId, new TileHealth { Current = 100, Max = 100 }));
        long target = h.Server.SpawnActor(new TileCoord(20, 21, 0), new TileActorSpawn(1000, 14, TileDirection.N));
        h.Client.Queue(TileCommand.Attack(target, TileMoveMode.Walk));
        h.Frames(6);
        Assert.True(h.Server.TryGetCombatPreparation(h.Client.LocalNetId, out var server));
        Assert.True(h.Client.TryGetCombatPreparation(h.Client.LocalNetId, out var client));
        Assert.Equal(server, client);
    }

    [Fact]
    public void Whole_set_validation_happens_before_any_chunk_is_sent()
    {
        using var f = new PreparationDeliveryScenario();
        long tick = f.Server.TickCount;
        var records = Enumerable.Range(1, 256).Select(i => new TileCombatPreparation(1000 + i, 999,
            1, 1, 7, tick, tick + 3, 1, 14)).ToArray();
        records[255] = records[254];
        f.Wire.Sent.Clear();
        Assert.Throws<ArgumentException>(() => f.Server.SendPreparationFrameSet(0, records, Array.Empty<TileCombatTerminal>()));
        Assert.Empty(f.Wire.Sent);
    }

    [Fact]
    public void Maximum_sized_state_set_is_sent_completely_and_boot_boundary_is_accepted()
    {
        using var f = new PreparationDeliveryScenario();
        long tick = f.Server.TickCount;
        var records = Enumerable.Range(1, TileProtocol.MaxPreparationRecords).Select(i => new TileCombatPreparation(1000 + i, 999,
            1, 1, 7, tick, tick + 3, 1, 14)).ToArray();
        f.Wire.Sent.Clear();
        Assert.True(f.Server.SendPreparationFrameSet(0, records, Array.Empty<TileCombatTerminal>()));
        Assert.Equal(256, f.Payloads().Length);
        f.Client.Poll();
        Assert.True(f.Client.TryGetCombatPreparation(1000 + TileProtocol.MaxPreparationRecords, out _));
        var hub = new InMemoryTransportHub();
        var doc = TileMoveSimulatorTests.FlatWorld(4, new RegionCoord(0, 0));
        using var server = new TileWorldServer(hub.Server,
            TileWorldServerTickTests.Config(new TileCoord(20, 20, 0)) with
            { MaxPlayers = 1, MaxActorsPerCell = 65279, CombatPreparationRules = new PreparationScenario.Profiles() },
            TileMoveSimulatorTests.Bake(doc), new TileDocumentTargets(doc, TileMoveSimulatorTests.Catalogs), new AllowAllAuthenticator());
    }

    [Fact]
    public void Overflow_after_impact_does_not_break_other_viewers_terminal_order()
    {
        using var f = new PreparationDeliveryScenario();
        var other = f.AddClient();
        Assert.True(f.Server.SetHealth(f.Client.LocalNetId, new TileHealth { Current = 100, Max = 100 }));
        (long attacker, _) = f.Fight();
        TileCombatResolveTests.Lock(f.Server, attacker, f.Client.LocalNetId);
        f.Step();
        Assert.True(f.Client.TryGetCombatPreparation(attacker, out var before));
        var results = new List<PreparedCombatEvent>();
        other.PreparedCombatEvent += results.Add;
        var ended = new List<CombatPreparationEnded>();
        other.CombatPreparationEnded += ended.Add;
        f.Through(before.ImpactTick - 1);
        f.Wire.AfterSend = (connection, frame) =>
        {
            if (connection.Value != 1 || SessionFrame.ReadOpcode(frame) != SessionOpcode.Data
                || SessionFrame.ReadBody(frame)[0] != TileProtocol.ServerFrameSnapshot) return;
            f.Wire.AfterSend = null;
            Assert.False(f.Server.SendPreparationFrameSet(0, new Oversized<TileCombatPreparation>(), Array.Empty<TileCombatTerminal>()));
        };
        f.Step();
        Assert.True(other.IsJoined);
        Assert.Equal(before.AttackId, Assert.Single(results).AttackId);
        Assert.False(f.Client.IsJoined);
        Assert.Empty(ended);
        Assert.True(other.TryGetCombatPreparation(attacker, out var successor));
        f.Step();
        Assert.False(other.TryGetCombatPreparation(attacker, out _));
        Assert.Equal(successor.AttackId, Assert.Single(ended).AttackId);
        Assert.Equal(before.ImpactTick + 1, ended[0].ServerTick);
    }

    sealed class Oversized<T> : IReadOnlyList<T>
    {
        public int Count => TileProtocol.MaxPreparationRecords + 1;
        public T this[int index] => throw new InvalidOperationException("An oversized set must never be indexed.");
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
