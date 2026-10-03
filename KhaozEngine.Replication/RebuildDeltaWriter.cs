using System;
using System.Collections.Generic;
using System.IO;
using CapturedState = System.Collections.Generic.Dictionary<long, KhaozEngine.Replication.CapturedComponents>;

namespace KhaozEngine.Replication;

/// <summary>
/// The format 2 half of both delta writers: per-slot acknowledged rebuild state over a viewer source the owning writer
/// has already captured, interest-filtered and owner-scoped. Independent of the legacy signed counter, so it shares the
/// writer's capture without sharing its sequence. Single-threaded, like the writers.
/// </summary>
/// <remarks>
/// Build order is fixed so a failure leaves the slot unchanged: sequence order, compact viewer projection (entity,
/// frame and byte limits), complete keyframe size plus <see cref="DeltaRebuildOptions.EnvelopeBytes"/>, body encoding,
/// then one atomic retention that supersedes the older unsent candidate. Only after that does the slot record the new
/// candidate.
/// </remarks>
internal sealed class RebuildDeltaWriter
{
    private readonly Dictionary<int, RebuildDeltaSlot> slots = new();
    private readonly MemoryStream bodyStream = new();
    private readonly BinaryWriter bodyWriter;
    private readonly List<long> scratch = new();

    public RebuildDeltaWriter(ReplicationRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        bodyWriter = new BinaryWriter(bodyStream);
    }

    /// <exception cref="ArgumentOutOfRangeException">Zero, non-increasing epoch, or invalid options.</exception>
    public void Start(int slot, ulong epoch, DeltaRebuildOptions options)
    {
        ReplicationSequence.RequireEpoch(epoch, nameof(epoch));
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (slots.TryGetValue(slot, out RebuildDeltaSlot? retired) && epoch <= retired.Epoch)
            throw new ArgumentOutOfRangeException(nameof(epoch), epoch,
                $"Slot {slot} already holds format 2 epoch {retired.Epoch}. A replacement epoch must be greater.");
        slots[slot] = new RebuildDeltaSlot(epoch, options);
    }

    /// <summary>Checks the slot preconditions before the owning writer captures or projects anything.</summary>
    /// <exception cref="InvalidOperationException">No started stream, or a different owner than the epoch's.</exception>
    public void RequireBuildable(int slot, long? ownerNetId)
    {
        RebuildDeltaSlot state = Require(slot);
        if (!state.OwnerMatches(ownerNetId))
            throw new InvalidOperationException(
                $"Slot {slot} epoch {state.Epoch} serves owner {Describe(state.OwnerNetId)}, not " +
                $"{Describe(ownerNetId)}. An owner change needs a new epoch.");
    }

    public ReplicationDeltaPacket Build(int slot, CapturedState viewerSource, long? ownerNetId, bool keyframe)
    {
        RequireBuildable(slot, ownerNetId);
        RebuildDeltaSlot state = slots[slot];
        var id = new ReplicationPacketId(state.Epoch, state.NextSequence);
        RequireOrdered(state, id.Sequence);

        ReplicationProjection projection = ReplicationProjection.Compact(viewerSource, state.Options);
        long keyframeBytes = RebuildDeltaEncoding.KeyframeBytes(projection) + state.Options.EnvelopeBytes;
        if (keyframeBytes > state.Options.MaxKeyframeBytes)
            throw new DeltaRebuildException(DeltaRebuildFailure.CapacityExceeded,
                $"Complete keyframe would be {keyframeBytes} bytes, limit {state.Options.MaxKeyframeBytes}.");

        (ReplicationPacketId, ReplicationProjection)? baseline = null;
        if (!keyframe && state.Acknowledged is ReplicationPacketId acked
            && state.Retention.TryGet(acked, out ReplicationProjection basis))
            baseline = (acked, basis);

        bodyStream.SetLength(0);
        RebuildDeltaEncoding.Write(bodyWriter, id, baseline, projection, scratch);
        bodyWriter.Flush();
        var packet = new ReplicationDeltaPacket(id, baseline?.Item1, baseline is null,
            new ReadOnlySpan<byte>(bodyStream.GetBuffer(), 0, (int)bodyStream.Length));

        // Defensive: the writer cannot reach RetentionPressure. The candidate sits beside at most two pins (acknowledged
        // baseline, pending keyframe), each projection is smaller than its complete keyframe, and Validate requires
        // room for four keyframes. The typed throw stays in case a future pin breaks that bound.
        if (!state.Retention.TryRetain(id, projection, state.PinsWith(id), state.Candidate,
                out DeltaRebuildFailure failure))
            throw new DeltaRebuildException(failure,
                failure == DeltaRebuildFailure.RetentionPressure
                    ? $"Slot {slot} pins leave no room for format 2 projection {id}. Keyframe repair releases them."
                    : $"Format 2 projection {id} can never fit the retention limits.");
        state.Built(id, baseline is null, ownerNetId, projection.BackingBytes);
        return packet;
    }

