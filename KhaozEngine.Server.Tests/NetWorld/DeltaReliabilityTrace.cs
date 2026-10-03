using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>One client presentation event: everything the acceptance cases read, recorded after the frame's
/// <c>AdvancePresentation</c>. <c>SnapshotArrived</c> is the presentation trace's ingest mark for the frame, and
/// <c>PresentationChangedByPoll</c> is true when the poll changed any current, previous or interpolation sample buffer
/// of the client's view.</summary>
internal readonly record struct DeltaFrameRow(
    int Client,
    int Frame,
    int Subtick,
    int ServerTick,
    int Phase,
    double ClockBefore,
    double ClockAfter,
    bool RequestedUnreliable,
    ReplicationSelection Selection,
    WorldConnectionState State,
    ReplicationPacketId? AcceptedId,
    uint? LastBaseline,
    int MovementAck,
    int PendingBeforePoll,
    int PendingAfterPoll,
    int IngestsThisPoll,
    int IngestCount,
    int AcceptedCount,
    Vector3 AuthoritativePosition,
    PlayerMoveState? Predicted,
    Vector3? Rendered,
    float Heading,
    bool Grounded,
    bool Swimming,
    uint LocalTeleportEpoch,
    float ReconcileError,
    int ClientRetainedCount,
    int ClientRetainedBytes,
    int ServerRetainedCount,
    int ServerRetainedBytes,
    int MaxServerPayload,
    int MaxClientPayload,
    int DeliveredThisPoll,
    bool SnapshotArrived,
    bool PresentationChangedByPoll);

/// <summary>One remote entity as a client rendered it in one presentation event.</summary>
internal readonly record struct DeltaRemoteRow(int Client, int Frame, long NetId, Vector3 Position, float Heading,
    bool Grounded, bool Swimming, bool Held);

/// <summary>One state a client ingested: an accepted format 2 id or a legacy frame, with the server tick it was built
/// at and the presentation clock it was stamped with.</summary>
internal readonly record struct DeltaIngestRow(int Client, int Frame, int Subtick, double Stamp, int SourceTick,
    ReplicationPacketId? Id, bool Keyframe);

/// <summary>One command a client submitted.</summary>
internal readonly record struct DeltaSubmitRow(int Client, int Frame, int Subtick, int ServerTick, int Seq,
    MoveCommand Command);

/// <summary>The rig's complete record of one finite run.</summary>
internal sealed class DeltaReliabilityTrace
{
    public List<DeltaFrameRow> Frames { get; } = new();
    public List<DeltaRemoteRow> Remotes { get; } = new();
    public List<DeltaIngestRow> Ingests { get; } = new();
    public List<DeltaSubmitRow> Submits { get; } = new();

    public IEnumerable<DeltaFrameRow> FramesOf(int client) => Frames.Where(r => r.Client == client);

    public IEnumerable<DeltaIngestRow> IngestsOf(int client) => Ingests.Where(r => r.Client == client);

    public IEnumerable<DeltaSubmitRow> SubmitsOf(int client) => Submits.Where(r => r.Client == client);

    public IEnumerable<DeltaRemoteRow> RemotesOf(int client, long netId) =>
        Remotes.Where(r => r.Client == client && r.NetId == netId);

    /// <summary>Every scheduled presentation event produced a row, in frame order, with nothing extra.</summary>
    public void AssertComplete(int client, int expectedFrames)
    {
        List<DeltaFrameRow> rows = FramesOf(client).ToList();
        Assert.Equal(expectedFrames, rows.Count);
        for (int i = 0; i < rows.Count; i++) Assert.Equal(i, rows[i].Frame);
    }

    /// <summary>Accepted ids never go backwards: a later epoch, or the same epoch and a sequence newer by the
    /// half-range rule.</summary>
    public void AssertMonotonicAccepted(int client)
    {
        ReplicationPacketId? previous = null;
        foreach (DeltaIngestRow row in IngestsOf(client))
        {
            if (row.Id is not ReplicationPacketId id) continue;
            if (previous is ReplicationPacketId p)
                Assert.True(IsNewer(id, p), $"client {client} accepted {id} after {p} at frame {row.Frame}");
            previous = id;
        }
    }

    /// <summary>Every row stays within the configured retention and packet maxima.</summary>
    public void AssertMaxima(int client, DeltaRebuildOptions limits, int maxServerPayload)
    {
        foreach (DeltaFrameRow row in FramesOf(client))
        {
            Assert.True(row.ClientRetainedCount <= limits.MaxRetainedProjections,
                $"frame {row.Frame}: client retains {row.ClientRetainedCount}");
            Assert.True(row.ClientRetainedBytes <= limits.MaxRetainedPayloadBytes,
                $"frame {row.Frame}: client retains {row.ClientRetainedBytes} bytes");
            Assert.True(row.ServerRetainedCount <= limits.MaxRetainedProjections,
                $"frame {row.Frame}: server retains {row.ServerRetainedCount}");
            Assert.True(row.ServerRetainedBytes <= limits.MaxRetainedPayloadBytes,
                $"frame {row.Frame}: server retains {row.ServerRetainedBytes} bytes");
            Assert.True(row.MaxServerPayload <= maxServerPayload,
                $"frame {row.Frame}: a format 2 send of {row.MaxServerPayload} bytes over {maxServerPayload}");
        }
    }

    /// <summary>True when <paramref name="a"/> is newer than <paramref name="b"/>: a greater epoch, or the same epoch
    /// with an unsigned sequence distance in 1 through 0x7fffffff.</summary>
    public static bool IsNewer(ReplicationPacketId a, ReplicationPacketId b)
    {
        if (a.Epoch != b.Epoch) return a.Epoch > b.Epoch;
        uint distance = unchecked(a.Sequence - b.Sequence);
        return distance is >= 1 and <= 0x7fff_ffffu;
    }

    /// <summary>The smallest angular difference between two headings.</summary>
    public static float HeadingDelta(float a, float b)
    {
        float d = MathF.IEEERemainder(a - b, MathF.Tau);
        return MathF.Abs(d);
    }
}
