using System.Collections.Generic;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Tests.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

/// <summary>
/// <see cref="NetServer.HoldSlotOnDisconnect"/> keeps a transport-disconnected slot allocated for its subject, so the
/// host can leave a body on it and the same account's reconnect lands back on that slot rather than a fresh one.
/// </summary>
public class NetServerSlotHoldTests
{
    private sealed class Peer
    {
        public required NetClient Client { get; init; }
        public required INetTransport Transport { get; init; }
        public List<string> Rejects { get; } = new();
        public int JoinedSlot { get; private set; } = -1;

        public void Poll()
        {
            Client.Poll();
            while (Client.TryDequeueEvent(out ClientSessionEvent ev))
            {
                if (ev.Kind == ClientSessionEventKind.Joined) JoinedSlot = ev.Slot;
                else if (ev.Kind == ClientSessionEventKind.Rejected) Rejects.Add(ev.RejectReason);
            }
        }
    }

    private sealed class Rig
    {
        public InMemoryTransportHub Hub { get; } = new();
        public NetServer Server { get; }
        public List<Peer> Peers { get; } = new();
        public List<ServerSessionEvent> Events { get; } = new();
        public List<int> HoldAsks { get; } = new();

        public Rig(int maxPlayers = 2, DuplicateSessionPolicy policy = DuplicateSessionPolicy.KickOlder) =>
            Server = new NetServer(Hub.Server, maxPlayers, WireGenerationAuthenticator.Install(new AllowAllAuthenticator()),
                duplicateSessions: policy);

        /// <summary>Installs a hold delegate that records every slot it is asked about and answers
        /// <paramref name="hold"/>.</summary>
        public void Hold(bool hold) => Server.HoldSlotOnDisconnect = slot => { HoldAsks.Add(slot); return hold; };

        /// <summary>Connects a client. A null subject is a tokenless guest.</summary>
        public Peer Connect(string? subject)
        {
            INetTransport t = Hub.CreateClient();
            var peer = new Peer
            {
                Client = new NetClient(t, subject is null ? TestHandshake.Wire() : TestHandshake.Wire(subject)),
                Transport = t,
            };
            Peers.Add(peer);
            return peer;
        }

        public void Drop(Peer peer) => Hub.DisconnectClient(peer.Transport);

        public void Pump(int frames = 6)
        {
            for (int i = 0; i < frames; i++)
            {
                foreach (Peer p in Peers) p.Poll();
                Server.Poll();
                while (Server.TryDequeueEvent(out ServerSessionEvent ev))
                    if (ev.Kind != ServerSessionEventKind.Data) Events.Add(ev);
            }
        }

        /// <summary>Connects and pumps until the session layer has answered.</summary>
        public Peer Join(string? subject)
        {
            Peer peer = Connect(subject);
            Pump();
            return peer;
        }

        public void Clear() => Events.Clear();
    }

    private static void AssertJoined(ServerSessionEvent ev, int slot, string subject)
    {
        Assert.Equal(ServerSessionEventKind.Joined, ev.Kind);
        Assert.Equal(slot, ev.Slot);
        Assert.Equal(subject, ev.Subject);
    }

    private static void AssertLeft(ServerSessionEvent ev, int slot)
    {
        Assert.Equal(ServerSessionEventKind.Left, ev.Kind);
        Assert.Equal(slot, ev.Slot);
    }

    [Fact]
    public void WithoutAHoldTheSlotIsFreedAsToday()
    {
        var rig = new Rig();
        Peer a = rig.Join("a");
        Assert.Equal(0, a.JoinedSlot);

        rig.Drop(a);
        rig.Pump();
        Peer b = rig.Join("b");

        Assert.Equal(0, b.JoinedSlot);
        Assert.Collection(rig.Events,
            ev => AssertJoined(ev, 0, "a"),
            ev => AssertLeft(ev, 0),
            ev => AssertJoined(ev, 0, "b"));
    }

