using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public sealed class ClientWorldTickTests : ClientWorldTickContract
{
    protected override ClientHead CreateClient(INetTransport transport, ReplicationRegistry registry)
    {
        var client = new WorldClient(transport, (_, _) => 0f, MoveTuning.Default, registry: registry);
        return new(client.WorldForTest, () => client.Poll(), () => client.Joined, client);
    }

    protected override byte[] Frame(byte[] snapshot) => MoveProtocol.EncodeServerFrame(
        MoveProtocol.ServerFrameKind.Snapshot, MoveProtocol.EncodeSnapshotFrame(1, 0, snapshot));

    [Fact]
    public void Format2_poll_owns_live_and_staging_boundaries()
    {
        // #1771: a private format 2 staging world must not retain generations behind a cleared live world.
        ClientRebuildRig rig = ClientRebuildRig.Create(RebuildHostKind.World);
        using WorldClient client = rig.Client;
        rig.Steps(12);
        Assert.Equal(ReplicationDeliveryMode.AcknowledgedUnreliable, client.ReplicationSelection.Mode);
        Assert.NotNull(client.DeltaRebuildForTest);
        var world = client.WorldForTest;
        var local = client.ViewForTest.Entities[client.LocalNetId];
        world.Emit(-1);
        ulong before = world.Tick;

        rig.Step();

        Assert.Equal(before + 1, world.Tick);
        Assert.Contains(local, world.Changed<ReplicatedPosition>());
        Assert.Empty(world.Events<int>());
        Assert.Equal((0, 0, 0, 0, 0), client.DeltaRebuildForTest!.StagingTrackingForTests.Counts);
        world.Emit(7);
        Assert.Equal(new[] { 7 }, world.Events<int>());
        client.Poll();
        Assert.Equal(before + 2, world.Tick);
        Assert.Equal((0, 0, 0, 0, 0), world.ChangeTrackingCountsForTests);
        Assert.Empty(world.Events<int>());
    }

    [Fact]
    public void Reconnect_wait_polls_clear_the_old_world_then_the_attempt_advances_the_replacement()
    {
        // #1771: clearing before StartAttempt would advance the abandoned world instead of the fresh one.
        var hub = new RestartableHub();
        using var client = new WorldClient(hub.Connect, (_, _) => 0f, MoveTuning.Default, new WorldClientConfig
        {
            DisconnectTimeoutSeconds = 1f,
            Reconnect = new ReconnectBackoff { InitialSeconds = 1f, MaxSeconds = 1f },
        });
        var oldWorld = client.WorldForTest;
        client.Poll(1f); // The unanswered initial attempt reaches its deadline.
        Assert.Equal(WorldConnectionState.Reconnecting, client.ConnectionState);
        oldWorld.Emit(7);
        ulong beforeWait = oldWorld.Tick;
        client.Poll(0.5f);
        Assert.Same(oldWorld, client.WorldForTest);
        Assert.Equal(beforeWait + 1, oldWorld.Tick);
        Assert.Empty(oldWorld.Events<int>());

        client.Poll(0.5f); // Starts a fresh attempt before the receive boundary.
        Assert.NotSame(oldWorld, client.WorldForTest);
        Assert.Equal(1ul, client.WorldForTest.Tick);
        Assert.Equal(beforeWait + 1, oldWorld.Tick);
        var server = new NetServer(hub.ServerTransport, 1, new AllowAllAuthenticator());
        server.Poll();
        client.Poll();
        Assert.True(client.Joined);
        Assert.Equal(2ul, client.WorldForTest.Tick);
    }
}
