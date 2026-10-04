using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The opt-in disconnect linger on <see cref="ShardedWorldServer"/>: a transport drop the game grants <c>n</c> ticks
/// keeps the body joined on its held slot, stepped on the neutral command and served to others, for exactly those
/// ticks, then leaves once through the ordinary path. Server-initiated closes and drops after a drain never linger.
/// </summary>
public class ShardedWorldServerLingerTests
{
    internal const float Dt = 1f / 30f;

    // One server on an in-memory hub, with a linger hook that answers LingerTicks and counts its calls, and an event
    // log that interleaves PlayerLeaving with OnBeforeTick so ordering inside a tick is checkable.
    internal sealed class Rig : IDisposable
    {
        public readonly InMemoryTransportHub Hub = new();
        public readonly ShardedWorldServer Server;
        public readonly List<string> Log = new();
        public int LingerTicks;
        public int HookCalls;
        public int Leaves;

        public Rig(int? lingerTicks, AntiCheatConfig? antiCheat = null, int maxInputBacklog = 8, bool log = true)
        {
            LingerTicks = lingerTicks ?? 0;
            var config = new ShardedWorldServerConfig
            {
                TickSeconds = Dt,
                MaxPlayers = 4,
                MaxInputBacklog = maxInputBacklog,
                AntiCheat = antiCheat ?? new AntiCheatConfig(),
                DisconnectLingerTicks = lingerTicks is null ? null : (_, _) => { HookCalls++; return LingerTicks; },
            };
            Server = new ShardedWorldServer(Hub.Server, config, (_, _) => 0f, MoveTuning.Default);
            Server.PlayerLeaving += (slot, _, _) => { Leaves++; Log.Add($"leaving:{slot}"); };
            if (log) Server.OnBeforeTick += _ => Log.Add($"before:{Server.ServerTick}");
        }

        public int JoinRaw(string account, out NetClient client, out INetTransport transport)
        {
            transport = Hub.CreateClient();
            client = new NetClient(transport, TestHandshake.Wire(account));
            client.Poll();
            Server.Poll();
            client.Poll();
            Assert.True(client.Slot >= 0);
            Assert.True(Server.TryGetPlayerNetId(client.Slot, out _));
            return client.Slot;
        }

        public WorldClient JoinWorldClient(string account)
        {
            var client = new WorldClient(Hub.CreateClient(), (_, _) => 0f, MoveTuning.Default,
                new WorldClientConfig { TickSeconds = Dt }, token: Encoding.UTF8.GetBytes(account));
            for (int i = 0; i < 30; i++) Step(client);
            return client;
        }

        public void Step(WorldClient? observer = null)
        {
            Server.Poll();
            Server.Tick(Dt);
            observer?.Poll();
        }

        public bool BodyAlive(long netId) => Server.Host.TryGetOwner(netId, out _, out _);

        public void Dispose()
        {
            Server.Dispose();
            Hub.Dispose();
        }
    }

    private static bool Renders(WorldClient client, long netId)
    {
        foreach (EntityRenderState e in client.Snapshot())
            if (e.Id.Value == netId) return true;
        return false;
    }

    [Fact]
    public void DefaultOffLeavesOnThePollThatSeesTheDrop()
    {
        using var rig = new Rig(lingerTicks: null);
        int slot = rig.JoinRaw("a", out _, out INetTransport transport);
        Assert.True(rig.Server.TryGetPlayerNetId(slot, out long netId));

        rig.Hub.DisconnectClient(transport);
        rig.Server.Poll();

        Assert.False(rig.Server.TryGetPlayerNetId(slot, out _));
        Assert.False(rig.Server.IsLingering(slot));
        Assert.False(rig.BodyAlive(netId));
        Assert.Equal(1, rig.Leaves);
    }

