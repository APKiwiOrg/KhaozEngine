using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Netcode;
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

    // Every swing distinguishable from every other, which is what lets the re-entrancy test below see a clobber at
    // all: with identical events a lost swing and a duplicated one cancel out and every count still matches.
    sealed class RisingRules : ITileCombatRules
    {
        ushort next = 1;
        public TileAttackOutcome Roll(in TileAttackContext context) => TileAttackOutcome.Hit(next++, 3);
        public byte AttackTicks(long attackerNetId) => 1;
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

    // The CLIENT half of the target-0 refusal, which the server-side test cannot see: Admit's two halves must
    // match to the letter, and without the client clause a locally queued Attack(0) is predicted as accepted,
    // clearing the predicted lock for the frames until the snapshot corrects it. CorrectionCount cannot see
    // that flicker because the position never moves, so the predicted lock itself is the observable.
    [Fact]
    public void A_target_zero_attack_is_refused_by_the_client_too_so_the_predicted_lock_never_flickers()
    {
        using var h = new TileCombatHarness(TileMoveSimulatorTests.FlatWorld(), new TileCoord(20, 20, 0));
        h.Server.CombatRules = new FlatRules { Damage = 6, Ticks = 4 };
        h.Frames(8);
        Assert.True(h.Server.SetHealth(h.Client.LocalNetId, new TileHealth { Current = 100, Max = 100 }));
        long actor = h.Server.SpawnActor(new TileCoord(20, 24, 0), new TileActorSpawn(200, 4, TileDirection.S));
        h.Frames(8);
        h.Client.Queue(TileCommand.Attack(actor, TileMoveMode.Run));
        h.Frames(30);
        Assert.Equal(actor, h.Client.Prediction.PredictedState.CombatTarget);

        h.Client.Queue(TileCommand.Attack(0, TileMoveMode.Run));
        for (int i = 0; i < 12; i++)
        {
            h.Frames(1);
            Assert.Equal(actor, h.Client.Prediction.PredictedState.CombatTarget);
        }
    }

    // A DEATH, END TO END, which is the one thing the Killed bit exists for: a head learns a monster died from the
    // blow that killed it rather than from noticing an absence, and an absence cannot be told apart from a walk out
    // of interest anyway. The count equality is the assertion that earns its keep here, and it is the one that goes
    // red when the serve builds its interest set after the corpse has already left the world: every swing the server
    // rolled has to arrive, and the killing one is exactly the swing a despawn-before-serve drops.
    [Fact]
    public void A_client_that_kills_an_actor_receives_the_killing_blow_with_its_Killed_bit()
    {
        using var h = new TileCombatHarness(TileMoveSimulatorTests.FlatWorld(), new TileCoord(20, 20, 0));
        h.Server.CombatRules = new FlatRules { Damage = 60, Ticks = 4 };
        h.Frames(8);
        // See the end to end test above for why a player's health is written here rather than spawned with one.
        Assert.True(h.Server.SetHealth(h.Client.LocalNetId, new TileHealth { Current = 100, Max = 100 }));
        // A hundred against sixty is two swings: one ordinary blow, then the lethal one.
        long actor = h.Server.SpawnActor(new TileCoord(20, 24, 0), new TileActorSpawn(100, 4, TileDirection.S));
        h.Frames(8);

        var onServer = new List<TileCombatEvent>();
        var onClient = new List<TileCombatEvent>();
        var left = new List<long>();
        h.Server.OnCombatEvent += ev => onServer.Add(ev);
        h.Client.CombatEvent += ev => onClient.Add(ev);
        h.Client.RemoteLeft += id => left.Add(id);

        h.Client.Queue(TileCommand.Attack(actor, TileMoveMode.Run));
        h.Frames(120);

        // The server really did kill it, so everything below is about DELIVERY rather than about a fight that never
        // ran. Without this the test would pass on a server that stopped fighting altogether.
        TileCombatEvent serverKill = Assert.Single(onServer, ev => ev.Killed);
        Assert.False(h.Server.TryGetHealth(actor, out _));

        Assert.Equal(onServer, onClient);
        TileCombatEvent clientKill = Assert.Single(onClient, ev => ev.Killed);
        Assert.Equal(serverKill, clientKill);
        Assert.Equal(h.Client.LocalNetId, clientKill.AttackerNetId);
        Assert.Equal(actor, clientKill.TargetNetId);
        Assert.True(clientKill.Landed);

        // And the corpse leaves on its own frame, so the pair a head prunes its hitsplat stack on still arrives in
        // the order it reads: the blow that says it died, then the departure.
        Assert.Equal(new[] { actor }, left);
        Assert.DoesNotContain(actor, h.Client.RemoteNetIds);
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

    // THE DRAIN IS RE-ENTRANT BY CONSTRUCTION: a head may pump the transport from inside a hitsplat handler, and
    // nothing stops it. So the raise loop has to own its events for the length of the walk, or a nested drain decodes
    // the next frame straight into the list the outer loop is still walking and every swing after the current one is
    // replaced by whatever the last nested frame carried.
    [Fact]
    public void A_combat_handler_that_re_enters_Poll_still_sees_every_swing_exactly_once()
    {
        using var h = new TileCombatHarness(TileMoveSimulatorTests.FlatWorld(), new TileCoord(20, 20, 0));
        h.Server.CombatRules = new RisingRules();
        h.Frames(8);
        // Two actors trading blows beside the client, so each tick's frame carries TWO events. A frame of ONE event
        // cannot show this at all: the outer loop is never mid walk when the nested drain refills the list.
        long a = h.Server.SpawnActor(new TileCoord(22, 20, 0), new TileActorSpawn(2000, 1, TileDirection.S));
        long b = h.Server.SpawnActor(new TileCoord(22, 21, 0), new TileActorSpawn(2000, 1, TileDirection.S));
        h.Frames(8);
        h.Server.Actors.Command(a, TileCommand.Attack(b, TileMoveMode.Walk));
        h.Server.Actors.Command(b, TileCommand.Attack(a, TileMoveMode.Walk));

        var onServer = new List<TileCombatEvent>();
        h.Server.OnCombatEvent += ev => onServer.Add(ev);
        // Three combat frames piled into one inbox, which is a head whose own frames ran slower than the server's
        // ticks. The client is deliberately not polled in between.
        for (int i = 0; i < 3; i++)
        {
            h.Server.Poll();
            h.Server.Tick(TileCombatHarness.Tick);
        }

        var seen = new List<TileCombatEvent>();
        bool reentered = false;
        h.Client.CombatEvent += ev =>
        {
            seen.Add(ev);
            if (reentered) return;
            reentered = true;
            h.Client.Poll();
        };
        h.Client.Poll();

        Assert.True(reentered, "the handler re-entered the drain");
        Assert.True(onServer.Count >= 4,
            $"the piled frames carried more than one event each, they carried {onServer.Count}");
        // Every swing the server rolled, exactly once. Ordered rather than compared as sequences, because a nested
        // drain legitimately raises the later frames first and that is not the property under test: what must not
        // happen is a swing lost or a swing raised twice.
        Assert.Equal(onServer.OrderBy(ev => ev.Amount), seen.OrderBy(ev => ev.Amount));
    }

    // The same ownership rule on the lifecycle pair, which is raised out of the presentation advance rather than out
    // of the drain. Far less plausible than the combat one and the same one line.
    [Fact]
    public void A_RemoteEntered_handler_that_re_enters_the_presentation_advance_still_sees_every_arrival()
    {
        using var h = new TileCombatHarness(TileMoveSimulatorTests.FlatWorld(), new TileCoord(20, 20, 0));
        h.Frames(8);
        // Both spawned on ONE server tick, so ONE refresh has two arrivals to raise.
        long a = h.Server.SpawnActor(new TileCoord(22, 20, 0), new TileActorSpawn(30, 4, TileDirection.S));
        long b = h.Server.SpawnActor(new TileCoord(23, 20, 0), new TileActorSpawn(30, 4, TileDirection.S));

        var entered = new List<long>();
        bool reentered = false;
        h.Client.RemoteEntered += id =>
        {
            entered.Add(id);
            if (reentered) return;
            reentered = true;
            h.Client.AdvancePresentation(TileCombatHarness.Frame);
        };
        h.Frames(12);

        Assert.True(reentered, "the handler re-entered the presentation advance");
        Assert.Equal(new[] { a, b }, entered.OrderBy(id => id));
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

    // THE RECOVERY, and what it recovers from is a world left permanently wrong rather than one tick wrong. The
    // despawn a death owes an actor is held from step 4b until step 5b, and the WHOLE serve loop sits between the
    // two, so one client's send failing loses that reap. A head that catches and keeps ticking, which is the normal
    // shape for a server loop, then has a corpse standing at zero health forever: it holds a slot against the cell's
    // actor cap, its spawner never respawns it because the id still answers, and every viewer in range is served it
    // on every tick. Draining the list at the top of the next combat pass is what gets the despawn back.
    [Fact]
    public void A_send_that_throws_out_of_the_serve_still_reaps_the_corpse_on_the_next_tick()
    {
        ThrowOnceTransport link = null!;
        using var h = new TileCombatHarness(TileMoveSimulatorTests.FlatWorld(), new TileCoord(20, 20, 0),
            wrapServer: inner => link = new ThrowOnceTransport(inner));
        h.Server.CombatRules = new FlatRules { Damage = 60, Ticks = 4 };
        h.Frames(8);
        Assert.True(h.Server.SetHealth(h.Client.LocalNetId, new TileHealth { Current = 100, Max = 100 }));
        long actor = h.Server.SpawnActor(new TileCoord(20, 24, 0), new TileActorSpawn(100, 4, TileDirection.S));
        h.Frames(8);

        // ARMED FROM THE DEATH ITSELF rather than after a fixed number of sends, because the window is one specific
        // tick's serve. OnDied is raised in phase 3 of the pass that killed the actor, so the next send is that same
        // tick's snapshot, which is exactly where the reap now sits behind.
        long killed = 0;
        h.Server.OnDied += (dead, _, _) => { killed = dead; link.ArmThrow = true; };

        h.Client.Queue(TileCommand.Attack(actor, TileMoveMode.Run));
        Assert.Throws<InvalidOperationException>(() => h.Frames(200));
        Assert.Equal(actor, killed);
        Assert.True(link.Threw, "the link failed inside the serve rather than somewhere else");

        // The tick that threw never reached 5b, so the corpse is still standing right now. That much is true under
        // either behaviour and is not the failure: the failure is that it is still standing a tick later.
        Assert.True(h.Server.TryGetActorState(actor, out _));
        Assert.True(h.Server.TryGetHealth(actor, out TileHealth corpse));
        Assert.Equal(0, corpse.Current);

        h.Frames(8);
        Assert.False(h.Server.TryGetActorState(actor, out _));
        Assert.False(h.Server.TryGetHealth(actor, out _));
        Assert.DoesNotContain(actor, h.Client.RemoteNetIds);
    }

    // A server transport that fails ONE send and then behaves, which is the recoverable error a real link hands a
    // server loop. It wraps rather than replaces the hub's own endpoint, so everything either head sends outside the
    // armed window still arrives and the test is about the reap rather than about a dead session.
    sealed class ThrowOnceTransport : INetTransport
    {
        readonly INetTransport inner;

        public ThrowOnceTransport(INetTransport inner) => this.inner = inner;

        /// <summary>Set to fail the very next send, once. Cleared by the throw it causes.</summary>
        public bool ArmThrow;

        /// <summary>Whether the armed send ever actually happened, so a test cannot pass on a throw that never
        /// fired.</summary>
        public bool Threw { get; private set; }

        public void Poll() => inner.Poll();

        public bool TryDequeueEvent(out NetEvent ev) => inner.TryDequeueEvent(out ev);

        public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
        {
            if (ArmThrow)
            {
                ArmThrow = false;
                Threw = true;
                throw new InvalidOperationException("the link failed mid serve");
            }
            inner.Send(target, payload, reliability);
        }

        public void Disconnect(NetConnectionId connection) => inner.Disconnect(connection);

        public void Dispose() => inner.Dispose();
    }
}
