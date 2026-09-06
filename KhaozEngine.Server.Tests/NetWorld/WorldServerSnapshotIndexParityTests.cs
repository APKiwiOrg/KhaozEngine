using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Byte-identity acceptance for <see cref="WorldServer"/>'s non-delta fallback serve path (the single-world
/// "every client gets a full snapshot" line in <see cref="WorldServer.Tick"/>), which now resolves each client's
/// area-of-interest set off a <see cref="WorldSnapshotIndex"/> rebuilt at most once per tick and shared across
/// every fallback client served that tick, instead of the old per-client full-world scan. The wire the server
/// actually sends must stay byte-identical to what the OLD full-scan
/// <see cref="SnapshotWriter.WriteFiltered(World, ReplicationRegistry, IReadOnlySet{long}, ReplicationChannels, long?)"/>
/// would have produced against the same post-tick world and (owner, interest-set) pair. The underlying indexed
/// API itself is already proven byte-identical generically in <c>SnapshotWriterIndexedParityTests</c>
/// (KhaozEngine.Tests/Replication). This pins the specific WorldServer call-site adoption end to end: shared index
/// reuse across several clients in one tick, lazy per-tick rebuild, and correctness across roster churn.
/// </summary>
public class WorldServerSnapshotIndexParityTests
{
    private static readonly Func<float, float, float> Flat = (x, z) => 0f;
    private const float Dt = 1f / 30f;

    // A huge interest radius so every client's AoI resolves to literally every NetId+ReplicatedPosition entity in
    // the world (players and NPCs alike). This removes InterestGrid bucketing from the comparison entirely, so the
    // test isolates the one thing that changed: how WriteFiltered resolves that set (index vs. full scan).
    private const float HugeRadius = 1_000_000f;

    private static WorldServer NewServer(InMemoryTransportHub hub, int maxPlayers) =>
        new(hub.Server,
            new WorldServerConfig { TickSeconds = Dt, InterestRadius = HugeRadius, MaxPlayers = maxPlayers, DeltaReplication = false },
            Flat, MoveTuning.Default);

    // A minimal client that captures the raw snapshot body bytes the server sent it on its last Poll, plus the
    // envelope's local net id: the exact two things needed to recompute the reference via the old full-scan API.
    // DeltaReplication is off server-wide in these tests, so every client (this one never advertises DeltaCapable
    // anyway) always rides the fallback full-snapshot path this file is pinning.
    private sealed class CapturingClient
    {
        private readonly NetClient net;
        public long LocalNetId { get; private set; } = -1;
        public byte[]? LastSnapshot { get; private set; }

        public CapturingClient(INetTransport transport) => net = new NetClient(transport, TestHandshake.Wire());

        public void SendMove(int seq, in MoveCommand cmd) =>
            net.Send(MoveProtocol.EncodeMove(seq, cmd), NetChannelReliability.ReliableOrdered);

        public void Poll()
        {
            net.Poll();
            while (net.TryDequeueEvent(out ClientSessionEvent ev))
            {
                if (ev.Kind != ClientSessionEventKind.Data) continue;
                if (!MoveProtocol.TryDecodeServerFrame(ev.Data, out MoveProtocol.ServerFrameKind kind, out byte[] payload)) continue;
                if (kind != MoveProtocol.ServerFrameKind.Snapshot) continue;
                if (!MoveProtocol.TryDecodeSnapshotFrame(payload, out long ln, out _, out byte[] snap)) continue;
                LocalNetId = ln;
                LastSnapshot = snap;
            }
        }
    }

    // The reference interest set: every NetId entity in `world` that also carries ReplicatedPosition - the same
    // membership rule WorldServer.Tick uses to build its InterestGrid. Under HugeRadius that is exactly what every
    // client's interest.Query returns. Recomputed this way (rather than poking the private InterestGrid) so the
    // reference stays independent of the grid's own implementation.
    private static HashSet<long> AllPositionedNetIds(World world)
    {
        var ids = new HashSet<long>();
        world.ForEach<NetId>((Entity e, ref NetId id) => { if (world.TryGet(e, out ReplicatedPosition _)) ids.Add(id.Value); });
        return ids;
    }

