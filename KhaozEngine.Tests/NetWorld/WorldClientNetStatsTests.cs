using System;
using KhaozEngine.Diagnostics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

// The diagnostics seam: WorldClient.NetStats surfaces connection health (snapshot rate, correction magnitude,
// plus transport RTT/loss/bytes when the transport provides them) for a telemetry overlay. Exercised over the
// in-memory loopback, which reports NetTransportStats.Unavailable (so RTT/loss/bytes stay 0 here; the LiteNetLib
// path is verified by the build against the real NetPeer/NetStatistics API).
public sealed class WorldClientNetStatsTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    [Fact]
    public void Disconnected_before_join_reports_not_connected_and_zero()
    {
        (_, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig());

        ClientNetStats s = client.NetStats;
        Assert.False(s.Connected);
        Assert.Equal(0f, s.SnapshotsPerSec);
        Assert.Equal(0f, s.BytesInPerSec);
        Assert.Equal(0f, s.BytesOutPerSec);
        Assert.Equal(0f, s.LastCorrectionMeters);
        Assert.Equal(0f, s.AvgCorrectionMeters);
    }

    [Fact]
    public void Connected_client_reports_a_positive_snapshot_rate()
    {
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = config.TickSeconds });
        float dt = config.TickSeconds;

        // Drive > 1s of frames (with AdvancePresentation, which rolls the rate window) while snapshots flow.
        for (int i = 0; i < 45; i++)
        {
            client.SendInput(MoveCommand.Idle);
            server.Poll();
            server.Tick(dt);
            client.Poll();
            client.AdvancePresentation(dt);
        }

        ClientNetStats s = client.NetStats;
        Assert.True(client.Joined);
        Assert.True(s.Connected);
        Assert.True(s.SnapshotsPerSec > 0f, $"expected a positive snapshot rate, got {s.SnapshotsPerSec}");
        Assert.True(s.SnapshotsPerSec < 200f, $"snapshot rate should be a sane value, got {s.SnapshotsPerSec}");
        Assert.False(float.IsNaN(s.LastCorrectionMeters));
        Assert.True(s.LastCorrectionMeters >= 0f);
        Assert.True(s.AvgCorrectionMeters >= 0f);
        // Loopback exposes no transport-level stats.
        Assert.Equal(0f, s.RttMs);
        Assert.Equal(0f, s.PacketLoss);
    }

    [Fact]
    public void Loopback_transport_default_stats_are_unavailable()
    {
        (LoopbackTransport a, _) = LoopbackTransport.CreatePair();
        NetTransportStats s = ((INetTransport)a).Stats; // exercises the default interface method
        Assert.False(s.Connected);
        Assert.Equal(0f, s.RttMs);
        Assert.Equal(0L, s.BytesReceivedTotal);
    }
}
