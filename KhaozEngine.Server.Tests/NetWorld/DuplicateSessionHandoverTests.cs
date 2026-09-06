using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// What the join gate hands the persistence layer, on a store where a WRITE costs more than a READ (#662). A kick
/// runs the displaced session's save-on-leave and the newcomer's load-on-join out of one event drain, and nothing at
/// the store orders the two against each other, so the read used to win: the newcomer was restored onto the record
/// as it stood BEFORE the leave, undoing everything the displaced session had done, and the next periodic save wrote
/// that rollback down as the truth. An ordinary drop and a fast rejoin is the same shape without the kick.
///
/// <para>The rows drive it through the real loopback stack over a <see cref="SlowSaveWorldStore"/>, which parks every
/// write until the row releases it and answers reads straight through, so the window is explicit rather than timed.
/// A synchronous store cannot show any of this: <see cref="GatedWorldStore"/> parks the other half.</para>
/// </summary>
public class DuplicateSessionHandoverTests
{
    private const float Dt = 1f / 30f;
    private const string Alpha = "alpha";

    // Where alpha's stored record says it is at the start of every row.
    private static readonly Vector3 Stored = new(333f, 0f, -333f);
    // And where the session actually gets to before it is displaced. 564 metres from the record, so a restore that
    // reads the pre-leave bytes is unmistakable rather than a rounding argument.
    private static readonly Vector3 Moved = new(-90f, 0f, 40f);

    private static float Flat(float x, float z) => 0f;

