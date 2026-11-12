using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public sealed class ClientWorldTickTests : ClientWorldTickContract
{
    protected override ClientHead CreateClient(INetTransport transport, ReplicationRegistry registry, bool? advance)
    {
        WorldClientConfig config = advance.HasValue
            ? new WorldClientConfig { AdvanceWorldTick = advance.Value }
            : new WorldClientConfig();
        var client = new WorldClient(transport, (_, _) => 0f, MoveTuning.Default, config, registry: registry);
        return new(client.WorldForTest, () => client.Poll(), () => client.Joined, client);
    }

    protected override byte[] Frame(byte[] snapshot) => MoveProtocol.EncodeServerFrame(
        MoveProtocol.ServerFrameKind.Snapshot, MoveProtocol.EncodeSnapshotFrame(1, 0, snapshot));

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
