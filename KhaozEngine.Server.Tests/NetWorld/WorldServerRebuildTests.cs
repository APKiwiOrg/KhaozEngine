using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>Format 2 offer, acceptance and selection on <see cref="WorldServer"/>.</summary>
public sealed class WorldServerRebuildTests : RebuildHostCases
{
    protected override RebuildHostKind Kind => RebuildHostKind.World;
}

/// <summary>
/// Per-host format 2 negotiation cases: acceptance of the exact offered epoch, the end of legacy serving, the
/// negotiation deadline, slot cleanup, the serve observation seam and construction-time validation. Each host's
/// test class runs every case against its own server. No case serves format 2 state, which needs the keyframe
/// barrier.
/// </summary>
public abstract class RebuildHostCases
{
    protected abstract RebuildHostKind Kind { get; }

    private (LimitTransport Transport, RebuildHost Host, RawRebuildClient Client, ulong Epoch) Offered()
    {
        var transport = LimitTransport.Known();
        RebuildHost host = RebuildHost.Create(Kind, transport, allowUnreliable: true);
        var client = new RawRebuildClient(transport.Hub.CreateClient(), requestRebuild: true);
        host.Pump(3, client);   // connect, join, then the capability is answered
        ReplicationModeOffer offer = Assert.Single(client.Offers);
        Assert.Equal(ReplicationDeliveryMode.AcknowledgedUnreliable, offer.Mode);
        return (transport, host, client, offer.Epoch);
    }

    [Fact]
    public void AcceptanceStopsLegacyAndAwaitsKeyframe()
    {
        (LimitTransport transport, RebuildHost host, RawRebuildClient client, ulong epoch) = Offered();
        int sendsBefore = transport.Sends.Count;

        client.Send(RebuildProtocol.EncodeAcceptance(epoch));
        host.Pump(20, client);

        Assert.True(host.TryGetRebuildStream(client.Slot, out RebuildServerStream stream));
        Assert.True(stream.Accepted);
        Assert.True(host.TryGetReplicationSelection(client.Slot, out ReplicationSelection selection));
        Assert.Equal(new ReplicationSelection(ReplicationDeliveryMode.AcknowledgedUnreliable,
            ReplicationSelectionReason.Selected, epoch), selection);
        Assert.Equal(sendsBefore, transport.Sends.Count);   // no legacy frame, no v2 delta, no chunk, no new offer
        Assert.Equal(0, client.LegacyFramesAfterFirstOffer);
        Assert.Equal(0, client.V2StateFrames);
        Assert.Equal(0, host.CountSuspicious(client.Slot, SuspiciousReason.MalformedPacket));
    }

    [Fact]
    public void AcceptanceForAnotherEpochIsIgnored()
    {
        (_, RebuildHost host, RawRebuildClient client, ulong epoch) = Offered();

        client.Send(RebuildProtocol.EncodeAcceptance(epoch + 1));
        host.Pump(2, client);

        Assert.True(host.TryGetRebuildStream(client.Slot, out RebuildServerStream stream));
        Assert.False(stream.Accepted);
        Assert.Equal(0, host.CountSuspicious(client.Slot, SuspiciousReason.MalformedPacket));

        client.Send(RebuildProtocol.EncodeAcceptance(epoch));
        host.Pump(1, client);
        Assert.True(stream.Accepted);
    }

    [Fact]
    public void RepeatedCapabilityAllocatesOneEpochAndSendsOneOffer()
    {
        (_, RebuildHost host, RawRebuildClient client, ulong epoch) = Offered();

        for (int i = 0; i < 3; i++)
            client.Send(MoveProtocol.EncodeClientControl(MoveProtocol.ClientControlKind.RebuildDeltaCapable));
        host.Pump(4, client);

        Assert.Single(client.Offers);
        Assert.Equal(epoch, host.EpochHighWater);
        Assert.Equal(1UL, epoch);
    }

    [Fact]
    public void UnacceptedOfferEndsWithRecoveryFailedAtTheDeadline()
    {
        (_, RebuildHost host, RawRebuildClient client, _) = Offered();
        int slot = client.Slot;
        // The offer went out one replication tick ago. 88 more ticks leave the negotiation open for 89.
        host.Pump(88, client);
        Assert.Equal(1, host.PlayerCount);
        Assert.Null(client.RejectReason);

        host.Pump(1, client);

        Assert.Equal(0, host.PlayerCount);
        Assert.False(host.TryGetReplicationSelection(slot, out _));
        Assert.False(host.TryGetRebuildStream(slot, out _));
        Assert.Equal(ReplicationFailure.RecoveryFailedToken, client.RejectReason);
        Assert.Equal(0, client.LegacyFramesAfterFirstOffer);
    }

    [Fact]
    public void AcceptanceDoesNotRestartTheNegotiationDeadline()
    {
        (_, RebuildHost host, RawRebuildClient client, ulong epoch) = Offered();
        int slot = client.Slot;
        host.Pump(3, client);   // the offer is now 4 replication ticks old
        client.Send(RebuildProtocol.EncodeAcceptance(epoch));
        host.Pump(85, client);   // offer + 89, acceptance + 85

        Assert.True(host.TryGetRebuildStream(slot, out RebuildServerStream stream));
        Assert.True(stream.Accepted);
        Assert.Equal(1, host.PlayerCount);
        Assert.Null(client.RejectReason);

        host.Pump(1, client);   // offer + 90

        Assert.Equal(0, host.PlayerCount);
        Assert.False(host.TryGetRebuildStream(slot, out _));
        Assert.Equal(ReplicationFailure.RecoveryFailedToken, client.RejectReason);
    }

