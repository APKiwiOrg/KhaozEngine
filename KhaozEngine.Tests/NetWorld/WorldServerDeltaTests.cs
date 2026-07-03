using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The WorldServer serving per-client AoI deltas: a delta-capable client is served
/// <see cref="MoveProtocol.ServerFrameKind.Delta"/> frames (and converges), a legacy client keeps getting full
/// snapshots (version skew, both directions), and idle in-AoI state costs near-zero bytes per tick.
/// </summary>
public class WorldServerDeltaTests
{
    private static readonly Func<float, float, float> Flat = (x, z) => 0f;
    private const float Dt = 1f / 30f;

    private static WorldServer NewServer(InMemoryHub hub, bool deltaReplication = true) =>
        new(hub.Server, new WorldServerConfig { TickSeconds = Dt, InterestRadius = 500f, MaxPlayers = 8, DeltaReplication = deltaReplication },
            Flat, MoveTuning.Default);

    private static void Pump(WorldServer server, int ticks, params RawDeltaClient[] clients)
    {
        for (int i = 0; i < ticks; i++)
        {
            server.Poll();
            server.Tick(Dt);
            foreach (RawDeltaClient c in clients) c.Poll();
        }
    }

    [Fact]
    public void Delta_capable_clients_are_served_deltas_and_see_each_other_move()
    {
        var hub = new InMemoryHub();
        WorldServer server = NewServer(hub);
        var a = new RawDeltaClient(hub.CreateClient(), server.Registry);
        var b = new RawDeltaClient(hub.CreateClient(), server.Registry);

        Pump(server, 8, a, b);
        Assert.True(a.Joined && b.Joined);
        Assert.True(a.DeltaFramesApplied > 0, "A should be served deltas once it advertised DeltaCapable");
        Assert.True(b.DeltaFramesApplied > 0);

        Assert.True(a.TryPos(b.LocalNetId, out Vector3 bBefore));
        var forward = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f);   // -Z
        for (int i = 0; i < 12; i++)
        {
            b.SendMove(forward);
            server.Poll();
            server.Tick(Dt);
            a.Poll();
            b.Poll();
        }
        Assert.True(a.TryPos(b.LocalNetId, out Vector3 bAfter));
        Assert.True(bAfter.Z < bBefore.Z - 0.1f, $"A should see B move via deltas: {bBefore.Z} -> {bAfter.Z}");
    }

    [Fact]
    public void Legacy_client_still_receives_full_snapshots_from_a_delta_server()
    {
        // Old client (never advertises DeltaCapable) against a new (delta) server: it must keep getting full
        // snapshots and never a Delta frame, and still see the world.
        var hub = new InMemoryHub();
        WorldServer server = NewServer(hub);
        var legacy = new RawDeltaClient(hub.CreateClient(), server.Registry, advertiseDelta: false);
        var mover = new RawDeltaClient(hub.CreateClient(), server.Registry);

        Pump(server, 8, legacy, mover);
        Assert.True(legacy.Joined);
        Assert.Equal(0, legacy.DeltaFramesApplied);          // never served a delta
        Assert.True(legacy.SnapshotFramesApplied > 0);

        Assert.True(legacy.TryPos(mover.LocalNetId, out Vector3 before));
        var forward = new MoveCommand(new Vector2(0f, 1f), false, 0f);
        for (int i = 0; i < 12; i++) { mover.SendMove(forward); server.Poll(); server.Tick(Dt); legacy.Poll(); mover.Poll(); }
        Assert.True(legacy.TryPos(mover.LocalNetId, out Vector3 after));
        Assert.True(after.Z < before.Z - 0.1f, "legacy client still sees movement over full snapshots");
    }

    [Fact]
    public void Delta_disabled_server_serves_full_snapshots_even_to_capable_clients()
    {
        var hub = new InMemoryHub();
        WorldServer server = NewServer(hub, deltaReplication: false);
        var a = new RawDeltaClient(hub.CreateClient(), server.Registry);   // advertises delta

        Pump(server, 8, a);
        Assert.True(a.Joined);
        Assert.Equal(0, a.DeltaFramesApplied);               // server forced full snapshots
        Assert.True(a.SnapshotFramesApplied > 0);
    }

    [Fact]
    public void Idle_in_aoi_state_costs_far_fewer_bytes_as_deltas_than_full_snapshots()
    {
        var hub = new InMemoryHub();
        WorldServer server = NewServer(hub);
        var delta = new RawDeltaClient(hub.CreateClient(), server.Registry);                    // deltas
        var legacy = new RawDeltaClient(hub.CreateClient(), server.Registry, advertiseDelta: false);  // full snapshots

        Pump(server, 8, delta, legacy);   // warm up: join, hello, first full/delta

        long deltaBytesBefore = delta.TotalDeltaBytes;
        long legacyBytesBefore = legacy.TotalSnapshotBytes;
        const int idle = 30;
        Pump(server, idle, delta, legacy);   // nobody sends input -> nothing moves

        long deltaBytes = delta.TotalDeltaBytes - deltaBytesBefore;
        long legacyBytes = legacy.TotalSnapshotBytes - legacyBytesBefore;
        // Two idle players in AoI: each full snapshot re-sends both entities' components every tick; each delta is
        // just the (empty) header. The delta stream is a small fraction of the full-snapshot stream.
        Assert.True(deltaBytes * 3 < legacyBytes, $"idle delta bytes {deltaBytes} vs full {legacyBytes}");
    }
}
