using System;
using System.IO;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The last-resort backstop: a snapshot the client cannot decode (an unregistered component type id from a newer
/// server protocol) must become a clean <see cref="DisconnectReason.IncompatibleVersion"/> disconnect, never an
/// unhandled exception escaping <see cref="WorldClient.Poll"/> into the consumer's frame loop.
/// </summary>
public class WorldClientDecodeFailureTests
{
    private static readonly Func<float, float, float> Flat = (x, z) => 0f;

    // A snapshot with one entity carrying a single component of an unregistered type id (256 - the shared registry
    // only knows 1/2/3), wrapped as a server->client snapshot frame.
    private static byte[] BadSnapshotFrame()
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(1);              // entity count
            bw.Write(1);              // netId
            bw.Write((ushort)256);    // unregistered type id -> decode fails here
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
        Assert.Contains("unregistered type id 256", client.DisconnectReasonDetail);
    }
}