    [Theory]
    [InlineData(1UL, 3)]
    [InlineData(2UL, 5)]
    [InlineData(7UL, 8)]
    public void FallbackSnapshot_MatchesOldFullScan_AcrossRandomizedMovement(ulong seed, int clientCount)
    {
        var hub = new InMemoryTransportHub();
        WorldServer server = NewServer(hub, maxPlayers: clientCount + 2);
        var rng = new DeterministicRng(seed);
        var clients = new List<CapturingClient>();
        var seqs = new List<int>();
        for (int i = 0; i < clientCount; i++)
        {
            clients.Add(new CapturingClient(hub.CreateClient()));
            seqs.Add(0);
        }

        // A couple of server-owned NPCs so the fallback path also resolves non-player NetId entities through the
        // index, not just players.
        server.SpawnEntity(10f, -5f);
        server.SpawnEntity(-20f, 30f);

        const int ticks = 40;
        for (int t = 0; t < ticks; t++)
        {
            server.Poll();
            for (int i = 0; i < clients.Count; i++)
            {
                if (rng.NextFloat() < 0.7f)
                {
                    var mv = new Vector2(rng.NextFloat() * 2f - 1f, rng.NextFloat() * 2f - 1f);
                    bool run = rng.NextFloat() < 0.5f;
                    float yaw = rng.NextFloat() * MathF.Tau;
                    clients[i].SendMove(seqs[i]++, new MoveCommand(mv, run, yaw));
                }
            }
            server.Tick(Dt);
            foreach (CapturingClient c in clients) c.Poll();

            World world = server.World;
            HashSet<long> allIds = AllPositionedNetIds(world);
            foreach (CapturingClient c in clients)
            {
                if (c.LastSnapshot is null || c.LocalNetId < 0) continue; // join hasn't landed yet this early tick
                byte[] expected =
                    SnapshotWriter.WriteFiltered(world, server.Registry, allIds, ReplicationChannels.Replicate, c.LocalNetId);
                Assert.Equal(expected, c.LastSnapshot);
            }
        }
    }

    [Fact]
    public void FallbackSnapshot_MatchesOldFullScan_AcrossJoinLeaveChurn()
    {
        // Roster churn (a slot leaving, a fresh one joining later) exercises that the shared per-tick index is
        // rebuilt fresh every tick rather than reused stale across ticks: a leave must drop that entity from every
        // remaining client's next snapshot, and a join must appear in it, with the indexed path still matching the
        // old full-scan reference exactly.
        var hub = new InMemoryTransportHub();
        WorldServer server = NewServer(hub, maxPlayers: 6);
        var rng = new DeterministicRng(99UL);

        var aTransport = hub.CreateClient();
        var a = new CapturingClient(aTransport);
        var b = new CapturingClient(hub.CreateClient());
        var clients = new List<CapturingClient> { a, b };
        var seqs = new List<int> { 0, 0 };

        void Verify()
        {
            World world = server.World;
            HashSet<long> allIds = AllPositionedNetIds(world);
            foreach (CapturingClient c in clients)
            {
                if (c.LastSnapshot is null || c.LocalNetId < 0) continue;
                byte[] expected =
                    SnapshotWriter.WriteFiltered(world, server.Registry, allIds, ReplicationChannels.Replicate, c.LocalNetId);
                Assert.Equal(expected, c.LastSnapshot);
            }
        }

        for (int t = 0; t < 10; t++)
        {
            server.Poll();
            foreach ((CapturingClient c, int idx) in new[] { (a, 0), (b, 1) })
            {
                var mv = new Vector2(rng.NextFloat() * 2f - 1f, rng.NextFloat() * 2f - 1f);
                c.SendMove(seqs[idx]++, new MoveCommand(mv, run: false, cameraYaw: 0f));
            }
            server.Tick(Dt);
            a.Poll();
            b.Poll();
            Verify();
        }

        // A departs. Only B remains in the roster.
        hub.DisconnectClient(aTransport);
        clients.Remove(a);

        for (int t = 0; t < 5; t++)
        {
            server.Poll();
            server.Tick(Dt);
            b.Poll();
            Verify();
        }

        // A fresh client C joins the now-smaller roster.
        var c2 = new CapturingClient(hub.CreateClient());
        clients.Add(c2);
        int c2Seq = 0;

        for (int t = 0; t < 10; t++)
        {
            server.Poll();
            var mv = new Vector2(rng.NextFloat() * 2f - 1f, rng.NextFloat() * 2f - 1f);
            b.SendMove(seqs[1]++, new MoveCommand(mv, run: false, cameraYaw: 0f));
            c2.SendMove(c2Seq++, new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f));
            server.Tick(Dt);
            b.Poll();
            c2.Poll();
            Verify();
        }
    }
}