    /// <exception cref="InvalidOperationException">No started stream, or not the exact unsent candidate.</exception>
    public void RecordSent(int slot, ReplicationPacketId id) => Require(slot).Sent(id);

    public void Acknowledge(int slot, ReplicationPacketId id)
    {
        if (slots.TryGetValue(slot, out RebuildDeltaSlot? state)) state.Acknowledge(id);
    }

    // The byte branch is a defensive guard the writer cannot trigger, for the same bound as the retention throw in
    // Build. Only the no-ack window fires under validated options.
    public bool NeedsRepair(int slot)
    {
        if (!slots.TryGetValue(slot, out RebuildDeltaSlot? state)) return false;
        if (state.NewSentCount >= state.Options.EffectiveNoAckSendWindow) return true;
        return state.LatestBytes > 0
            && state.PinnedBytes() + state.LatestBytes > state.Options.MaxRetainedPayloadBytes;
    }

    public void Forget(int slot) => slots.Remove(slot);

    public void Clear() => slots.Clear();

    public RebuildUsage Usage(int slot)
    {
        if (!slots.TryGetValue(slot, out RebuildDeltaSlot? state)) return default;
        ProjectionRetention r = state.Retention;
        return new RebuildUsage(r.Count, r.PayloadBytes, state.Candidate, state.Acknowledged, state.NewSentCount,
            r.Entities, r.Components);
    }

    public bool TryGetRetained(int slot, ReplicationPacketId id, out ReplicationProjection projection)
    {
        if (slots.TryGetValue(slot, out RebuildDeltaSlot? state)) return state.Retention.TryGet(id, out projection);
        projection = null!;
        return false;
    }

    /// <summary>Moves the slot's next sequence, so a test reaches the wrap or a half-range gap without a loop.</summary>
    public void SeedSequence(int slot, uint sequence) => Require(slot).NextSequence = sequence;

    private RebuildDeltaSlot Require(int slot) =>
        slots.TryGetValue(slot, out RebuildDeltaSlot? state)
            ? state
            : throw new InvalidOperationException($"Slot {slot} has no started format 2 stream. Call StartRebuild.");

    // The next sequence must follow the baseline, every id that can still be acknowledged and the unsent candidate,
    // inside half the range.
    private static void RequireOrdered(RebuildDeltaSlot state, uint next)
    {
        RequireAfter(state.Acknowledged, next);
        RequireAfter(state.NewestCommitted, next);
        RequireAfter(state.Candidate, next);
    }

    private static void RequireAfter(ReplicationPacketId? reference, uint next)
    {
        if (reference is ReplicationPacketId r && !ReplicationSequence.IsNewer(next, r.Sequence))
            throw new DeltaRebuildException(DeltaRebuildFailure.SequenceAmbiguous,
                $"Format 2 sequence {next} is not ordered after {r}. Start a new epoch.");
    }

    private static string Describe(long? owner) =>
        owner?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none";
}
