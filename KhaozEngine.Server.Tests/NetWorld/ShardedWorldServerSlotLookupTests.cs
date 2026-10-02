using System;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ShardedWorldServerSlotLookupTests
{
    private static ShardedWorldServer CreateServer(InMemoryTransportHub hub) =>
        new(hub.Server, new ShardedWorldServerConfig { MaxPlayers = 4 }, (_, _) => 0f, MoveTuning.Default);

    private static int Join(ShardedWorldServer server, InMemoryTransportHub hub, string account,
        out INetTransport transport)
    {
        transport = hub.CreateClient();
        var client = new NetClient(transport, TestHandshake.Wire(account));
        client.Poll();
        server.Poll();
        client.Poll();
        Assert.True(client.Slot >= 0);
        Assert.True(server.TryGetPlayerNetId(client.Slot, out _));
        return client.Slot;
    }

    [Fact]
    public void Join_ResolvesNetIdBeforePlayerJoinedCallback()
    {
        using var hub = new InMemoryTransportHub();
        ShardedWorldServer server = CreateServer(hub);
        server.EnsureNextNetIdAtLeast(1L << 40);
        int callbackSlot = -1;
        server.PlayerJoined += (slot, _) =>
        {
            Assert.True(server.TryGetPlayerNetId(slot, out long netId));
            Assert.True(netId >= 1L << 40);
            Assert.True(server.TryGetSlot(netId, out callbackSlot));
            Assert.Equal(slot, callbackSlot);
        };

        int joinedSlot = Join(server, hub, "player", out _);

        Assert.Equal(joinedSlot, callbackSlot);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    public void UnknownNetId_ReturnsFalseWithDefaultSlot(long netId)
    {
        using var hub = new InMemoryTransportHub();
        ShardedWorldServer server = CreateServer(hub);

        Assert.False(server.TryGetSlot(netId, out int slot));
        Assert.Equal(0, slot);
    }

    [Fact]
    public void NonPlayerEntity_HasNoSlot()
    {
        using var hub = new InMemoryTransportHub();
        ShardedWorldServer server = CreateServer(hub);
        Join(server, hub, "player", out _);
        long entityNetId = server.SpawnEntity(5f, 5f);

        Assert.True(server.Host.TryGetOwner(entityNetId, out _, out _));
        Assert.False(server.TryGetSlot(entityNetId, out int slot));
        Assert.Equal(0, slot);
    }

    [Fact]
    public void TransportDisconnect_RemovesOnlyDepartedPlayerLookup()
    {
        using var hub = new InMemoryTransportHub();
        ShardedWorldServer server = CreateServer(hub);
        int departedSlot = Join(server, hub, "departed", out INetTransport departedTransport);
        int remainingSlot = Join(server, hub, "remaining", out _);
        Assert.True(server.TryGetPlayerNetId(departedSlot, out long departedNetId));
        Assert.True(server.TryGetPlayerNetId(remainingSlot, out long remainingNetId));
        Assert.True(server.TryGetSlot(departedNetId, out _));

        hub.DisconnectClient(departedTransport);
        server.Poll();

        Assert.False(server.TryGetPlayerNetId(departedSlot, out _));
        Assert.False(server.TryGetSlot(departedNetId, out int goneSlot));
        Assert.Equal(0, goneSlot);
        Assert.True(server.TryGetSlot(remainingNetId, out int resolvedSlot));
        Assert.Equal(remainingSlot, resolvedSlot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ServerDisconnect_RemovesLookupImmediately(bool withReason)
    {
        using var hub = new InMemoryTransportHub();
        ShardedWorldServer server = CreateServer(hub);
        int slot = Join(server, hub, "player", out _);
        Assert.True(server.TryGetPlayerNetId(slot, out long netId));
        Assert.True(server.TryGetSlot(netId, out _));

        if (withReason) server.Disconnect(slot, "test-kick");
        else server.Disconnect(slot);

        Assert.False(server.TryGetSlot(netId, out int goneSlot));
        Assert.Equal(0, goneSlot);
        server.Poll();
        Assert.False(server.TryGetSlot(netId, out _));
    }

    [Fact]
    public void LeaveAndJoinInSamePoll_ReusedSlotResolvesOnlyNewNetId()
    {
        using var hub = new InMemoryTransportHub();
        ShardedWorldServer server = CreateServer(hub);
        int oldSlot = Join(server, hub, "old-player", out INetTransport oldTransport);
        Assert.True(server.TryGetPlayerNetId(oldSlot, out long oldNetId));

        hub.DisconnectClient(oldTransport);
        int newSlot = Join(server, hub, "new-player", out _);

        Assert.Equal(oldSlot, newSlot);
        Assert.True(server.TryGetPlayerNetId(newSlot, out long newNetId));
        Assert.NotEqual(oldNetId, newNetId);
        Assert.False(server.TryGetSlot(oldNetId, out _));
        Assert.True(server.TryGetSlot(newNetId, out int resolvedSlot));
        Assert.Equal(newSlot, resolvedSlot);
        Assert.Equal(1, server.PlayerCount);
    }

    [Fact]
    public void ThrowingPlayerLeavingCallback_StillRemovesLookup()
    {
        using var hub = new InMemoryTransportHub();
        ShardedWorldServer server = CreateServer(hub);
        int slot = Join(server, hub, "player", out _);
        Assert.True(server.TryGetPlayerNetId(slot, out long netId));
        Assert.True(server.TryGetSlot(netId, out _));
        server.PlayerLeaving += (_, _, _) => throw new InvalidOperationException("leave callback failed");

        Assert.Throws<InvalidOperationException>(() => server.Disconnect(slot));

        Assert.False(server.TryGetPlayerNetId(slot, out _));
        Assert.False(server.TryGetSlot(netId, out _));
    }
}