    [Fact]
    public void ALingeringBodyStepsExactlyItsTicksThenLeavesOnce()
    {
        using var rig = new Rig(lingerTicks: 3);
        rig.JoinRaw("b", out _, out _);
        int slot = rig.JoinRaw("a", out _, out INetTransport transport);
        Assert.True(rig.Server.TryGetPlayerNetId(slot, out long netId));
        rig.Step();
        rig.Step();

        rig.Hub.DisconnectClient(transport);
        rig.Server.Poll();
        long k = rig.Server.ServerTick;

        Assert.Equal(1, rig.HookCalls);
        Assert.True(rig.Server.IsLingering(slot));
        Assert.True(rig.Server.TryGetPlayerNetId(slot, out long lingering));
        Assert.Equal(netId, lingering);
        Assert.Equal(0, rig.Leaves);

        for (long t = k + 1; t <= k + 3; t++)
        {
            rig.Step();
            Assert.Equal(t, rig.Server.ServerTick);
            Assert.True(rig.Server.IsLingering(slot), $"tick {t}");
            Assert.True(rig.BodyAlive(netId), $"tick {t}");
            Assert.Equal(0, rig.Leaves);
        }

        rig.Log.Clear();
        rig.Step();

        Assert.Equal(k + 4, rig.Server.ServerTick);
        Assert.Equal(new[] { $"leaving:{slot}", $"before:{k + 4}" }, rig.Log);
        Assert.False(rig.BodyAlive(netId));
        Assert.False(rig.Server.IsLingering(slot));
        Assert.False(rig.Server.TryGetPlayerNetId(slot, out _));

        for (int i = 0; i < 5; i++) rig.Step();
        Assert.Equal(1, rig.Leaves);
        Assert.Equal(1, rig.HookCalls);
    }

    [Fact]
    public void ALingeringBodyIsServedToOthers()
    {
        using var rig = new Rig(lingerTicks: 5);
        int slot = rig.JoinRaw("a", out _, out INetTransport transport);
        Assert.True(rig.Server.TryGetPlayerNetId(slot, out long netId));
        WorldClient b = rig.JoinWorldClient("b");
        Assert.Equal(2, rig.Server.PlayerCount);
        Assert.True(Renders(b, netId), "B must see A before the drop");

        rig.Hub.DisconnectClient(transport);
        rig.Server.Poll();
        Assert.True(rig.Server.IsLingering(slot));

        for (int t = 1; t <= 5; t++)
        {
            rig.Server.Tick(Dt);
            b.Poll();
            Assert.True(Renders(b, netId), $"linger tick {t}");
            rig.Server.Poll();
        }

        rig.Server.Tick(Dt);
        b.Poll();
        Assert.Equal(1, rig.Leaves);
        Assert.False(Renders(b, netId), "A's body left on this tick");
    }

    // Review Focus 2. Input the client queued before the link dropped reaches the server in the same drain as the
    // drop. It must not drive the lingering body: the body steps on the neutral command.
    [Fact]
    public void ALingeringBodyIgnoresInputQueuedBeforeTheDrop()
    {
        float lingering = XTravelAfterTenRightCommands(drop: true);
        float control = XTravelAfterTenRightCommands(drop: false);

        Assert.True(lingering < 0.05f, $"lingering body moved {lingering} m on input queued before the drop");
        Assert.True(control >= 0.5f, $"control body moved only {control} m");
    }