    [Fact]
    public void AHeldSlotIsNotReallocated()
    {
        var rig = new Rig();
        rig.Hold(true);
        Peer a = rig.Join("a");

        rig.Drop(a);
        rig.Pump();
        Peer b = rig.Join("b");

        Assert.Equal(1, b.JoinedSlot);
        Assert.Equal(new[] { 0 }, rig.HoldAsks);
        Assert.Collection(rig.Events,
            ev => AssertJoined(ev, 0, "a"),
            ev => AssertLeft(ev, 0),
            ev => AssertJoined(ev, 1, "b"));
    }

    [Theory]
    [InlineData(DuplicateSessionPolicy.KickOlder)]
    [InlineData(DuplicateSessionPolicy.RefuseNewer)]
    public void TheHoldingSubjectReclaimsItsSlot(DuplicateSessionPolicy policy)
    {
        var rig = new Rig(policy: policy);
        rig.Hold(true);
        Peer a = rig.Join("a");
        rig.Drop(a);
        rig.Pump();
        rig.Clear();

        Peer again = rig.Join("a");

        Assert.Equal(0, again.JoinedSlot);
        Assert.Empty(again.Rejects);
        ServerSessionEvent joined = Assert.Single(rig.Events);
        AssertJoined(joined, 0, "a");
    }

    [Fact]
    public void AFullServerStillSeatsTheReturningSubject()
    {
        var rig = new Rig(maxPlayers: 1);
        rig.Hold(true);
        Peer a = rig.Join("a");
        rig.Drop(a);
        rig.Pump();

        Peer b = rig.Join("b");
        Assert.Equal(-1, b.JoinedSlot);
        Assert.Equal(new[] { "server full" }, b.Rejects);

        Peer again = rig.Join("a");
        Assert.Equal(0, again.JoinedSlot);
        Assert.Empty(again.Rejects);
    }

    [Fact]
    public void ReleaseFreesTheSlotAndForgetsTheSubject()
    {
        var rig = new Rig();
        rig.Hold(true);
        Peer a = rig.Join("a");
        rig.Drop(a);
        rig.Pump();

        rig.Server.ReleaseHeldSlot(0);
        Peer b = rig.Join("b");
        Assert.Equal(0, b.JoinedSlot);

        rig.Clear();
        Peer again = rig.Join("a");
        Assert.Equal(1, again.JoinedSlot);
        ServerSessionEvent joined = Assert.Single(rig.Events);
        AssertJoined(joined, 1, "a");

        // Slot 1 is live, not held, so releasing it changes nothing: it keeps its connection and its allocator bit.
        rig.Server.ReleaseHeldSlot(1);
        Assert.True(rig.Server.TrySendTo(1, new byte[] { 1 }, NetChannelReliability.ReliableOrdered));
        Peer c = rig.Join("c");
        Assert.Equal(new[] { "server full" }, c.Rejects);
    }

    [Fact]
    public void ATokenlessHoldIsNeverReclaimed()
    {
        var rig = new Rig();
        rig.Hold(true);
        Peer guest = rig.Join(null);
        Assert.Equal(0, guest.JoinedSlot);
        rig.Drop(guest);
        rig.Pump();

        Peer other = rig.Join(null);

        Assert.Equal(1, other.JoinedSlot);
    }

    [Fact]
    public void OnlyTransportDisconnectsAskTheHold()
    {
        var rig = new Rig();
        rig.Hold(true);
        Peer a = rig.Join("a");
        Peer b = rig.Join("b");
        Assert.Equal(0, a.JoinedSlot);
        Assert.Equal(1, b.JoinedSlot);

        // A KickOlder duplicate ends a's live session itself, and the transport's later Disconnected for that
        // connection finds no slot.
        Peer a2 = rig.Join("a");
        Assert.Equal(new[] { SessionRejectReason.SignedInElsewhere }, a.Rejects);
        Assert.Equal(0, a2.JoinedSlot);

        // A refused Hello holds no slot, and neither does a pending connection that drops before its Hello.
        Peer c = rig.Join("c");
        Assert.Equal(new[] { "server full" }, c.Rejects);
        Peer pending = rig.Connect("d");
        rig.Drop(pending);
        rig.Pump();

        Assert.Empty(rig.HoldAsks);
    }
}
