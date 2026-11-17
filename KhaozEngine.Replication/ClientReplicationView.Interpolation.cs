using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;

namespace KhaozEngine.Replication;

public sealed partial class ClientReplicationView
{
    /// <summary>
    /// Records a fixed-delay interpolation sample: snapshots the current interpolatable component bytes (the values
    /// just written by <see cref="Apply"/>/<see cref="ApplyDelta"/>) into the per-component history, stamped at the
    /// arrival time <paramref name="timeSeconds"/> (a monotonic client-side render clock). <see cref="InterpolateAt"/>
    /// later lerps the two samples bracketing a render time by their true timestamps. Call once per applied
    /// snapshot/delta, right after the apply. Stamps must be non-decreasing; a stamp at or before the last recorded
    /// one overwrites it (so a burst of snapshots ingested without an intervening clock advance collapses to a single
    /// sample rather than a zero-width bracket).
    /// <para><paramref name="excludeNetId"/> (the local, predicted avatar) is never buffered: it renders from
    /// prediction, so its samples are wasted work AND buffering-then-<see cref="InterpolateAt"/> would clobber its
    /// client-world replicated position (the reconcile basis) with a stale fixed-delay value. When it CHANGES across
    /// calls (a reconnect assigns a new local id) the new id's stale sample history is dropped, so a later un-exclude
    /// of that id cannot lerp across the gap. Pass <c>null</c> (the default) to buffer everything.</para>
    /// </summary>
    public void RecordInterpolationSample(double timeSeconds, long? excludeNetId = null)
        => RecordInterpolationSample(timeSeconds, excludeNetId, serverTick: null);

    /// <summary>
    /// Records the same fixed-delay sample with the producing server tick, or <c>null</c> when unavailable.
    /// The tick is metadata and does not affect sample ordering or position. To convert tick differences
    /// into elapsed time, the host must tick once per configured simulation step.
    /// </summary>
    public void RecordInterpolationSample(double timeSeconds, long? excludeNetId, long? serverTick)
    {
        if (excludeNetId != excludedFromInterpolation)
        {
            // The excluded id changed (first ingest, or a reconnect re-id): drop the newly-excluded id's stale buffer.
            if (excludeNetId is long changed) RemoveSampleHistory(changed);
            excludedFromInterpolation = excludeNetId;
        }
        foreach (KeyValuePair<(long netId, ushort typeId), byte[]> kv in currentBytes)
        {
            if (excludeNetId is long ex && kv.Key.netId == ex) continue;   // local avatar: predicted, never interpolated
            if (!registry.TryGet(kv.Key.typeId, out ComponentCodec codec) || !codec.FixedDelaySampled) continue;
            if (!sampleHistory.TryGetValue(kv.Key, out InterpolationSampleHistory? history))
                sampleHistory[kv.Key] = history = new InterpolationSampleHistory();
            history.Record(timeSeconds, kv.Value, serverTick);
        }
    }

    /// <summary>
    /// Fixed-delay snapshot interpolation: for every interpolatable component writes the value at
    /// <paramref name="renderTime"/> by lerping the two buffered samples bracketing it (by their true timestamps).
    /// A render time before the oldest sample clamps to it (warm-up); a render time at/past the newest HOLDS at the
    /// newest (snapshot starvation - never extrapolates) and flags the entity (see
    /// <see cref="WasHeldAtLastInterpolation"/>). A single-sample component renders that sample. Idempotent for a
    /// given <paramref name="renderTime"/>. Feed a monotonically increasing render time: samples strictly below the
    /// lower bracket are pruned each call, so the history stays bounded to what is still reachable.
    /// <para><paramref name="excludeNetId"/> (the local, predicted avatar) is skipped: it renders from prediction and
    /// its replicated position must stay the last-received authoritative value (see <see cref="RecordInterpolationSample(double, long?)"/>).
    /// In steady state that id has no buffered history anyway (it was never recorded); the skip also covers the frame
    /// right after a reconnect re-id, before the next record purges its stale buffer. Pass <c>null</c> to interpolate
    /// everything.</para>
    /// </summary>
    public void InterpolateAt(World world, double renderTime, long? excludeNetId = null)
    {
        heldNetIds.Clear();
        foreach (var (key, history) in sampleHistory)
        {
            history.ForgetDrawnSegment();
            if (excludeNetId is long ex && key.netId == ex) continue;
            if (!entityByNetId.TryGetValue(key.netId, out Entity entity) || !world.IsAlive(entity)) continue;
            if (!registry.TryGet(key.typeId, out ComponentCodec codec) || !codec.FixedDelaySampled) continue;

            var (from, to, fraction, held) = history.Resolve(renderTime);
            WriteSample(codec, world, entity, from.Bytes, to.Bytes, fraction);
            if (held) heldNetIds.Add(key.netId);
        }
    }

