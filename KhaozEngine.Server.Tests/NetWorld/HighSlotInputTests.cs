using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Regression: <see cref="WorldServer"/> and <see cref="ShardedWorldServer"/> built their
/// <see cref="RemoteCommandQueue{TCommand}"/> without a <c>maxSlots</c>, so it used the queue's default distinct-slot
/// cap of 64. A game configured with <c>MaxPlayers &gt; 64</c> then had every move command for the 65th+ concurrent
/// slot silently dropped by <see cref="RemoteCommandQueue{TCommand}.Store"/> (the distinct-slot bound), freezing that
/// avatar with no error. Both servers must size the queue to <c>MaxPlayers</c> so input for the highest slot flows.
/// </summary>
public class HighSlotInputTests
{
    private static float Flat(float x, float z) => 0f;

    // W at yaw 0 -> -Z: forward motion is a strict decrease in Z, easy to assert per slot.
    private static readonly MoveCommand Forward = new(new Vector2(0f, 1f), run: false, cameraYaw: 0f);

    private const int Players = 70;   // above the RemoteCommandQueue default distinct-slot cap of 64

    [Fact]
    public void WorldServer_processes_input_for_slots_above_the_64_queue_default()
    {
        var hub = new InMemoryHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = Players };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);

        var clients = new List<NetClient>();
        for (int i = 0; i < Players; i++) clients.Add(new NetClient(hub.CreateClient(), TestHandshake.Wire()));

        // Join everyone.
        // Pump until every client has completed the join handshake (received its Welcome + slot). Waiting on
        // server-side PlayerCount alone races: the server counts a player as joined a round before that client polls
        // its Welcome frame, so the last few clients would still read Slot == -1.
        for (int i = 0; i < 600 && !clients.TrueForAll(c => c.Slot >= 0); i++)
        {
            foreach (NetClient c in clients) c.Poll();
            server.Poll();
            server.Tick(config.TickSeconds);
        }
        Assert.Equal(Players, server.PlayerCount);

        // Record each slot's spawn Z.
        var spawnZ = new Dictionary<int, float>();
        foreach (NetClient c in clients)
        {
            Assert.True(c.Slot >= 0, "every client should have been assigned a slot");
            Assert.True(server.TryGetPlayerState(c.Slot, out PlayerMoveState s));
            spawnZ[c.Slot] = s.Position.Z;
        }

        // Everyone walks forward for several ticks (per-client monotonic seq).
        for (int tick = 0; tick < 20; tick++)
        {
            foreach (NetClient c in clients)
                c.Send(MoveProtocol.EncodeMove(tick, Forward), NetChannelReliability.ReliableOrdered);
            server.Poll();
            server.Tick(config.TickSeconds);
            foreach (NetClient c in clients) c.Poll();
        }

        // Every player - including the ones on slots >= 64 - must have moved. Pre-fix, the highest distinct slot's
        // commands were dropped by the queue's 64-slot cap and that avatar stayed frozen at spawn.
        foreach (NetClient c in clients)
        {
            Assert.True(server.TryGetPlayerState(c.Slot, out PlayerMoveState s));
            Assert.True(s.Position.Z < spawnZ[c.Slot] - 0.1f,
                $"slot {c.Slot} never moved: spawn z {spawnZ[c.Slot]} -> {s.Position.Z} (input dropped by the maxSlots cap)");
        }
    }

    [Fact]
    public void ShardedWorldServer_processes_input_for_slots_above_the_64_queue_default()
    {
        var hub = new InMemoryHub();
        var config = new ShardedWorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = Players };
        var server = new ShardedWorldServer(hub.Server, config, Flat, MoveTuning.Default);

        var clients = new List<NetClient>();
        for (int i = 0; i < Players; i++) clients.Add(new NetClient(hub.CreateClient(), TestHandshake.Wire()));

        // Pump until every client has completed the join handshake (received its Welcome + slot). Waiting on
        // server-side PlayerCount alone races: the server counts a player as joined a round before that client polls
        // its Welcome frame, so the last few clients would still read Slot == -1.
        for (int i = 0; i < 600 && !clients.TrueForAll(c => c.Slot >= 0); i++)
        {
            foreach (NetClient c in clients) c.Poll();
            server.Poll();
            server.Tick(config.TickSeconds);
        }
        Assert.Equal(Players, server.PlayerCount);

        var spawnZ = new Dictionary<int, float>();
        foreach (NetClient c in clients)
        {
            Assert.True(c.Slot >= 0, "every client should have been assigned a slot");
            Assert.True(server.TryGetPlayerState(c.Slot, out PlayerMoveState s));
            spawnZ[c.Slot] = s.Position.Z;
        }

        for (int tick = 0; tick < 20; tick++)
        {
            foreach (NetClient c in clients)
                c.Send(MoveProtocol.EncodeMove(tick, Forward), NetChannelReliability.ReliableOrdered);
            server.Poll();
            server.Tick(config.TickSeconds);
            foreach (NetClient c in clients) c.Poll();
        }

        foreach (NetClient c in clients)
        {
            Assert.True(server.TryGetPlayerState(c.Slot, out PlayerMoveState s));
            Assert.True(s.Position.Z < spawnZ[c.Slot] - 0.1f,
                $"slot {c.Slot} never moved: spawn z {spawnZ[c.Slot]} -> {s.Position.Z} (input dropped by the maxSlots cap)");
        }
    }
}