    private static float XTravelAfterTenRightCommands(bool drop)
    {
        // Strict oldest-first order, so the control queue drives ten ticks of movement instead of one catch-up step.
        using var rig = new Rig(lingerTicks: 40, maxInputBacklog: 0);
        int slot = rig.JoinRaw("a", out NetClient client, out INetTransport transport);
        for (int i = 0; i < 5; i++) rig.Step();
        Assert.True(rig.Server.TryGetPlayerState(slot, out PlayerMoveState start));

        var right = new MoveCommand(new Vector2(1f, 0f), false, 0f);
        for (int seq = 0; seq < 10; seq++)
            client.Send(MoveProtocol.EncodeMove(seq, right), NetChannelReliability.ReliableOrdered);
        if (drop) rig.Hub.DisconnectClient(transport);

        for (int i = 0; i < 30; i++) rig.Step();

        Assert.Equal(drop, rig.Server.IsLingering(slot));
        Assert.True(rig.Server.TryGetPlayerState(slot, out PlayerMoveState end));
        return MathF.Abs(end.Position.X - start.Position.X);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ANonPositiveLingerLeavesAtOnce(int n)
    {
        using var rig = new Rig(lingerTicks: n);
        int slot = rig.JoinRaw("a", out _, out INetTransport transport);
        Assert.True(rig.Server.TryGetPlayerNetId(slot, out long netId));

        rig.Hub.DisconnectClient(transport);
        rig.Server.Poll();

        Assert.Equal(1, rig.HookCalls);
        Assert.False(rig.Server.IsLingering(slot));
        Assert.False(rig.Server.TryGetPlayerNetId(slot, out _));
        Assert.False(rig.BodyAlive(netId));
        Assert.Equal(1, rig.Leaves);
    }

    [Fact]
    public void ServerClosesNeverLinger()
    {
        AssertServerCloseLeavesAtOnce(null, (rig, slot, _, _) => rig.Server.Disconnect(slot));
        AssertServerCloseLeavesAtOnce(null, (rig, slot, _, _) => rig.Server.Disconnect(slot, "test-kick"));
        AssertServerCloseLeavesAtOnce(null, (rig, slot, _, _) =>
        {
            Assert.True(rig.Server.TryGetAccountId(slot, out string account));
            rig.Server.Kick(PlayerRef.Account(account), "bye");
            rig.Server.Tick(Dt);
        });
        AssertServerCloseLeavesAtOnce(
            new AntiCheatConfig { MaxMessagesPerSecond = 30f, MessageBurst = 3f, DisconnectOnRateLimit = true },
            (rig, _, client, _) =>
            {
                var idle = new MoveCommand(Vector2.Zero, false, 0f);
                for (int seq = 0; seq < 20; seq++)
                    client.Send(MoveProtocol.EncodeMove(seq, idle), NetChannelReliability.UnreliableSequenced);
                rig.Server.Poll();   // trips the limiter, which closes the connection
            });
        AssertServerCloseLeavesAtOnce(null, (rig, _, _, transport) =>
        {
            rig.Server.BeginDrain(new ServerNotice(ServerNoticeKind.Custom, "restart"), 60f);
            rig.Hub.DisconnectClient(transport);
        });
        AssertServerCloseLeavesAtOnce(null, (rig, _, _, transport) =>
        {
            rig.Server.BeginDrain(new ServerNotice(ServerNoticeKind.Custom, "restart"), 0.1f);
            for (int i = 0; i < 30 && !rig.Server.IsDrainComplete; i++) rig.Server.Tick(Dt);
            Assert.True(rig.Server.IsDrainComplete);
            Assert.False(rig.Server.IsDraining);   // a completed drain still never lingers
            rig.Hub.DisconnectClient(transport);
        });
    }

    private static void AssertServerCloseLeavesAtOnce(AntiCheatConfig? antiCheat,
        Action<Rig, int, NetClient, INetTransport> close)
    {
        using var rig = new Rig(lingerTicks: 300, antiCheat);
        int slot = rig.JoinRaw("a", out NetClient client, out INetTransport transport);
        Assert.True(rig.Server.TryGetPlayerNetId(slot, out long netId));

        close(rig, slot, client, transport);
        rig.Server.Poll();   // the transport's Disconnected for the closed connection
        rig.Server.Poll();

        Assert.Equal(0, rig.HookCalls);
        Assert.False(rig.Server.IsLingering(slot));
        Assert.False(rig.Server.TryGetPlayerNetId(slot, out _));
        Assert.False(rig.BodyAlive(netId));
        Assert.Equal(1, rig.Leaves);
    }

    [Fact]
    public void KickByAccountEndsALingeringBody()
    {
        using var rig = new Rig(lingerTicks: 300);
        rig.JoinRaw("b", out _, out _);
        int slot = rig.JoinRaw("a", out _, out INetTransport transport);
        Assert.True(rig.Server.TryGetPlayerNetId(slot, out long netId));
        Assert.True(rig.Server.TryGetAccountId(slot, out string account));
        rig.Hub.DisconnectClient(transport);
        rig.Server.Poll();
        Assert.True(rig.Server.IsLingering(slot));

        rig.Server.Kick(PlayerRef.Account(account), "bye");
        rig.Server.Tick(Dt);

        Assert.Equal(1, rig.Leaves);
        Assert.False(rig.BodyAlive(netId));
        Assert.False(rig.Server.IsLingering(slot));
        int reused = rig.JoinRaw("c", out _, out _);
        Assert.Equal(slot, reused);
        Assert.Equal(1, rig.Leaves);
    }

    [Fact]
    public void ALingeringSlotStaysOnTheRoster()
    {
        using var rig = new Rig(lingerTicks: 300);
        rig.JoinRaw("b", out _, out _);
        int slot = rig.JoinRaw("a", out _, out INetTransport transport);
        Assert.True(rig.Server.TryGetPlayerNetId(slot, out long netId));
        rig.Hub.DisconnectClient(transport);
        rig.Server.Poll();
        rig.Server.Tick(Dt);

        Assert.True(rig.Server.IsLingering(slot));
        Assert.Contains(slot, rig.Server.JoinedSlots);
        Assert.Equal(2, rig.Server.PlayerCount);
        Assert.Contains(rig.Server.ListOnline(), p => p.Slot == slot && p.NetId == netId);
    }
}

/// <summary>A tick whose only joined body is lingering allocates nothing of its own once warm.</summary>
[Collection("AllocSensitive")]
public sealed class ShardedWorldServerLingerAllocationTests
{
    // ShardedWorldServer.Tick already allocates with no players at all once a cell is live, and more per entity the
    // cell holds (the cell step, handoffs, ghost sync and cell enumeration, near 2 KB a tick with one entity here), so
    // a strict zero cannot hold. The linger's own cost is pinned instead: the same 50 ticks with the body lingering
    // allocate exactly what they allocate on the same server and cell once the body has left, no player is joined
    // and one non-player entity stands where the body stood.
    [Fact]
    public void TickWithOnlyALingeringBodyAllocatesNothing()
    {
        const float dt = ShardedWorldServerLingerTests.Dt;
        using var rig = new ShardedWorldServerLingerTests.Rig(lingerTicks: 100_000, log: false);
        int slot = rig.JoinRaw("a", out _, out INetTransport transport);
        rig.Hub.DisconnectClient(transport);
        rig.Server.Poll();
        Assert.True(rig.Server.IsLingering(slot));
        for (int i = 0; i < 10; i++) rig.Server.Tick(dt);

        long lingering = Measure(() => { for (int i = 0; i < 50; i++) rig.Server.Tick(dt); });
        long retryLingering = Measure(() => { for (int i = 0; i < 50; i++) rig.Server.Tick(dt); });
        Assert.True(rig.Server.IsLingering(slot));
        Assert.True(rig.Server.TryGetPlayerState(slot, out PlayerMoveState body));

        rig.Server.Disconnect(slot);
        Assert.Equal(0, rig.Server.PlayerCount);
        rig.Server.SpawnEntity(body.Position.X, body.Position.Z);
        for (int i = 0; i < 10; i++) rig.Server.Tick(dt);
        long baseline = Measure(() => { for (int i = 0; i < 50; i++) rig.Server.Tick(dt); });
        long retryBaseline = Measure(() => { for (int i = 0; i < 50; i++) rig.Server.Tick(dt); });

        // Each side is measured twice and any equal pair passes, because one-time lazy work (a first use of a code
        // path or a collection growing to its working size) can still land in either side's first window.
        Assert.True(
            lingering == baseline || lingering == retryBaseline
                || retryLingering == baseline || retryLingering == retryBaseline,
            $"50 ticks with a lingering body allocated {lingering} then {retryLingering} bytes, against {baseline} "
                + $"then {retryBaseline} with no player and one entity");
    }

    private static long Measure(Action loop)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        loop();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
