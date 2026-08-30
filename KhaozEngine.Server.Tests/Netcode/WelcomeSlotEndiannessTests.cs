using System;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

/// <summary>
/// The Welcome body is a 4-byte LITTLE-ENDIAN slot, the format <see cref="SessionOpcode.Welcome"/> documents, on both
/// sides and on any host (#133). Both sides used to go through <c>BitConverter</c>, which encodes in the running
/// process's native order: it agrees with the documented format on every machine the fleet runs on, and the two sides
/// always agreed with each other because both made the same call, so nothing here could ever have gone wrong in the
/// field. What it broke was anyone decoding the wire from the doc comment rather than from this code.
/// <para>These tests pin the BYTES rather than a round trip, because a round trip is byte-order-blind: it passes
/// against big-endian code as long as both ends are big-endian. A slot of 256 is the smallest value whose encoding
/// differs between the two orders, so it is what both directions are pinned on.</para>
/// </summary>
public class WelcomeSlotEndiannessTests
{
    // 256 little-endian. Big-endian would be { 0x00, 0x00, 0x01, 0x00 }.
    private static readonly byte[] SlotTwoFiftySixLittleEndian = { 0x00, 0x01, 0x00, 0x00 };

    [Fact]
    public void Server_writes_the_slot_little_endian()
    {
        var transport = new RecordingTransport();
        var server = new NetServer(transport, maxPlayers: 300, new AllowAllAuthenticator());

        // 257 distinct connections, each presenting a tokenless Hello, so the last one is admitted into slot 256.
        // Tokenless is what keeps them 257 separate sessions: an empty subject is never a duplicate of anything.
        byte[] hello = SessionFrame.Write(SessionOpcode.Hello, ReadOnlySpan<byte>.Empty);
        for (int i = 1; i <= 257; i++)
            transport.Deliver(NetEvent.FromData(new NetConnectionId(i), hello, NetChannelReliability.ReliableOrdered));
        server.Poll();

        Assert.Equal(257, transport.Sent.Count);
        byte[] lastWelcome = transport.Sent[^1].Payload;
        Assert.Equal(SessionOpcode.Welcome, SessionFrame.ReadOpcode(lastWelcome));
        Assert.Equal(SlotTwoFiftySixLittleEndian, SessionFrame.ReadBody(lastWelcome));
    }

    [Fact]
    public void Client_reads_the_slot_little_endian()
    {
        var transport = new RecordingTransport();
        var connection = new NetConnectionId(1);
        transport.Deliver(NetEvent.Connected(connection));
        transport.Deliver(NetEvent.FromData(connection,
            SessionFrame.Write(SessionOpcode.Welcome, SlotTwoFiftySixLittleEndian),
            NetChannelReliability.ReliableOrdered));

        var client = new NetClient(transport);
        client.Poll();

        Assert.Equal(256, client.Slot);
        Assert.True(client.TryDequeueEvent(out ClientSessionEvent joined));
        Assert.Equal(ClientSessionEventKind.Joined, joined.Kind);
        Assert.Equal(256, joined.Slot);
    }

    [Fact]
    public void A_short_Welcome_body_leaves_the_client_without_a_slot()
    {
        // The length guard on the read side: a truncated body must not be decoded out of whatever is behind it.
        var transport = new RecordingTransport();
        var connection = new NetConnectionId(1);
        transport.Deliver(NetEvent.Connected(connection));
        transport.Deliver(NetEvent.FromData(connection,
            SessionFrame.Write(SessionOpcode.Welcome, new byte[] { 0x00, 0x01 }),
            NetChannelReliability.ReliableOrdered));

        var client = new NetClient(transport);
        client.Poll();

        Assert.Equal(-1, client.Slot);
    }
}
