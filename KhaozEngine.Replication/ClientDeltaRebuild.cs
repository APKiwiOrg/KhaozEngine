using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KhaozEngine.Ecs;

namespace KhaozEngine.Replication;

/// <summary>
/// The format 2 receiver for one connection. It reconstructs each newer packet privately from its exact retained
/// baseline, or from empty state for a keyframe, validates every count, id, length, terminator and byte, retains the
/// result, and only then publishes it into the live world through <see cref="ClientReplicationView"/>. The live world,
/// which presentation may have interpolated or changed, is never a baseline. Single-threaded.
/// </summary>
/// <remarks>
/// <para>Classification order: a valid 18-byte fixed header, then the epoch, then the sequence against
/// <see cref="LatestAcceptedId"/>, then the named baseline, and only then the body codecs. A duplicate, stale,
/// retired-epoch or unknown-epoch packet is therefore never decoded, and a missing baseline changes nothing.</para>
/// <para>Retention pins the latest publication, which is also the ack target, and the newest server baseline named by
/// an accepted packet. Other projections are pruned oldest first by insertion. Each retained projection owns one
/// compact backing no larger than its complete keyframe, so a new projection beside the pins always fits the budget
/// <see cref="DeltaRebuildOptions.Validate"/> requires.</para>
/// <para>Publication shifts the presentation buffers once and records no interpolation sample. The caller stamps one
/// sample after <see cref="DeltaRebuildResult.Accepted"/>, on its existing ingest path.</para>
/// </remarks>
public sealed class ClientDeltaRebuild
{
    private readonly ClientReplicationView view;
    private readonly ProjectionRetention retention;
    private readonly ProjectionStaging staging;
    private readonly RebuildDeltaReader reader;
    private readonly HashSet<ReplicationPacketId> prospectivePins = new();

    private ulong? epoch;          // the authorized epoch, pending until its first accepted packet
    private bool established;      // a packet of the authorized epoch was accepted
    private ulong retiredFloor;    // epochs at or below this were replaced or reset and are never authorized again
    private ReplicationPacketId? latest;
    private ReplicationPacketId? confirmedBaseline;

