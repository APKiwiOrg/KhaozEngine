using System;
using System.Collections.Generic;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileCombatWireTests
{
    sealed class FlatRules : ITileCombatRules
    {
        public ushort Damage = 6;
        public byte Ticks = 4;
        public TileAttackOutcome Roll(in TileAttackContext context) => TileAttackOutcome.Hit(Damage, 3);
        public byte AttackTicks(long attackerNetId) => Ticks;
    }

    static readonly TileCombatEvent[] Sample =
    {
        new(AttackerNetId: 0x0001_0000_0000_002AL, TargetNetId: 9L, Amount: 12, Kind: 3, Landed: true, Killed: false),
        new(AttackerNetId: 9L, TargetNetId: 0x0001_0000_0000_002AL, Amount: 0, Kind: 0, Landed: false, Killed: false),
        new(AttackerNetId: 4L, TargetNetId: 5L, Amount: 31, Kind: 1, Landed: true, Killed: true),
    };

    [Fact]
    public void A_combat_frame_round_trips_its_events_and_both_flag_bits()
    {
        byte[] frame = TileProtocol.EncodeCombat(Sample);
        Assert.Equal(TileProtocol.ServerFrameCombat, TileProtocol.ServerFrameTag(frame));
        Assert.Equal(2 + 3 * 20, frame.Length);

        var back = new List<TileCombatEvent>();
        Assert.True(TileProtocol.TryDecodeCombat(frame, back));
        Assert.Equal(Sample, back);
    }

    [Fact]
    public void An_empty_combat_frame_decodes_to_no_events()
    {
        byte[] frame = TileProtocol.EncodeCombat(Array.Empty<TileCombatEvent>());
        Assert.Equal(2, frame.Length);
        var back = new List<TileCombatEvent> { Sample[0] };
        Assert.True(TileProtocol.TryDecodeCombat(frame, back));
        Assert.Empty(back);
    }

    // Total, on the same grounds as every other decoder here: every byte came off a socket, and one that threw would
    // hand a remote peer a way to kill the receiving loop with one bad packet.
    [Fact]
    public void The_combat_decoder_refuses_every_truncation_a_wrong_tag_and_a_lying_count()
    {
        byte[] frame = TileProtocol.EncodeCombat(Sample);
        var back = new List<TileCombatEvent>();

        for (int cut = 0; cut < frame.Length; cut++)
            Assert.False(TileProtocol.TryDecodeCombat(frame.AsSpan(0, cut), back), $"a {cut} byte frame decoded");

        byte[] mistagged = (byte[])frame.Clone();
        mistagged[0] = TileProtocol.ServerFrameSnapshot;
        Assert.False(TileProtocol.TryDecodeCombat(mistagged, back));

        byte[] lying = (byte[])frame.Clone();
        lying[1] = 9;                                  // says nine events behind three events' worth of bytes
        Assert.False(TileProtocol.TryDecodeCombat(lying, back));
        lying[1] = 1;                                  // and the other direction, fewer than the bytes present
        Assert.False(TileProtocol.TryDecodeCombat(lying, back));

        Assert.False(TileProtocol.TryDecodeCombat(ReadOnlySpan<byte>.Empty, back));
    }

    // Over the cap is a LOCAL bug worth a stack, exactly as an over-long game-message payload is. The count rides in
    // one byte, so 255 is the wire's own ceiling rather than a policy.
    [Fact]
    public void Encoding_more_than_the_cap_throws()
    {
        var many = new TileCombatEvent[TileProtocol.MaxCombatEvents + 1];
        Assert.Throws<ArgumentException>(() => TileProtocol.EncodeCombat(many));
        Assert.Throws<ArgumentNullException>(() => TileProtocol.EncodeCombat(null!));
    }

    // The PAD RULE, asserted as the property it actually is: no encodable combat frame ever shares a length with a
    // command frame, so a demux that keyed on length still could not confuse the two.
    [Fact]
    public void No_combat_frame_ever_shares_a_length_with_a_command_frame()
    {
        int commandFrameSize = TileProtocol.EncodeCommand(0, TileCommand.None).Length;
        for (int count = 0; count <= 4; count++)
        {
            byte[] frame = TileProtocol.EncodeCombat(new TileCombatEvent[count]);
            Assert.NotEqual(commandFrameSize, frame.Length);
            var back = new List<TileCombatEvent>();
            Assert.True(TileProtocol.TryDecodeCombat(frame, back));
            Assert.Equal(count, back.Count);
        }
    }

    // End to end: a client clicks Attack on a server-driven actor, walks to it, and sees the same swings the server
    // rolled. Health arrives on the snapshot and the swings arrive on their own frame, which is the whole reason the
    // frame exists: two hits on one tick collapse into one health delta and a MISS moves health by zero.
    [Fact]
    public void A_client_attacking_an_actor_receives_the_swings_the_server_rolled()
    {
        using var h = new TileCombatHarness(TileMoveSimulatorTests.FlatWorld(), new TileCoord(20, 20, 0));
        h.Server.CombatRules = new FlatRules { Damage = 6, Ticks = 4 };
        h.Frames(8);
        // A PLAYER is spawned with no health at all, deliberately: what a player's health is belongs to the game's
        // own skill core rather than to the door. The roll phase skips an attacker that has none, so a player who
        // was never given one can never swing, and this test would watch a fight that never starts.
        Assert.True(h.Server.SetHealth(h.Client.LocalNetId, new TileHealth { Current = 100, Max = 100 }));
        long actor = h.Server.SpawnActor(new TileCoord(20, 24, 0), new TileActorSpawn(200, 4, TileDirection.S));
        h.Frames(8);

        var seen = new List<TileCombatEvent>();
        h.Client.CombatEvent += ev => seen.Add(ev);
        h.Client.Queue(TileCommand.Attack(actor, TileMoveMode.Run));
        h.Frames(120);

        Assert.NotEmpty(seen);
        Assert.All(seen, ev =>
        {
            Assert.Equal(h.Client.LocalNetId, ev.AttackerNetId);
            Assert.Equal(actor, ev.TargetNetId);
            Assert.True(ev.Landed);
            Assert.Equal(6, ev.Amount);
            Assert.Equal(3, ev.Kind);
        });
        Assert.True(h.Server.TryGetHealth(actor, out TileHealth hp));
        Assert.Equal(200 - 6 * seen.Count, hp.Current);
    }

    // Only the events whose TARGET is in that viewer's interest set, so an ordinary tick costs nothing and a fight
    // on the far side of the world costs nothing either.
    [Fact]
    public void An_event_whose_target_is_outside_a_viewers_interest_is_not_sent_to_that_viewer()
    {
        using var h = new TileCombatHarness(TileMoveSimulatorTests.FlatWorld(), new TileCoord(5, 5, 0));
        h.Server.CombatRules = new FlatRules();
        h.Frames(8);

        // Two actors fighting each other far outside the client's interest radius, which defaults to 15 tiles.
        long a = h.Server.SpawnActor(new TileCoord(55, 55, 0), new TileActorSpawn(200, 4, TileDirection.S));
        long b = h.Server.SpawnActor(new TileCoord(55, 56, 0), new TileActorSpawn(200, 4, TileDirection.S));
        h.Frames(4);
        h.Server.Actors.Command(a, TileCommand.Attack(b, TileMoveMode.Walk));

        var onServer = new List<TileCombatEvent>();
        var onClient = new List<TileCombatEvent>();
        h.Server.OnCombatEvent += ev => onServer.Add(ev);
        h.Client.CombatEvent += ev => onClient.Add(ev);
        h.Frames(60);

        // The server really did resolve swings, so the client seeing none is a FILTER rather than a fight that never
        // happened. Without this the test would pass on a server that stopped fighting altogether.
        Assert.NotEmpty(onServer);
        Assert.Empty(onClient);
        Assert.DoesNotContain(a, h.Client.RemoteNetIds);
    }

    // The diff is already computed every frame inside RefreshRemoteSamples and was simply not surfaced. Surfacing it
    // is what a per-monster hitsplat stack keys its prune on.
    [Fact]
    public void RemoteEntered_and_RemoteLeft_fire_once_each_around_an_actor_life()
    {
        using var h = new TileCombatHarness(TileMoveSimulatorTests.FlatWorld(), new TileCoord(20, 20, 0));
        h.Frames(8);

        var entered = new List<long>();
        var left = new List<long>();
        h.Client.RemoteEntered += id => entered.Add(id);
        h.Client.RemoteLeft += id => left.Add(id);

        long actor = h.Server.SpawnActor(new TileCoord(22, 20, 0), new TileActorSpawn(30, 4, TileDirection.S));
        h.Frames(12);
        Assert.Equal(new[] { actor }, entered);
        Assert.Empty(left);
        Assert.Contains(actor, h.Client.RemoteNetIds);

        Assert.True(h.Server.DespawnActor(actor));
        h.Frames(12);
        Assert.Equal(new[] { actor }, entered);
        Assert.Equal(new[] { actor }, left);
        Assert.DoesNotContain(actor, h.Client.RemoteNetIds);
    }

    // The approach to a MOVING target RECONCILES rather than diverging, and the corrections are counted so a later
    // change that makes them worse is visible as a number rather than as a feeling.
    [Fact]
    public void A_client_approaching_a_moving_actor_reconciles_and_never_hard_snaps()
    {
        using var h = new TileCombatHarness(TileMoveSimulatorTests.FlatWorld(), new TileCoord(20, 20, 0));
        h.Server.CombatRules = new FlatRules();
        h.Frames(8);
        long actor = h.Server.SpawnActor(new TileCoord(20, 30, 0), new TileActorSpawn(500, 40, TileDirection.S));
        h.Frames(8);

        h.Client.Queue(TileCommand.Attack(actor, TileMoveMode.Run));
        for (int leg = 0; leg < 6; leg++)
        {
            // Keep the target walking, so the client is predicting an approach to something that keeps moving.
            h.Server.Actors.Command(actor,
                TileCommand.WalkTo(new TileCoord(20 + (leg % 2 == 0 ? 6 : 0), 30, 0), TileMoveMode.Walk));
            h.Frames(30);
        }

        Assert.Equal(0, h.Client.SnapCount);
        Assert.True(h.Client.CorrectionCount <= 12,
            $"the approach reconciled cheaply, corrections were {h.Client.CorrectionCount}");
        Assert.True(h.Server.TryGetPlayerState(0, out TileMoveState server));
        Assert.Equal(server.Tile, h.Client.Prediction.PredictedState.Tile);
    }
}
