using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The second position lever. <see cref="IAdminControllable.Teleport"/> always stamps the teleport epoch, which an
/// honest client treats as a hard cut (a camera cut, a streaming ring rebuild, a render-height snap), so a game
/// that CLAMPS a position every tick was advertising a discontinuity every tick. That was harmless for eight minor
/// versions and catastrophic the moment a consumer grew a reaction to the epoch (#379, found through a dead-player
/// lock that re-asserted Teleport per tick).
///
/// <para><see cref="IAdminControllable.SetPosition"/> is the same placement without the claim: it moves the player
/// and leaves the epoch exactly where it was, so nothing downstream cuts. Teleport is unchanged, and this is
/// additive: what these tests pin is that the two levers differ in the epoch and in nothing else.</para>
/// </summary>
public class ContinuousPlacementTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    static readonly MoveTuning Unit = MoveTuning.Default with { CapsuleHalfHeight = 0.5f };

    static (WorldServer server, WorldClient client, WorldServerConfig config) Connect()
    {
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        var server = new WorldServer(st, config, Flat, Unit);
        var client = new WorldClient(ct, Flat, Unit, new WorldClientConfig { TickSeconds = config.TickSeconds });
        for (int i = 0; i < 10 && !client.Joined; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);
        return (server, client, config);
    }

    static (ShardedWorldServer server, WorldClient client, ShardedWorldServerConfig config) ConnectSharded()
    {
        var config = new ShardedWorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 8 };
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var server = new ShardedWorldServer(st, config, Flat, Unit);
        var client = new WorldClient(ct, Flat, Unit, new WorldClientConfig { TickSeconds = config.TickSeconds });
        for (int i = 0; i < 30 && !client.Joined; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);
        return (server, client, config);
    }

    [Fact]
    public void SetPosition_moves_the_player_without_advancing_the_epoch()
    {
        (WorldServer server, WorldClient client, WorldServerConfig config) = Connect();
        Assert.True(server.TryGetPlayerState(0, out PlayerMoveState before));
        uint clientEpochBefore = client.LocalTeleportEpoch;
        int fired = 0;
        client.LocalTeleported += () => fired++;

        server.SetPosition(PlayerRef.Slot(0), new Vector3(12f, 0f, -6f));
        for (int i = 0; i < 5; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }

        Assert.True(server.TryGetPlayerState(0, out PlayerMoveState after));
        Assert.Equal(12f, after.Position.X, 1);
        Assert.Equal(-6f, after.Position.Z, 1);
        Assert.Equal(before.TeleportEpoch, after.TeleportEpoch);
        Assert.Equal(clientEpochBefore, client.LocalTeleportEpoch);
        Assert.Equal(0, fired);
    }

    [Fact]
    public void A_per_tick_clamp_never_advertises_a_teleport()
    {
        // The shape #379 was filed for: a game holding a body at one spot, re-asserting it every tick. With Teleport
        // that was one epoch edge per tick. Here it is none, over sixty of them.
        (WorldServer server, WorldClient client, WorldServerConfig config) = Connect();
        Assert.True(server.TryGetPlayerState(0, out PlayerMoveState before));
        int fired = 0;
        client.LocalTeleported += () => fired++;

        var hold = new Vector3(4f, 0f, 4f);
        for (int i = 0; i < 60; i++)
        {
            server.SetPosition(PlayerRef.Slot(0), hold);
            server.Poll();
            server.Tick(config.TickSeconds);
            client.Poll();
        }

        Assert.True(server.TryGetPlayerState(0, out PlayerMoveState after));
        Assert.Equal(before.TeleportEpoch, after.TeleportEpoch);
        Assert.Equal(0, fired);
        Assert.Equal(4f, after.Position.X, 1);
        Assert.Equal(4f, after.Position.Z, 1);
    }

    [Fact]
    public void Teleport_still_advertises_one()
    {
        // The additive half of the contract: nothing about Teleport changed.
        (WorldServer server, WorldClient client, WorldServerConfig config) = Connect();
        Assert.True(server.TryGetPlayerState(0, out PlayerMoveState before));

        server.Teleport(PlayerRef.Slot(0), new Vector3(12f, 0f, -6f));
        for (int i = 0; i < 5; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }

        Assert.True(server.TryGetPlayerState(0, out PlayerMoveState after));
        Assert.True(after.TeleportEpoch > before.TeleportEpoch);
    }

    [Fact]
    public void SetPosition_on_the_sharded_head_leaves_the_epoch_alone()
    {
        (ShardedWorldServer server, WorldClient client, ShardedWorldServerConfig config) = ConnectSharded();
        int slot = server.JoinedSlots.First();
        Assert.True(server.TryGetPlayerState(slot, out PlayerMoveState before));
        int fired = 0;
        client.LocalTeleported += () => fired++;

        server.SetPosition(PlayerRef.Slot(slot), new Vector3(18f, 0f, 9f));
        for (int i = 0; i < 8; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }

        Assert.True(server.TryGetPlayerState(slot, out PlayerMoveState after));
        Assert.Equal(18f, after.Position.X, 1);
        Assert.Equal(9f, after.Position.Z, 1);
        Assert.Equal(before.TeleportEpoch, after.TeleportEpoch);
        Assert.Equal(0, fired);
    }

    [Fact]
    public void ServerAdmin_forwards_it_to_the_head()
    {
        var head = new RecordingSetPositionHead();
        var admin = new ServerAdmin(head);
        admin.SetPosition(PlayerRef.Slot(3), new Vector3(1f, 2f, 3f));
        Assert.Equal(("set", 3, new Vector3(1f, 2f, 3f)), Assert.Single(head.Calls));
    }

    [Fact]
    public void An_implementer_that_never_heard_of_it_falls_back_to_Teleport()
    {
        // SetPosition is a default interface method, so a consumer head written before it keeps compiling. The
        // default cannot be a no-op (that would silently drop the placement) and it cannot be free (the epoch is what
        // the head does not know how to skip), so it forwards to Teleport: the position is right and the cut is
        // spurious, which is exactly the pre-#379 behaviour and never worse than it.
        var head = new RecordingHead();
        ((IAdminControllable)head).SetPosition(PlayerRef.Account("acct:1"), new Vector3(5f, 0f, 5f));
        Assert.Equal("teleport", Assert.Single(head.Calls).Kind);
    }

    private sealed class RecordingHead : IAdminControllable
    {
        public List<(string Kind, int Slot, Vector3 Position)> Calls { get; } = new();

        public IReadOnlyList<OnlinePlayer> ListOnline() => Array.Empty<OnlinePlayer>();
        public void Teleport(PlayerRef target, Vector3 position) => Calls.Add(("teleport", Slot(target), position));
        public void Kick(PlayerRef target, string reason) { }
        public void Broadcast(string text) { }

        private static int Slot(in PlayerRef target) => target.IsSlot ? target.SlotValue : -1;
    }

    private sealed class RecordingSetPositionHead : IAdminControllable
    {
        public List<(string Kind, int Slot, Vector3 Position)> Calls { get; } = new();

        public IReadOnlyList<OnlinePlayer> ListOnline() => Array.Empty<OnlinePlayer>();
        public void Teleport(PlayerRef target, Vector3 position) => Calls.Add(("teleport", Slot(target), position));
        public void SetPosition(PlayerRef target, Vector3 position) => Calls.Add(("set", Slot(target), position));
        public void Kick(PlayerRef target, string reason) { }
        public void Broadcast(string text) { }

        private static int Slot(in PlayerRef target) => target.IsSlot ? target.SlotValue : -1;
    }
}