    /// <summary>
    /// Reads the two buffered values used by the most recent <see cref="InterpolateAt"/> for this
    /// entity and component. Returns false before interpolation, after a new sample or a snap, when the
    /// component was excluded, clamped, held or had only one sample, or when the type does not match.
    /// Each tick is null if its endpoint was recorded without one. Interpreting tick differences as
    /// elapsed time assumes the host ticks once per configured simulation step.
    /// </summary>
    public bool TryGetInterpolationSegment<T>(long netId, ushort typeId, out T from, out T to,
        out long? fromTick, out long? toTick) where T : struct, IComponent
    {
        from = to = default;
        fromTick = toTick = null;
        if (!sampleHistory.TryGetValue((netId, typeId), out InterpolationSampleHistory? history)
            || !history.HasDrawnSegment
            || !registry.TryGet(typeId, out ComponentCodec codec)
            || codec.ReadComponent is not Func<BinaryReader, T> read)
            return false;

        var first = history.Samples[0];
        var second = history.Samples[1];
        try
        {
            framedPayload.Retarget(first.Bytes, 0, first.Bytes.Length);
            from = read(framedReader);
            framedPayload.Retarget(second.Bytes, 0, second.Bytes.Length);
            to = read(framedReader);
        }
        finally
        {
            framedPayload.Release();
        }
        fromTick = first.ServerTick;
        toTick = second.ServerTick;
        return true;
    }

    // Writes the value at the resolved bracket: for a lerp component, lerp(lo, hi, frac); for a DISCRETE component pick
    // the NEAREST of the two bracketing samples (frac <= 0.5 -> the lower, tie to lower, mirroring the lerp clamp) and
    // set its bytes verbatim - a flag / quantized rate rides the same delayed timeline as position but is never blended
    // into an impossible in-between. Static (no capture) so the per-frame InterpolateAt hot path allocates nothing.
    private static void WriteSample(ComponentCodec codec, World world, Entity e, byte[] loBytes, byte[] hiBytes, float frac)
    {
        if (codec.Discrete) codec.SetFromBytes!(world, e, frac <= 0.5f ? loBytes : hiBytes);
        else codec.LerpFromBytes!(world, e, loBytes, hiBytes, frac);
    }

    /// <summary>True if the entity with network id <paramref name="netId"/> was HELD at the newest buffered sample
    /// during the most recent <see cref="InterpolateAt"/> - i.e. ANY of its interpolatable components ran past the
    /// buffer (a snapshot starvation hold). Aggregated across the entity's components (held iff at least one held).
    /// Diagnostics-only; recomputed each <see cref="InterpolateAt"/>.</summary>
    public bool WasHeldAtLastInterpolation(long netId) => heldNetIds.Contains(netId);

    /// <summary>
    /// Drops every buffered interpolation sample for the entity with network id <paramref name="netId"/> EXCEPT the
    /// newest, so the next <see cref="InterpolateAt"/> renders that newest sample (a hard cut) instead of lerping
    /// across the gap between the pre- and post-jump samples. The caller uses this when an entity teleports: its
    /// buffer then straddles the discontinuity (a far-apart old + new position), and interpolating between them would
    /// streak the entity across the world. After the snap the buffer holds one sample, so the entity renders at the
    /// destination and smooth interpolation resumes naturally as later samples arrive. No-op for an untracked or
    /// already-single-sample entity. This view has no notion of what a "teleport" is (that lives in the netcode layer,
    /// keyed off the replicated teleport epoch); it just exposes the flush.
    /// </summary>
    public void SnapInterpolationToNewest(long netId)
    {
        foreach (var (key, history) in sampleHistory)
        {
            if (key.netId == netId) history.SnapToNewest();
        }
    }
}
