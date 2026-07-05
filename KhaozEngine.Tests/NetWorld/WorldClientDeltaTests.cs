using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// End-to-end delta replication through the real <see cref="WorldClient"/> / <see cref="WorldServer"/>, including
/// both version-skew directions (a delta client against a legacy server, a legacy client against a delta server) so
/// client and server can deploy independently with no disconnect.
/// </summary>
public class WorldClientDeltaTests
{
    private static readonly Func<float, float, float> Flat = (x, z) => 0f;
    private const float Dt = 1f / 30f;

    private static WorldServerConfig ServerConfig(bool delta) =>
        new() { TickSeconds = Dt, InterestRadius = 500f, MaxPlayers = 8, DeltaReplication = delta };

    private static WorldClientConfig ClientConfig(bool delta) =>
        new() { TickSeconds = Dt, RequestDeltaReplication = delta };

    private static Vector3 RemotePos(WorldClient observer, long remoteNetId)
    {
        foreach (EntityRenderState e in observer.Snapshot())
            if (!e.IsLocal && e.Id.Value == remoteNetId) return e.Position;
        throw new Xunit.Sdk.XunitException($"remote {remoteNetId} not visible");
    }

    private static void RunAndAssertRemoteMovesForward(bool serverDelta, bool clientDelta)
    {
        var hub = new InMemoryHub();
        var server = new WorldServer(hub.Server, ServerConfig(serverDelta), Flat, MoveTuning.Default);
        var a = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, ClientConfig(clientDelta));
        var b = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, ClientConfig(clientDelta));

        for (int i = 0; i < 8; i++) { server.Poll(); server.Tick(Dt); a.Poll(); b.Poll(); }
        Assert.True(a.Joined && b.Joined);
        Assert.True(a.LocalNetId > 0 && b.LocalNetId > 0);

        Vector3 aSeenByB_before = RemotePos(b, a.LocalNetId);
        var forward = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f);   // -Z
        for (int i = 0; i < 14; i++)
        {
            a.SendInput(forward);
            server.Poll();
            server.Tick(Dt);
            a.Poll();
            b.Poll();
        }
        Vector3 aSeenByB_after = RemotePos(b, a.LocalNetId);
        Assert.True(aSeenByB_after.Z < aSeenByB_before.Z - 0.1f,
            $"B should see A move -Z (serverDelta={serverDelta}, clientDelta={clientDelta}): {aSeenByB_before.Z} -> {aSeenByB_after.Z}");
    }

    [Fact]
    public void Delta_client_and_delta_server_round_trip()
    {
        RunAndAssertRemoteMovesForward(serverDelta: true, clientDelta: true);
    }

    [Fact]
    public void Delta_client_against_a_legacy_server_round_trips()
    {
        // New client (advertises delta) + old server (delta serving off): the server ignores the hello and serves
        // full snapshots; the client applies them and sends no acks. No disconnect, movement replicates.
        RunAndAssertRemoteMovesForward(serverDelta: false, clientDelta: true);
    }

    [Fact]
    public void Legacy_client_against_a_delta_server_round_trips()
    {
        // Old client (never advertises) + new server: the server never upgrades the slot and serves full snapshots.
        RunAndAssertRemoteMovesForward(serverDelta: true, clientDelta: false);
    }

    [Fact]
    public void Remote_interpolates_continuously_across_deltas()
    {
        var hub = new InMemoryHub();
        var server = new WorldServer(hub.Server, ServerConfig(delta: true), Flat, MoveTuning.Default);
        var a = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, ClientConfig(delta: true));
        var b = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, ClientConfig(delta: true));

        for (int i = 0; i < 8; i++) { server.Poll(); server.Tick(Dt); a.Poll(); b.Poll(); }
        Assert.True(a.Joined && b.Joined);

        // A walks steadily; B renders A with interpolation. The interpolated Z must advance monotonically across the
        // delta stream (no backward jump from a mis-applied delta), staying continuous.
        var forward = new MoveCommand(new Vector2(0f, 1f), false, 0f);
        float prevZ = RemotePos(b, a.LocalNetId).Z;
        int advances = 0;
        for (int i = 0; i < 20; i++)
        {
            a.SendInput(forward);
            server.Poll();
            server.Tick(Dt);
            a.Poll();
            b.Poll();
            b.AdvancePresentation(Dt);
            float z = RemotePos(b, a.LocalNetId).Z;
            Assert.True(z <= prevZ + 1e-3f, $"remote Z jumped backward (forward motion is -Z): {prevZ} -> {z}");
            if (z < prevZ - 1e-4f) advances++;
            prevZ = z;
        }
        Assert.True(advances > 5, "the interpolated remote should keep advancing across deltas");
    }
}
