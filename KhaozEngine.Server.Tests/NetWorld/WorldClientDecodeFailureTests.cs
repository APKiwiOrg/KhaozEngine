using System;
using System.IO;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The last-resort backstop: a snapshot the client cannot decode because it carries an unregistered <b>built-in</b>
/// (below-floor) component type id (a genuinely newer/incompatible core protocol) must become a clean
/// <see cref="DisconnectReason.IncompatibleVersion"/> disconnect, never an unhandled exception escaping
/// <see cref="WorldClient.Poll"/> into the consumer's frame loop. (An unregistered <em>extension</em> id, at/above
/// <see cref="KhaozEngine.Replication.ReplicationRegistry.FirstExtensionTypeId"/>, is instead SKIPPED, covered by
/// <see cref="EntityReplicationSeamTests"/>.)
/// </summary>
public class WorldClientDecodeFailureTests
{
    private static readonly Func<float, float, float> Flat = (x, z) => 0f;

    // A snapshot with one entity carrying a single component of an unregistered built-in type id (5 - reserved
    // below the extension floor; the shared registry only knows 1/2/3/4), wrapped as a server->client snapshot frame.
    // Below the floor it is unframed, so an unknown id there is a hard "client out of date" mismatch, not a skip.
    private static byte[] BadSnapshotFrame()
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(1);              // entity count
            bw.Write(1L);             // netId (64-bit)
            bw.Write((ushort)5);      // unregistered built-in (below-floor) type id -> decode fails here
        }
        byte[] snapshot = ms.ToArray();
        return MoveProtocol.EncodeServerFrame(MoveProtocol.ServerFrameKind.Snapshot,
            MoveProtocol.EncodeSnapshotFrame(localNetId: 1, ackSeq: 0, snapshot));
    }

    [Fact]
    public void Undecodable_snapshot_disconnects_cleanly_instead_of_throwing()
    {
        var (serverTransport, clientTransport) = LoopbackTransport.CreatePair();
        var server = new NetServer(serverTransport, maxPlayers: 4, new AllowAllAuthenticator());
        var client = new WorldClient(clientTransport, Flat, MoveTuning.Default, new WorldClientConfig());

        string? decodeError = null;
        client.SnapshotDecodeFailed += e => decodeError = e;

        // Handshake the client in, capturing the joined slot.
        int slot = -1;
        for (int i = 0; i < 20 && slot < 0; i++)
        {
            server.Poll();
            while (server.TryDequeueEvent(out ServerSessionEvent ev))
                if (ev.Kind == ServerSessionEventKind.Joined) slot = ev.Slot;
            client.Poll();
        }
        Assert.True(slot >= 0);
        Assert.Equal(WorldConnectionState.Connected, client.ConnectionState);

        // Server pushes a snapshot the client's registry cannot decode.
        server.SendTo(slot, BadSnapshotFrame(), NetChannelReliability.ReliableOrdered);

        // Pumping the client must NOT throw (the whole point); it disconnects cleanly instead.
        Exception? thrown = Record.Exception(() =>
        {
            for (int i = 0; i < 5; i++) { server.Poll(); client.Poll(); }
        });

        Assert.Null(thrown);
        Assert.Equal(WorldConnectionState.Disconnected, client.ConnectionState);
        Assert.Equal(DisconnectReason.IncompatibleVersion, client.DisconnectReason);
        Assert.NotNull(decodeError);
        Assert.Contains("unregistered type id 5", client.DisconnectReasonDetail);
    }
}