    private static float PlanarDistance(Vector3 a, Vector3 b) =>
        Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));

    private sealed class Rig
    {
        public required InMemoryTransportHub Hub { get; init; }
        public required SlowSaveWorldStore Store { get; init; }
        public required InMemoryWorldStore Inner { get; init; }
        public required WorldPersistence Persistence { get; init; }
        public required WorldServer Server { get; init; }

        private readonly List<NetClient> clients = new();
        private readonly List<INetTransport> transports = new();

        public NetClient Connect(string account)
        {
            INetTransport t = Hub.CreateClient();
            var client = new NetClient(t, TestHandshake.Wire(account));
            clients.Add(client);
            transports.Add(t);
            return client;
        }

        public void Drop(NetClient client) => Hub.DisconnectClient(transports[clients.IndexOf(client)]);

        /// <summary>Runs server frames until <paramref name="until"/> holds, draining each client's session events.</summary>
        public void Pump(Func<bool> until, int frames = 400)
        {
            for (int i = 0; i < frames && !until(); i++)
            {
                foreach (NetClient c in clients)
                {
                    c.Poll();
                    while (c.TryDequeueEvent(out _)) { }
                }
                Server.Poll();
                Server.Tick(Dt);
                Persistence.Update(Dt);
            }
        }

        /// <summary>Runs a fixed number of frames, for a row that has to give a WRONG apply every chance to land.</summary>
        public void PumpFrames(int frames) => Pump(() => false, frames);

        public PlayerMoveState State(int slot)
        {
            Assert.True(Server.TryGetPlayerState(slot, out PlayerMoveState s), $"slot {slot} has no live player");
            return s;
        }

        /// <summary>The only slot with a live player, asserting there is exactly one.</summary>
        public int OnlySlot()
        {
            Assert.Single(Server.JoinedSlots);
            foreach (int slot in Server.JoinedSlots) return slot;
            throw new InvalidOperationException("unreachable");
        }

        /// <summary>Lets the parked writes through and settles everything waiting behind them.</summary>
        public async Task SettleAsync()
        {
            Store.ReleaseSaves();
            await Persistence.FlushAsync();
            Persistence.Update(0f);
        }

        /// <summary>What alpha's record says now, read straight from the wrapped store.</summary>
        public async Task<Vector3> StoredPositionAsync()
        {
            byte[]? data = await Inner.LoadAsync("player:" + Alpha);
            Assert.NotNull(data);
            return PlayerRecord.Decode(data!).ToState().Position;
        }
    }

    private static async Task<Rig> BuildAsync()
    {
        var inner = new InMemoryWorldStore();
        await inner.SaveAsync("player:" + Alpha, PlayerRecord.From(new PlayerMoveState { Position = Stored },
            Encoding.UTF8.GetBytes("alpha-blob")).Encode());
        var store = new SlowSaveWorldStore(inner);
        var hub = new InMemoryTransportHub();
        var server = new WorldServer(hub.Server, new WorldServerConfig
        {
            TickSeconds = Dt,
            InterestRadius = 500f,
            MaxPlayers = 8,
            SpawnPosition = _ => Vector3.Zero,
        }, Flat, MoveTuning.Default);
        var persistence = new WorldPersistence(server, store, new WorldPersistenceConfig
        {
            SaveIntervalSeconds = 999f,   // no periodic pass runs on its own
        });
        return new Rig { Hub = hub, Store = store, Inner = inner, Persistence = persistence, Server = server };
    }

    // Joins alpha, settles its restore, then plays it out to a position the stored record knows nothing about.
    // Hands back the joined client, which one row goes on to displace and the other simply drops.
    private static NetClient StartedAndMoved(Rig rig)
    {
        NetClient first = rig.Connect(Alpha);
        rig.Pump(() => first.Slot >= 0 && rig.Server.JoinedSlots.Count == 1);
        rig.Pump(() => PlanarDistance(rig.State(0).Position, Stored) < 1f);
        Assert.True(PlanarDistance(rig.State(0).Position, Stored) < 1f,
            $"the first session was never restored ({rig.State(0).Position})");
        Assert.Equal(0, rig.Store.CompletedSaves);            // reads are free here, writes have not started
        rig.Server.SetPlayerState(0, new PlayerMoveState { Position = Moved }, teleport: true);
        return first;
    }

    [Fact]
    public async Task A_kick_handover_restores_where_the_displaced_session_actually_was()
    {
        Rig rig = await BuildAsync();
        NetClient first = StartedAndMoved(rig);

        NetClient second = rig.Connect(Alpha);                 // the same account, from somewhere else
        rig.Pump(() => second.Slot >= 0);
        Assert.Equal(-1, first.Slot);                          // displaced, and no longer naming the seat it lost

        // The displaced session's save-on-leave is in the air, and the newcomer's load has to wait behind it. Reading
        // now would hand this session the pre-leave record and roll the account back 564 metres.
        Assert.Equal(1, rig.Store.PendingSaves);
        Assert.Equal(0, rig.Store.CompletedSaves);
        rig.PumpFrames(5);                                     // give a wrong restore every chance to land
        int slot = rig.OnlySlot();
        Assert.True(PlanarDistance(rig.State(slot).Position, Moved) < 1f,
            $"the newcomer was restored from the record as it stood before the leave ({rig.State(slot).Position})");

        await rig.SettleAsync();

        Assert.True(PlanarDistance(rig.State(slot).Position, Moved) < 1f,
            $"the restore behind the save landed on stale bytes ({rig.State(slot).Position})");
        Assert.True(PlanarDistance(await rig.StoredPositionAsync(), Moved) < 1f,
            "the account's record should hold where the displaced session actually was");
    }

    [Fact]
    public async Task A_rejoin_inside_the_write_latency_waits_for_its_own_leave_save()
    {
        // The same guarantee without the kick: an ordinary drop, then a rejoin fast enough to beat the write. It
        // comes free with the handover fix, and it is the case a player on a flaky link hits without anyone else
        // being involved at all.
        Rig rig = await BuildAsync();
        NetClient first = StartedAndMoved(rig);

        rig.Drop(first);
        rig.Pump(() => rig.Server.JoinedSlots.Count == 0);
        Assert.Equal(1, rig.Store.PendingSaves);               // the leave-save is parked
        Assert.Equal(0, rig.Store.CompletedSaves);

        NetClient again = rig.Connect(Alpha);
        rig.Pump(() => again.Slot >= 0);
        rig.PumpFrames(5);
        int slot = rig.OnlySlot();
        Assert.True(PlanarDistance(rig.State(slot).Position, Moved) < 1f,
            $"the rejoin read the record from before its own leave ({rig.State(slot).Position})");

        await rig.SettleAsync();

        Assert.True(PlanarDistance(rig.State(slot).Position, Moved) < 1f,
            $"the restore behind the save landed on stale bytes ({rig.State(slot).Position})");
        Assert.True(PlanarDistance(await rig.StoredPositionAsync(), Moved) < 1f,
            "the account's record should hold where the session actually was");
    }
}
