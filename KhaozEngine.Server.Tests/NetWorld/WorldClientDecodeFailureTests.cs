using System;
using System.IO;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The last-resort backstop: a legacy snapshot or delta the client cannot decode because it carries an unregistered
/// <b>built-in</b> (below-floor) component type id (a genuinely newer/incompatible core protocol) must become a clean
/// <see cref="DisconnectReason.IncompatibleVersion"/> disconnect, never an unhandled exception escaping
/// <see cref="WorldClient.Poll"/> into the consumer's frame loop. (An unregistered <em>extension</em> id, at/above
/// <see cref="KhaozEngine.Replication.ReplicationRegistry.FirstExtensionTypeId"/>, is instead SKIPPED, covered by
/// <see cref="EntityReplicationSeamTests"/>.)
/// </summary>
public class WorldClientDecodeFailureTests
{
    private static readonly Func<float, float, float> Flat = (x, z) => 0f;

    // The lowest reserved built-in id the shared registry does NOT claim, so this fixture keeps meaning "an id from a
    // newer core protocol" as the engine grows into the 1..15 band (it was 5 until PickupState took that, and 6 until
    // MovementOwnerState did). Assert-
    // pinned below rather than hand-tracked, so the next built-in fails loudly here instead of silently testing a
    // registered id.
    private const ushort UnregisteredBuiltInTypeId = 7;

    // A legacy state frame whose one entity carries a single component of an unregistered built-in type id (reserved
    // below the extension floor). Below the floor it is unframed, so an unknown id there is a hard "client out of
    // date" mismatch, not a skip. The delta body is a baseline -1 full delta, so the failure is the type id and never
    // a baseline gap.
    private static byte[] BadStateFrame(MoveProtocol.ServerFrameKind kind)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms))
        {
            if (kind == MoveProtocol.ServerFrameKind.Delta)
            {
                bw.Write(-1);                         // baseline seq: a full delta
                bw.Write(1);                          // snapshot seq
                bw.Write(0);                          // removed entity count
            }
            bw.Write(1);                              // entity count (changed count in a delta)
            bw.Write(1L);                             // netId (64-bit)
            if (kind == MoveProtocol.ServerFrameKind.Delta)
            {
                bw.Write((byte)1);                    // isNew
                bw.Write(0);                          // removed component count
            }
            bw.Write(UnregisteredBuiltInTypeId);      // unregistered built-in (below-floor) id -> decode fails here
        }
        return MoveProtocol.EncodeServerFrame(kind, MoveProtocol.EncodeSnapshotFrame(localNetId: 1, ackSeq: 0, ms.ToArray()));
    }

    [Fact]
    public void The_fixture_id_is_still_an_unclaimed_built_in()
    {
        // Guards the fixture itself: the id must stay below the extension floor (so it is unframed and hard-fails)
        // AND unregistered by the shared registry (so the failure is the one under test). Registering it would make
        // the test above decode happily and assert nothing.
        Assert.True(UnregisteredBuiltInTypeId < ReplicationRegistry.FirstExtensionTypeId);
        Assert.NotEqual(MoveProtocol.PositionTypeId, UnregisteredBuiltInTypeId);
        Assert.NotEqual(MoveProtocol.MovementTypeId, UnregisteredBuiltInTypeId);
        Assert.NotEqual(MoveProtocol.IdentityTypeId, UnregisteredBuiltInTypeId);
        Assert.NotEqual(MoveProtocol.DynamicBodyTypeId, UnregisteredBuiltInTypeId);
        Assert.NotEqual(MoveProtocol.PickupTypeId, UnregisteredBuiltInTypeId);
        Assert.NotEqual(MoveProtocol.MovementOwnerTypeId, UnregisteredBuiltInTypeId);
        Assert.False(MoveProtocol.CreateRegistry().IsRegistered(UnregisteredBuiltInTypeId));
    }

    // #1262: both legacy state paths end the connection locally, so the server sees the leave at once rather than
    // holding the session until its own timeout. The format 2 path has the same proof in WorldClientRebuildRecoveryTests.
    [Theory]
    [InlineData(MoveProtocol.ServerFrameKind.Snapshot)]
    [InlineData(MoveProtocol.ServerFrameKind.Delta)]
    public void Undecodable_legacy_state_disconnects_cleanly_instead_of_throwing(MoveProtocol.ServerFrameKind kind)
    {
        var (serverTransport, clientTransport) = LoopbackTransport.CreatePair();
        var server = new NetServer(serverTransport, maxPlayers: 4, new AllowAllAuthenticator());
        var client = new WorldClient(clientTransport, Flat, MoveTuning.Default, new WorldClientConfig());

        string? decodeError = null;
        WorldConnectionState? stateAtDecodeFailure = null;
        client.SnapshotDecodeFailed += e =>
        {
            decodeError = e;
            stateAtDecodeFailure = client.ConnectionState;
        };

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

        // Server pushes a state frame the client's registry cannot decode, then a valid empty snapshot naming another
        // local id. Both land in the client's first drain.
        server.SendTo(slot, BadStateFrame(kind), NetChannelReliability.ReliableOrdered);
        server.SendTo(slot, MoveProtocol.EncodeServerFrame(MoveProtocol.ServerFrameKind.Snapshot,
            MoveProtocol.EncodeSnapshotFrame(localNetId: 2, ackSeq: 0, BitConverter.GetBytes(0))),
            NetChannelReliability.ReliableOrdered);

        // Pumping the client must NOT throw (the whole point); it disconnects cleanly instead.
        bool serverSawLeave = false;
        Exception? thrown = Record.Exception(() =>
        {
            for (int i = 0; i < 5; i++)
            {
                server.Poll();
                while (server.TryDequeueEvent(out ServerSessionEvent ev))
                    serverSawLeave |= ev.Kind == ServerSessionEventKind.Left && ev.Slot == slot;
                client.Poll();
            }
        });

        Assert.Null(thrown);
        Assert.Equal(WorldConnectionState.Disconnected, client.ConnectionState);
        Assert.Equal(DisconnectReason.IncompatibleVersion, client.DisconnectReason);
        Assert.NotNull(decodeError);
        // #1790: the event still fires before the terminal state change, which consumers read on Disconnected.
        Assert.Equal(WorldConnectionState.Connected, stateAtDecodeFailure);
        Assert.Contains($"unregistered type id {UnregisteredBuiltInTypeId}", client.DisconnectReasonDetail);
        Assert.True(serverSawLeave, "the server sees the session end without waiting for its own timeout");
        // #1262: a state frame queued behind the failure is ignored once the end is terminal, so nothing is ingested.
        Assert.Equal(-1, client.LocalNetId);
    }
}
