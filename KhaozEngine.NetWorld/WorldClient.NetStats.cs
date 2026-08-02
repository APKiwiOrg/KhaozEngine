using KhaozEngine.Diagnostics;
using KhaozEngine.Netcode;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The client's DIAGNOSTICS-ONLY connection-health window: RTT and loss read straight off the transport, byte and
/// snapshot rates accumulated over a rolling ~1s window, and the prediction-reconciliation correction magnitude (last
/// value plus a rolling average over a fixed ring).
/// <para>
/// Nothing here is on the simulation path. It is carved out of <c>WorldClient.cs</c> for exactly that reason: it is
/// the one concern in that file that no other part of the client reads, so it costs nothing to isolate and it keeps
/// the session/prediction file to the session and the prediction. The whole of it is these fields plus
/// <see cref="NetStats"/> and two recorders, and the rest of the client touches it through those two calls alone.
/// </para>
/// </summary>
public sealed partial class WorldClient
{
    // Rates are computed over a rolling ~1s window driven by AdvancePresentation(dt) (the canonical per-frame call).
    // Snapshot count and correction magnitude are captured at ingest in Poll/OnSnapshot.
    private const float StatsWindowSeconds = 1f;
    private float statsElapsed;                        // seconds accumulated in the current window
    private int snapshotsSinceWindow;                  // AoI snapshots applied since the window opened
    private bool statsBaselineSet;                     // whether the byte-counter baseline has been captured
    private long bytesInBaseline;
    private long bytesOutBaseline;
    private float snapshotsPerSec;                     // last completed window's rates (reported by NetStats)
    private float bytesInPerSec;
    private float bytesOutPerSec;
    private float lastCorrection;                      // magnitude of the most recent reconciliation correction (m)
    private readonly float[] correctionRing = new float[64];
    private float correctionSum;
    private int correctionCount;
    private int correctionHead;

    /// <summary>
    /// A read-only snapshot of this client's connection health for a diagnostics/telemetry overlay: RTT, packet
    /// loss, and byte rates (from the transport - 0 over loopback), the AoI snapshot ingest rate, and the
    /// prediction-reconciliation correction magnitude (last + rolling average). <see cref="ClientNetStats.Connected"/>
    /// tracks <see cref="Joined"/>. Rates refresh once per ~1s window as <see cref="AdvancePresentation"/> is pumped, and
    /// reading this never mutates state.
    /// </summary>
    public ClientNetStats NetStats
    {
        get
        {
            NetTransportStats t = net.TransportStats;
            return new ClientNetStats
            {
                Connected = Joined,
                RttMs = t.RttMs,
                PacketLoss = t.PacketLoss,
                BytesInPerSec = bytesInPerSec,
                BytesOutPerSec = bytesOutPerSec,
                SnapshotsPerSec = snapshotsPerSec,
                LastCorrectionMeters = lastCorrection,
                AvgCorrectionMeters = correctionCount > 0 ? correctionSum / correctionCount : 0f,
            };
        }
    }

    /// <summary>Roll the byte/snapshot-rate window forward by <paramref name="dt"/>, recomputing the rates each ~1s.</summary>
    private void UpdateNetStatsWindow(float dt)
    {
        if (dt > 0f) statsElapsed += dt;

        NetTransportStats t = net.TransportStats;
        if (!statsBaselineSet)
        {
            bytesInBaseline = t.BytesReceivedTotal;
            bytesOutBaseline = t.BytesSentTotal;
            statsBaselineSet = true;
        }

        if (statsElapsed >= StatsWindowSeconds)
        {
            snapshotsPerSec = snapshotsSinceWindow / statsElapsed;
            bytesInPerSec = (t.BytesReceivedTotal - bytesInBaseline) / statsElapsed;
            bytesOutPerSec = (t.BytesSentTotal - bytesOutBaseline) / statsElapsed;
            statsElapsed = 0f;
            snapshotsSinceWindow = 0;
            bytesInBaseline = t.BytesReceivedTotal;
            bytesOutBaseline = t.BytesSentTotal;
        }
    }

    /// <summary>Record one AoI snapshot/delta ingest into the current rate window.</summary>
    private void RecordSnapshotIngest() => snapshotsSinceWindow++;

    /// <summary>Record one reconciliation correction magnitude into the last-value + rolling-average buffer.</summary>
    private void RecordCorrection(float meters)
    {
        if (float.IsNaN(meters) || float.IsInfinity(meters) || meters < 0f) return;
        lastCorrection = meters;
        if (correctionCount < correctionRing.Length)
        {
            correctionRing[correctionHead] = meters;
            correctionSum += meters;
            correctionCount++;
        }
        else
        {
            correctionSum += meters - correctionRing[correctionHead];
            correctionRing[correctionHead] = meters;
        }
        correctionHead = (correctionHead + 1) % correctionRing.Length;
    }
}