    [Fact]
    public void ReusedSlotStartsUnnegotiated()
    {
        (LimitTransport transport, RebuildHost host, RawRebuildClient first, _) = Offered();
        int slot = first.Slot;
        first.Disconnect();
        host.Pump(2, first);

        var next = new RawRebuildClient(transport.Hub.CreateClient(), requestRebuild: false);
        host.Pump(6, next);

        Assert.Equal(slot, next.Slot);
        Assert.True(host.TryGetReplicationSelection(slot, out ReplicationSelection selection));
        Assert.Equal(default, selection);
        Assert.False(host.TryGetRebuildStream(slot, out _));
        Assert.True(next.LegacyFrames >= 5, "the new session is served legacy state");
    }

    [Fact]
    public void ServerWithoutDeltaReplicationAnswersModeZeroForServerPolicy()
    {
        var transport = LimitTransport.Known();
        RebuildHost host = RebuildHost.Create(Kind, transport, deltaReplication: false);
        var client = new RawRebuildClient(transport.Hub.CreateClient(), requestRebuild: true);

        host.Pump(10, client);

        Assert.Equal(RebuildProtocol.FallbackOffer(ReplicationSelectionReason.DisabledServerPolicy),
            Assert.Single(client.Offers));
        Assert.True(client.LegacyFramesAfterFirstOffer >= 5, "snapshots continue after a mode 0 offer");
        Assert.All(client.Frames, f => Assert.NotEqual(MoveProtocol.ServerFrameKind.Delta, f.Kind));
    }

    [Fact]
    public void ServeIsObservedOncePerSlotAfterTheVisibilityFilter()
    {
        var transport = LimitTransport.Known();
        long hidden = -1;
        RebuildHost host = RebuildHost.Create(Kind, transport, allowUnreliable: true,
            visible: (_, netId) => netId != hidden);
        hidden = host.SpawnEntity(RebuildHost.Spawn.X + 3f, RebuildHost.Spawn.Z + 3f);
        long shown = host.SpawnEntity(RebuildHost.Spawn.X - 3f, RebuildHost.Spawn.Z - 3f);
        var legacy = new RawRebuildClient(transport.Hub.CreateClient(), requestRebuild: false);
        var negotiated = new RawRebuildClient(transport.Hub.CreateClient(), requestRebuild: true);
        host.Pump(3, legacy, negotiated);
        Assert.Single(negotiated.Offers);

        var observed = new List<(int Slot, World World, long[] Interest, long Owner)>();
        host.ServeObserved = (slot, world, interest, owner) => observed.Add((slot, world, interest.ToArray(), owner));
        host.Pump(4, legacy, negotiated);

        foreach (RawRebuildClient client in new[] { legacy, negotiated })
        {
            var mine = observed.Where(o => o.Slot == client.Slot).ToList();
            Assert.Equal(4, mine.Count);
            Assert.All(mine, o =>
            {
                Assert.NotNull(o.World);
                Assert.Equal(client.LocalNetId, o.Owner);
                Assert.Contains(o.Owner, o.Interest);
                Assert.Contains(shown, o.Interest);
                Assert.DoesNotContain(hidden, o.Interest);
            });
        }
        Assert.Equal(0, negotiated.LegacyFramesAfterFirstOffer);
    }

    [Fact]
    public void OptInWithoutDeltaReplicationIsRefusedAtConstruction()
    {
        ArgumentException refused = Assert.Throws<ArgumentException>(() =>
            RebuildHost.Create(Kind, LimitTransport.Known(), allowUnreliable: true, deltaReplication: false));
        Assert.Equal("optIn", refused.ParamName);
    }

    [Fact]
    public void RetainedBudgetBelowFourKeyframesIsRefusedAtConstruction()
    {
        var stream = new ReplicationStreamOptions
        {
            Limits = new DeltaRebuildOptions { MaxKeyframeBytes = 64 * 1024, MaxRetainedPayloadBytes = 3 * 64 * 1024 },
        };
        Assert.ThrowsAny<ArgumentException>(() =>
            RebuildHost.Create(Kind, LimitTransport.Known(), allowUnreliable: true, stream: stream));
    }

    [Fact]
    public void ReliableOnlyHostNeverChecksStreamBudgets()
    {
        var invalid = new ReplicationStreamOptions
        {
            MaxChunksPerTick = 0,
            MaxTransportPayloadBytes = 1,
            Limits = new DeltaRebuildOptions { MaxRetainedProjections = 1 },
        };
        var transport = LimitTransport.Known();
        RebuildHost host = RebuildHost.Create(Kind, transport, stream: invalid);
        var client = new RawRebuildClient(transport.Hub.CreateClient(), requestRebuild: true);

        host.Pump(6, client);

        Assert.Equal(RebuildProtocol.FallbackOffer(ReplicationSelectionReason.DisabledServerPolicy),
            Assert.Single(client.Offers));
        Assert.True(client.LegacyFramesAfterFirstOffer >= 3);
    }
}
