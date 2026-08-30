using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The end-of-tick online-snapshot publish (#134). The serve loop used to hand
/// <c>AdminCommandBuffer.Publish</c> a freshly built list plus a <c>ToArray</c> copy on EVERY tick, whether or not
/// anything an admin can see had changed and whether or not anything was reading it. Now the rebuild goes into a
/// reused buffer and a new array is published only when the content actually differs.
/// <para>The observable proof is instance identity: an unchanged tick republishes nothing, so
/// <see cref="WorldServer.ListOnline"/> keeps handing back the same array. What it CONTAINS must still be exactly
/// what an unconditional rebuild would have produced, which is the second half of these tests: a move on the very
/// next tick shows up immediately, so the documented "at most one tick stale" contract is untouched.</para>
/// </summary>
public class WorldServerOnlineSnapshotTests
{
    private static float Flat(float x, float z) => 0f;

    private static WorldServer JoinOne(out NetClient client, out WorldServerConfig config, out int slot)
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        client = new NetClient(ct, TestHandshake.Wire("alice"));
        for (int i = 0; i < 200 && server.PlayerCount == 0; i++) { client.Poll(); server.Poll(); server.Tick(config.TickSeconds); }
        server.Tick(config.TickSeconds);   // publish the online snapshot
        slot = server.JoinedSlots.First();
        return server;
    }

    [Fact]
    public void IdleTicks_DoNotRepublishTheOnlineSnapshot()
    {
        WorldServer server = JoinOne(out _, out WorldServerConfig config, out _);
        IReadOnlyList<OnlinePlayer> first = server.ListOnline();
        Assert.Single(first);

        // Nothing joins, leaves or moves, so nothing an admin can read has changed: no rebuild is published and the
        // reader keeps the array it already had. That identity IS the "allocates nothing this tick" assertion.
        for (int i = 0; i < 10; i++) { server.Poll(); server.Tick(config.TickSeconds); }

        Assert.Same(first, server.ListOnline());
    }

    [Fact]
    public void AMoveIsPublishedOnTheVeryNextTick()
    {
        WorldServer server = JoinOne(out _, out WorldServerConfig config, out int slot);
        for (int i = 0; i < 5; i++) { server.Poll(); server.Tick(config.TickSeconds); }
        IReadOnlyList<OnlinePlayer> before = server.ListOnline();

        server.Teleport(PlayerRef.Slot(slot), new Vector3(120f, 0f, 240f));
        server.Poll();
        server.Tick(config.TickSeconds);

        IReadOnlyList<OnlinePlayer> after = server.ListOnline();
        Assert.NotSame(before, after);
        Assert.Single(after);
        Assert.Equal(120f, after[0].Position.X, 3);
        Assert.Equal(240f, after[0].Position.Z, 3);
    }

    [Fact]
    public void AJoinIsPublishedEvenWhenNobodyMoved()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        IReadOnlyList<OnlinePlayer> empty = server.ListOnline();
        Assert.Empty(empty);

        // An empty world stays on the empty snapshot however many ticks run: membership is what changed, not
        // position, and membership is exactly what the equality gate has to notice.
        for (int i = 0; i < 5; i++) { server.Poll(); server.Tick(config.TickSeconds); }
        Assert.Same(empty, server.ListOnline());

        var client = new NetClient(ct, TestHandshake.Wire("bob"));
        for (int i = 0; i < 200 && server.PlayerCount == 0; i++) { client.Poll(); server.Poll(); server.Tick(config.TickSeconds); }
        server.Tick(config.TickSeconds);

        IReadOnlyList<OnlinePlayer> joined = server.ListOnline();
        Assert.NotSame(empty, joined);
        Assert.Single(joined);
        Assert.Equal("bob", joined[0].AccountId);
    }

    [Fact]
    public void ALeaveIsPublished()
    {
        WorldServer server = JoinOne(out NetClient client, out WorldServerConfig config, out int slot);
        Assert.Single(server.ListOnline());

        server.Kick(PlayerRef.Slot(slot), "bye");
        for (int i = 0; i < 60 && server.PlayerCount > 0; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        server.Tick(config.TickSeconds);

        Assert.Empty(server.ListOnline());
    }
}