    /// <summary>Creates a receiver publishing into <paramref name="view"/>.</summary>
    /// <param name="registry">The view's registry. Staging decodes with it.</param>
    /// <param name="view">The live presentation view this receiver publishes into.</param>
    /// <param name="options">The stream limits. Validated here.</param>
    /// <exception cref="ArgumentOutOfRangeException">The options are invalid. <c>ParamName</c> names the property.</exception>
    /// <exception cref="ArgumentException"><paramref name="registry"/> is not the view's registry.</exception>
    public ClientDeltaRebuild(ReplicationRegistry registry, ClientReplicationView view, DeltaRebuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!ReferenceEquals(registry, view.Registry))
            throw new ArgumentException("The receiver must decode with the view's registry.", nameof(registry));
        this.view = view;
        retention = new ProjectionRetention(options);
        staging = new ProjectionStaging(registry);
        reader = new RebuildDeltaReader(options);
    }

    /// <summary>The id of the newest accepted and published projection in the authorized epoch, or null before the
    /// first accept, after <see cref="Reset"/> and after a replacement <see cref="ExpectEpoch"/>.</summary>
    public ReplicationPacketId? LatestAcceptedId => latest;

    /// <summary>The retained id routine acks advertise. The latest publication is pinned, so this equals
    /// <see cref="LatestAcceptedId"/>, and is null exactly when it is.</summary>
    public ReplicationPacketId? AckTarget => latest;

    /// <summary>Why the last <see cref="TryApply"/> returned <see cref="DeltaRebuildResult.Invalid"/>:
    /// <see cref="DeltaRebuildFailure.MalformedPacket"/> for malformed bytes or an unknown built-in, which is terminal
    /// decode incompatibility. <see cref="DeltaRebuildFailure.CapacityExceeded"/> for a projection that cannot be held
    /// within the limits, which is a terminal capacity failure. <see cref="DeltaRebuildFailure.SequenceAmbiguous"/> for
    /// a sequence or baseline exactly half the range away, whose order is undefined: the packet is not ingested, and
    /// the caller ignores it like a stale packet, with no disconnect and no repair. Any result other than Invalid, and
    /// <see cref="ExpectEpoch"/> or <see cref="Reset"/>, sets <see cref="DeltaRebuildFailure.None"/>.</summary>
    public DeltaRebuildFailure LastFailure { get; private set; }

    /// <summary>
    /// Authorizes <paramref name="epoch"/>, learned from a reliable mode offer or a reliable keyframe chunk header.
    /// A greater epoch retires every older one: its datagrams become stale, retained projections and pins are dropped,
    /// and the last published live state stays until a keyframe of the new epoch validates and publishes. Repeating
    /// the pending epoch before its first accepted packet changes nothing. Datagrams can never call this.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The epoch is zero, below the authorized one, equal to an
    /// established one, or was retired by an earlier replacement or <see cref="Reset"/>.</exception>
    public void ExpectEpoch(ulong epoch)
    {
        ReplicationSequence.RequireEpoch(epoch, nameof(epoch));
        if (epoch <= retiredFloor)
            throw new ArgumentOutOfRangeException(nameof(epoch), epoch, $"Epoch {epoch} was retired on this receiver.");
        if (this.epoch is ulong current)
        {
            if (epoch == current && !established) return;
            if (epoch == current)
                throw new ArgumentOutOfRangeException(nameof(epoch), epoch,
                    $"Epoch {epoch} is already established. A replacement must be greater.");
            if (epoch < current)
                throw new ArgumentOutOfRangeException(nameof(epoch), epoch,
                    $"Epoch {epoch} is below authorized epoch {current}. A replacement must be greater.");
            retiredFloor = current;
        }
        this.epoch = epoch;
        established = false;
        LastFailure = DeltaRebuildFailure.None;
        ClearStream();
    }

    /// <summary>
    /// Offers one format 2 body. On <see cref="DeltaRebuildResult.Accepted"/> the reconstructed projection was
    /// retained and published into <paramref name="world"/>, and <paramref name="acceptedId"/> names it. On
    /// <see cref="DeltaRebuildResult.MissingBaseline"/>, <paramref name="missingBaseline"/> names the exact id that is
    /// not retained. Every result other than Accepted leaves the live world, presentation buffers, pins and accepted
    /// id unchanged. Malformed input never throws.
    /// <para>Invalid has three meanings, told apart by <see cref="LastFailure"/>. MalformedPacket and CapacityExceeded
    /// are terminal for the stream. SequenceAmbiguous, a sequence or baseline exactly half the range away, is ignored
    /// like <see cref="DeltaRebuildResult.DuplicateOrStale"/>: nothing was decoded or changed, and the caller neither
    /// ingests nor disconnects.</para>
    /// </summary>
    /// <param name="world">The live client world publication writes.</param>
    /// <param name="packet">The body only, starting at the format byte, without any transport envelope.</param>
    /// <param name="acceptedId">The accepted id, or default.</param>
    /// <param name="missingBaseline">The missing baseline id, or null.</param>
    /// <param name="error">A developer facing reason for <see cref="DeltaRebuildResult.Invalid"/>, otherwise null.</param>
    public DeltaRebuildResult TryApply(World world, ReadOnlyMemory<byte> packet,
        out ReplicationPacketId acceptedId, out ReplicationPacketId? missingBaseline, out string? error)
    {
        ArgumentNullException.ThrowIfNull(world);
        acceptedId = default;
        missingBaseline = null;
        LastFailure = DeltaRebuildFailure.None;

        if (!RebuildDeltaReader.TryReadHeader(packet.Span, out RebuildDeltaReader.Header header, out error))
            return Fail(DeltaRebuildFailure.MalformedPacket);
        if (epoch != header.Id.Epoch) return DeltaRebuildResult.DuplicateOrStale;
        uint sequence = header.Id.Sequence;
        if (latest is ReplicationPacketId newest)
        {
            if (ReplicationSequence.IsAmbiguous(sequence, newest.Sequence))
                return Fail(DeltaRebuildFailure.SequenceAmbiguous, out error,
                    $"Sequence {sequence} is half the range from accepted {newest.Sequence}.");
            if (!ReplicationSequence.IsNewer(sequence, newest.Sequence)) return DeltaRebuildResult.DuplicateOrStale;
        }

        ReplicationProjection basis = ReplicationProjection.Empty;
        if (header.Baseline is ReplicationPacketId baseline)
        {
            if (ReplicationSequence.IsAmbiguous(sequence, baseline.Sequence))
                return Fail(DeltaRebuildFailure.SequenceAmbiguous, out error,
                    $"Baseline {baseline.Sequence} is half the range from sequence {sequence}.");
            if (!ReplicationSequence.IsNewer(sequence, baseline.Sequence))
                return Fail(DeltaRebuildFailure.MalformedPacket, out error,
                    $"Baseline {baseline.Sequence} is not older than sequence {sequence}.");
            if (!retention.TryGet(baseline, out basis))
            {
                missingBaseline = baseline;
                return DeltaRebuildResult.MissingBaseline;
            }
        }

        ReplicationProjection projection;
        staging.Reset();
        try
        {
            (byte[] source, int offset, int length) = Segment(packet);
            projection = reader.Read(source, offset, length, basis, header.IsKeyframe, staging);
            staging.FinishFor(projection);   // last, after every received and carried-over known frame decoded
        }
        catch (DeltaRebuildException ex)
        {
            staging.Reset();
            return Fail(ex.Failure, out error, ex.Message);
        }
        catch (Exception ex)
        {
            // Defensive: the reader bounds-checks every field and staging wraps codec faults, so this is not expected.
            staging.Reset();
            return Fail(DeltaRebuildFailure.MalformedPacket, out error, $"Format 2 body did not decode: {ex.Message}");
        }

        // Publication installs every known frame from staging. Verify that now, before retention changes, so a
        // publication fault can never leave retention holding an id LatestAcceptedId does not name.
        if (!staging.HoldsEveryKnownFrame(projection, out string? missing))
        {
            staging.Reset();
            return Fail(DeltaRebuildFailure.MalformedPacket, out error, $"Projection {header.Id} is incomplete: {missing}");
        }

        // Retain before publication and before anything can acknowledge the id.
        ReplicationPacketId? pinnedBaseline = NewerOf(confirmedBaseline, header.Baseline);
        prospectivePins.Clear();
        prospectivePins.Add(header.Id);
        if (pinnedBaseline is ReplicationPacketId pin) prospectivePins.Add(pin);
        // Defensive and unreachable under D2.9: each retained projection owns one backing smaller than its complete
        // keyframe, so the new projection and at most two distinct pins need at most three of the four keyframes the
        // budget must hold, and three of the four projections the count must allow.
        if (!retention.TryRetain(header.Id, projection, prospectivePins, out DeltaRebuildFailure failure))
        {
            staging.Reset();
            return Fail(DeltaRebuildFailure.CapacityExceeded, out error,
                $"Projection {header.Id} cannot be retained within the limits ({failure}).");
        }

        view.PublishProjection(world, projection, staging);
        staging.Reset();
        PublicationCountForTest++;
        latest = header.Id;
        confirmedBaseline = pinnedBaseline;
        established = true;
        acceptedId = header.Id;
        return DeltaRebuildResult.Accepted;
    }

    /// <summary>
    /// Clears the stream: authorized epoch, retained projections, pins, accepted id and failure. The live world and
    /// presentation are untouched. The epoch that was authorized is retired, so a later <see cref="ExpectEpoch"/> must
    /// name a greater one and no datagram of an earlier stream can revive state.
    /// </summary>
    public void Reset()
    {
        if (epoch is ulong current && current > retiredFloor) retiredFloor = current;
        epoch = null;
        established = false;
        LastFailure = DeltaRebuildFailure.None;
        ClearStream();
    }

    internal bool TryGetRetainedForTest(ReplicationPacketId id, out ReplicationProjection projection) =>
        retention.TryGet(id, out projection);

    internal int RetainedCountForTest => retention.Count;

    internal int RetainedBytesForTest => retention.PayloadBytes;

    internal int PublicationCountForTest { get; private set; }

    internal IReadOnlySet<ReplicationPacketId> PinnedIdsForTest => retention.Pins;

    private void ClearStream()
    {
        retention.Clear();
        staging.Reset();
        latest = null;
        confirmedBaseline = null;
    }

    private DeltaRebuildResult Fail(DeltaRebuildFailure failure)
    {
        LastFailure = failure;
        return DeltaRebuildResult.Invalid;
    }

    private DeltaRebuildResult Fail(DeltaRebuildFailure failure, out string? error, string message)
    {
        error = message;
        return Fail(failure);
    }

    // Accepted packets are monotonic and so are the server baselines they name, but keep the newer either way.
    private static ReplicationPacketId? NewerOf(ReplicationPacketId? pinned, ReplicationPacketId? named)
    {
        if (named is not ReplicationPacketId n) return pinned;
        if (pinned is not ReplicationPacketId p) return n;
        return ReplicationSequence.IsNewer(n.Sequence, p.Sequence) ? n : p;
    }

    private static (byte[] Source, int Offset, int Length) Segment(ReadOnlyMemory<byte> packet) =>
        MemoryMarshal.TryGetArray(packet, out ArraySegment<byte> segment) && segment.Array is not null
            ? (segment.Array, segment.Offset, segment.Count)
            : (packet.ToArray(), 0, packet.Length);
}
