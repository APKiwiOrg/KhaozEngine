using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;

namespace KhaozEngine.Replication;

/// <summary>
/// Applies full-state snapshots into a client <see cref="World"/>: spawns entities new this snapshot, despawns
/// entities absent from it, and updates the rest (keyed by <see cref="NetId"/>). For components registered with
/// a lerp, it double-buffers the last two snapshots so <see cref="Interpolate"/> can smooth them at render time,
/// and (the preferred path) keeps a timestamped sample history so <see cref="InterpolateAt"/> can render on a
/// fixed delay by lerping the two bracketing snapshots by their true timestamps.
/// </summary>
public sealed class ClientReplicationView
{
    private readonly ReplicationRegistry registry;
    private readonly Dictionary<long, Entity> entityByNetId = new();
    private readonly Dictionary<(long netId, ushort typeId), byte[]> currentBytes = new();
    private readonly Dictionary<(long netId, ushort typeId), byte[]> previousBytes = new();

    // Fixed-delay interpolation buffer: a per-component timestamped history of the interpolatable bytes, so
    // InterpolateAt can render at an arbitrary render time by lerping the two bracketing samples by their true
    // timestamps (see RecordInterpolationSample / InterpolateAt). Independent of the previous/current double-buffer
    // that drives the legacy alpha-ramp Interpolate.
    private readonly Dictionary<(long netId, ushort typeId), List<(double t, byte[] bytes)>> sampleHistory = new();
    // A remote whose most recent InterpolateAt clamped at the newest sample (renderTime ran past the buffer): a
    // genuine snapshot starvation "hold". Diagnostics read it via WasHeldAtLastInterpolation; recomputed per InterpolateAt.
    private readonly HashSet<long> heldNetIds = new();
    // The net id currently excluded from fixed-delay interpolation (the local, predicted avatar). It renders from
    // prediction, so its interpolation samples are wasted work; more importantly its client-world ReplicatedPosition
    // must stay the last-RECEIVED authoritative value (the reconcile basis), never a presentation-interpolated one, or
    // a post-teleport static entity feeds a stale pre-teleport basis back into the reconcile. Tracked so a CHANGE (a
    // reconnect assigning a new local id) drops the new id's stale buffer. Null = exclude nothing (the default; other
    // consumers pass no id). Kept in sync from RecordInterpolationSample, whose caller owns the local id per ingest.
    private long? excludedFromInterpolation;

    public ClientReplicationView(ReplicationRegistry registry)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>The live NetId→Entity map.</summary>
    public IReadOnlyDictionary<long, Entity> Entities => entityByNetId;

    /// <summary>Looks up the local entity for a network id.</summary>
    public bool TryGetEntity(long netId, out Entity entity) => entityByNetId.TryGetValue(netId, out entity);

    /// <summary>The snapshot seq of the last applied delta (-1 before any). Acknowledge this back to the server.</summary>
    public int LastAppliedSeq { get; private set; } = -1;

    /// <summary>
    /// Applies a snapshot: spawn-if-new + update every netId present, despawn every netId absent. Captures
    /// interpolatable components' raw bytes (shifting the prior snapshot's into the interpolation "previous").
    /// </summary>
    public void Apply(World world, byte[] snapshot) => ApplyInternal(world, snapshot, unknownExtensionSink: null);

    private void ApplyInternal(World world, byte[] snapshot, Action<long, ushort, byte[]>? unknownExtensionSink)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));

        // Shift current -> previous for interpolation.
        previousBytes.Clear();
        foreach (KeyValuePair<(long, ushort), byte[]> kv in currentBytes) previousBytes[kv.Key] = kv.Value;
        currentBytes.Clear();

        using var ms = new MemoryStream(snapshot);
        using var br = new BinaryReader(ms);
        int count = br.ReadInt32();
        var seen = new HashSet<long>();
        for (int i = 0; i < count; i++)
        {
            long netId = br.ReadInt64();
            seen.Add(netId);
            Entity entity = GetOrSpawn(world, netId);
            ReadEntityComponents(world, entity, netId, ms, br, snapshot, "Snapshot", unknownExtensionSink);
        }

        // Full-state: anything we still track but didn't see is gone.
        List<long>? gone = null;
        foreach (KeyValuePair<long, Entity> kv in entityByNetId)
            if (!seen.Contains(kv.Key)) (gone ??= new List<long>()).Add(kv.Key);
        if (gone is not null)
        {
            foreach (long netId in gone)   // long, not int: a full-snapshot despawn must not truncate a 64-bit id
            {
                if (world.IsAlive(entityByNetId[netId])) world.Despawn(entityByNetId[netId]);
                entityByNetId.Remove(netId);
                RemoveEntityBuffers(netId);   // also drop the fixed-delay sample history for the departed entity
            }
        }
    }

    /// <summary>
    /// Non-throwing <see cref="Apply"/>: catches a malformed/incompatible snapshot (e.g. an unregistered
    /// component type id from a newer server protocol) and returns <c>false</c> with the reason in
    /// <paramref name="error"/> instead of throwing into the caller's frame loop. A failed apply may leave the
    /// view partially updated, so the caller should treat <c>false</c> as terminal for the session (disconnect
    /// and surface "client out of date"), not retry against the same stream. Returns <c>true</c> on success.
    /// </summary>
    public bool TryApply(World world, byte[] snapshot, out string? error)
    {
        try
        {
            Apply(world, snapshot);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Applies a persist snapshot like <see cref="TryApply"/>, but additionally COLLECTS every extension frame whose
    /// type id this registry does not know (id &gt;= <see cref="ReplicationRegistry.FirstExtensionTypeId"/>,
    /// unregistered) as a raw <see cref="RetainedComponent"/> so the caller can retain it and re-persist it verbatim
    /// (retain-and-rewrite), instead of the silent skip <see cref="Apply"/> does. Built-in frames stay
    /// throw-on-unknown (a hard protocol mismatch), so an unknown built-in id yields <c>false</c> with the reason in
    /// <paramref name="error"/> and an empty <paramref name="retained"/>. Never throws. Intended for the cell
    /// persistence restore path (a throwaway view), where a registry downgrade must not destroy data at rest.
    /// </summary>
    public bool TryApplyRetainingUnknown(World world, byte[] snapshot,
        out IReadOnlyList<RetainedComponent> retained, out string? error)
    {
        var collected = new List<RetainedComponent>();
        try
        {
            ApplyInternal(world, snapshot,
                (netId, typeId, payload) => collected.Add(new RetainedComponent(netId, typeId, payload)));
            retained = collected;
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            retained = Array.Empty<RetainedComponent>();
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Non-throwing <see cref="ApplyDelta"/>: same contract as <see cref="TryApply"/> (a baseline
    /// mismatch or an unregistered type id yields <c>false</c> + <paramref name="error"/> instead of a throw).</summary>
    public bool TryApplyDelta(World world, byte[] delta, out string? error)
    {
        try
        {
            ApplyDelta(world, delta);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private Entity GetOrSpawn(World world, long netId)
    {
        if (entityByNetId.TryGetValue(netId, out Entity e) && world.IsAlive(e)) return e;
        e = world.Spawn();
        world.Set(e, new NetId(netId));
        entityByNetId[netId] = e;
        return e;
    }

    /// <summary>
    /// Applies a baseline+delta produced by <see cref="ServerReplicator.WriteFor"/> or
    /// <see cref="AoiDeltaReplicator.WriteFor"/>: despawns removed entities, spawns/updates changed ones (removing
    /// listed components), and maintains interpolation buffers so <see cref="Interpolate"/> keeps working. A delta
    /// whose baseline is at or before <see cref="LastAppliedSeq"/> is a valid rebuild: the server builds from the
    /// client's last ACKED baseline, which lags what the client has applied whenever an ack is in flight or was lost,
    /// so re-applying the diff from that older baseline is idempotent and self-heals (a dropped delta / ack needs no
    /// full resync). Only a baseline AHEAD of <see cref="LastAppliedSeq"/> is a genuine gap and throws (the caller
    /// should then await a full snapshot, baseline -1). A <c>baseline -1</c> delta is a full snapshot: entities the
    /// client still tracks but the delta omits are despawned (the same full-state semantics as <see cref="Apply"/>).
    /// </summary>
    public void ApplyDelta(World world, byte[] delta)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (delta is null) throw new ArgumentNullException(nameof(delta));

        using var ms = new MemoryStream(delta);
        using var br = new BinaryReader(ms);
        int baselineSeq = br.ReadInt32();
        int snapshotSeq = br.ReadInt32();
        if (baselineSeq > LastAppliedSeq)
            throw new InvalidOperationException(
                $"Delta baseline {baselineSeq} is ahead of last applied {LastAppliedSeq}; a full snapshot is needed.");

        // Interpolation: the pre-delta state becomes 'previous'; changed components below refresh 'current'.
        previousBytes.Clear();
        foreach (KeyValuePair<(long netId, ushort typeId), byte[]> kv in currentBytes) previousBytes[kv.Key] = kv.Value;

        int removedCount = br.ReadInt32();
        for (int i = 0; i < removedCount; i++)
        {
            long netId = br.ReadInt64();
            if (entityByNetId.TryGetValue(netId, out Entity e))
            {
                if (world.IsAlive(e)) world.Despawn(e);
                entityByNetId.Remove(netId);
            }
            RemoveEntityBuffers(netId);
        }

        // A baseline -1 delta is a full snapshot; track what it carries so anything else can be despawned below.
        HashSet<long>? seen = baselineSeq == -1 ? new HashSet<long>() : null;
        int changedCount = br.ReadInt32();
        for (int i = 0; i < changedCount; i++)
        {
            long netId = br.ReadInt64();
            seen?.Add(netId);
            br.ReadByte(); // isNew flag: GetOrSpawn handles both; read only to stay byte-aligned
            Entity entity = GetOrSpawn(world, netId);

            int removedCompCount = br.ReadInt32();
            for (int r = 0; r < removedCompCount; r++)
            {
                ushort rtid = br.ReadUInt16();
                if (registry.TryGet(rtid, out ComponentCodec rcodec))
                {
                    if (world.IsAlive(entity)) rcodec.RemoveComponent(world, entity);
                    currentBytes.Remove((netId, rtid));
                    previousBytes.Remove((netId, rtid));
                    // Also drop the fixed-delay sample history, else InterpolateAt keeps lerping the stale samples and
                    // world.Set re-adds the component the server just removed, every frame (resurrection).
                    sampleHistory.Remove((netId, rtid));
                }
            }

            ReadEntityComponents(world, entity, netId, ms, br, delta, "Delta", unknownExtensionSink: null);
        }

        // Full snapshot (baseline -1): anything we still track but the snapshot didn't carry is gone.
        if (seen is not null)
        {
            List<long>? gone = null;
            foreach (KeyValuePair<long, Entity> kv in entityByNetId)
                if (!seen.Contains(kv.Key)) (gone ??= new List<long>()).Add(kv.Key);
            if (gone is not null)
                foreach (long netId in gone)
                {
                    if (world.IsAlive(entityByNetId[netId])) world.Despawn(entityByNetId[netId]);
                    entityByNetId.Remove(netId);
                    RemoveEntityBuffers(netId);
                }
        }

        LastAppliedSeq = snapshotSeq;
    }

    /// <summary>
    /// Reads one entity's component stream up to the <c>[0]</c> terminator, deserializing each registered
    /// component and, for a consumer <b>extension</b> component (type id >= <see cref="ReplicationRegistry.FirstExtensionTypeId"/>,
    /// length-prefixed on the wire), SKIPPING it when this client's registry does not know the id, so an older
    /// client tolerates a newer server that added a component. An unknown BUILT-IN id (below the floor, unframed)
    /// is a hard protocol mismatch and throws (<paramref name="streamKind"/> names the stream for the message),
    /// caught by <see cref="TryApply"/>/<see cref="TryApplyDelta"/> and surfaced as "client out of date".
    /// </summary>
    private void ReadEntityComponents(World world, Entity entity, long netId, MemoryStream ms, BinaryReader br,
        byte[] backing, string streamKind, Action<long, ushort, byte[]>? unknownExtensionSink)
    {
        while (true)
        {
            ushort typeId = br.ReadUInt16();
            if (typeId == 0) break;
            bool extension = ReplicationRegistry.IsExtension(typeId);
            int len = extension ? br.Read7BitEncodedInt() : -1;   // extension payloads carry a length
            long posBefore = ms.Position;
            // Hostile/corrupt-safe: a bad length must never seek backward (a loop) or past the buffer. Turn it into a
            // clean caught error (TryApply -> disconnect), not an unbounded read.
            if (extension && (len < 0 || posBefore + len > ms.Length))
                throw new InvalidOperationException($"{streamKind} extension component {typeId} has an invalid length {len}.");
            if (registry.TryGet(typeId, out ComponentCodec codec))
            {
                codec.Deserialize(world, entity, br);
                long end = extension ? posBefore + len : ms.Position;   // codec consumes exactly len for extensions
                if (extension) ms.Position = end;                       // re-align defensively past the framed payload
                if (codec.Interpolatable)
                {
                    var slice = new byte[end - posBefore];
                    Array.Copy(backing, (int)posBefore, slice, 0, slice.Length);
                    currentBytes[(netId, typeId)] = slice;
                }
            }
            else if (extension)
            {
                if (unknownExtensionSink is not null)
                {
                    // Retain the raw frame (retain-and-rewrite) instead of dropping it, so a registry downgrade does
                    // not destroy data at rest. The caller re-emits it verbatim on the next save.
                    var payload = new byte[len];
                    Array.Copy(backing, (int)posBefore, payload, 0, len);
                    unknownExtensionSink(netId, typeId, payload);
                }
                ms.Position = posBefore + len;   // unknown extension: skip its framed payload (forward-compat)
            }
            else
            {
                throw new InvalidOperationException($"{streamKind} references unregistered type id {typeId}.");
            }
        }
    }

    private void RemoveEntityBuffers(long netIdToRemove)
    {
        var keys = new List<(long netId, ushort typeId)>();
        foreach ((long netId, ushort typeId) key in currentBytes.Keys) if (key.netId == netIdToRemove) keys.Add(key);
        foreach ((long netId, ushort typeId) key in keys) currentBytes.Remove(key);
        keys.Clear();
        foreach ((long netId, ushort typeId) key in previousBytes.Keys) if (key.netId == netIdToRemove) keys.Add(key);
        foreach ((long netId, ushort typeId) key in keys) previousBytes.Remove(key);
        keys.Clear();
        foreach ((long netId, ushort typeId) key in sampleHistory.Keys) if (key.netId == netIdToRemove) keys.Add(key);
        foreach ((long netId, ushort typeId) key in keys) sampleHistory.Remove(key);
    }

    // Drops ONLY the fixed-delay interpolation history for one net id (leaves the current/previous double-buffer that
    // feeds the legacy Interpolate). Used when the interpolation-excluded id changes: the newly-excluded local id's
    // stale samples (buffered while it was a remote, pre-reconnect) are cleared so a later un-exclude cannot lerp them.
    private void RemoveSampleHistory(long netIdToRemove)
    {
        var keys = new List<(long netId, ushort typeId)>();
        foreach ((long netId, ushort typeId) key in sampleHistory.Keys) if (key.netId == netIdToRemove) keys.Add(key);
        foreach ((long netId, ushort typeId) key in keys) sampleHistory.Remove(key);
    }

    /// <summary>
    /// Writes interpolated values (<c>lerp(previous, current, alpha)</c>) for every interpolatable component
    /// that has both a previous and current snapshot. <paramref name="alpha"/> is the render fraction in [0,1].
    /// The legacy estimate-and-ramp path; prefer the fixed-delay <see cref="InterpolateAt"/>.
    /// </summary>
    public void Interpolate(World world, float alpha)
    {
        foreach (KeyValuePair<(long netId, ushort typeId), byte[]> kv in currentBytes)
        {
            if (!previousBytes.TryGetValue(kv.Key, out byte[]? prev)) continue;
            if (!entityByNetId.TryGetValue(kv.Key.netId, out Entity e) || !world.IsAlive(e)) continue;
            if (!registry.TryGet(kv.Key.typeId, out ComponentCodec codec) || codec.LerpFromBytes is null) continue;
            codec.LerpFromBytes(world, e, prev, kv.Value, alpha);
        }
    }

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
            if (!registry.TryGet(kv.Key.typeId, out ComponentCodec codec) || codec.LerpFromBytes is null) continue;
            if (!sampleHistory.TryGetValue(kv.Key, out List<(double t, byte[] bytes)>? hist))
                sampleHistory[kv.Key] = hist = new List<(double t, byte[] bytes)>();
            if (hist.Count > 0 && timeSeconds <= hist[^1].t) hist[^1] = (timeSeconds, kv.Value);
            else hist.Add((timeSeconds, kv.Value));
            // InterpolateAt prunes below the render time each frame, so history normally stays at ~2-3 samples. This
            // is only a backstop for a consumer that ingests but never presents (never calls InterpolateAt): cap the
            // history so it cannot grow without bound.
            if (hist.Count > MaxHistorySamples) hist.RemoveRange(0, hist.Count - MaxHistorySamples);
        }
    }

    // ~20 s of 30 Hz history: far beyond any real interpolation delay, so it only trips on a never-presenting consumer.
    private const int MaxHistorySamples = 600;

    /// <summary>
    /// Fixed-delay snapshot interpolation: for every interpolatable component writes the value at
    /// <paramref name="renderTime"/> by lerping the two buffered samples bracketing it (by their true timestamps).
    /// A render time before the oldest sample clamps to it (warm-up); a render time at/past the newest HOLDS at the
    /// newest (snapshot starvation - never extrapolates) and flags the entity (see
    /// <see cref="WasHeldAtLastInterpolation"/>). A single-sample component renders that sample. Idempotent for a
    /// given <paramref name="renderTime"/>. Feed a monotonically increasing render time: samples strictly below the
    /// lower bracket are pruned each call, so the history stays bounded to what is still reachable.
    /// <para><paramref name="excludeNetId"/> (the local, predicted avatar) is skipped: it renders from prediction and
    /// its replicated position must stay the last-received authoritative value (see <see cref="RecordInterpolationSample"/>).
    /// In steady state that id has no buffered history anyway (it was never recorded); the skip also covers the frame
    /// right after a reconnect re-id, before the next record purges its stale buffer. Pass <c>null</c> to interpolate
    /// everything.</para>
    /// </summary>
    public void InterpolateAt(World world, double renderTime, long? excludeNetId = null)
    {
        heldNetIds.Clear();
        foreach (KeyValuePair<(long netId, ushort typeId), List<(double t, byte[] bytes)>> kv in sampleHistory)
        {
            if (excludeNetId is long ex && kv.Key.netId == ex) continue;   // local avatar: predicted, never interpolated
            List<(double t, byte[] bytes)> hist = kv.Value;
            if (hist.Count == 0) continue;
            if (!entityByNetId.TryGetValue(kv.Key.netId, out Entity e) || !world.IsAlive(e)) continue;
            if (!registry.TryGet(kv.Key.typeId, out ComponentCodec codec) || codec.LerpFromBytes is null) continue;

            // The last sample at or before renderTime is the lower bracket (history is time-ascending).
            int lo = -1;
            for (int i = 0; i < hist.Count; i++) { if (hist[i].t <= renderTime) lo = i; else break; }

            if (lo < 0)
            {
                // renderTime precedes the whole buffer: clamp to the oldest sample (no backward extrapolation).
                codec.LerpFromBytes(world, e, hist[0].bytes, hist[0].bytes, 0f);
                continue;
            }
            if (lo >= hist.Count - 1)
            {
                // renderTime is at/past the newest sample: HOLD there, never extrapolate. Flag a genuine starvation
                // hold (renderTime strictly past the newest), then keep only the newest sample.
                (double t, byte[] bytes) newest = hist[lo];
                codec.LerpFromBytes(world, e, newest.bytes, newest.bytes, 0f);
                if (renderTime > newest.t + 1e-9) heldNetIds.Add(kv.Key.netId);
                if (lo > 0) hist.RemoveRange(0, lo);
                continue;
            }
            (double t, byte[] bytes) a = hist[lo];
            (double t, byte[] bytes) b = hist[lo + 1];
            double span = b.t - a.t;
            float frac = span > 0 ? (float)Math.Clamp((renderTime - a.t) / span, 0.0, 1.0) : 1f;
            codec.LerpFromBytes(world, e, a.bytes, b.bytes, frac);
            if (lo > 0) hist.RemoveRange(0, lo);   // renderTime only increases: earlier samples are unreachable.
        }
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
        foreach (KeyValuePair<(long netId, ushort typeId), List<(double t, byte[] bytes)>> kv in sampleHistory)
        {
            if (kv.Key.netId != netId) continue;
            List<(double t, byte[] bytes)> hist = kv.Value;
            if (hist.Count > 1) hist.RemoveRange(0, hist.Count - 1);   // keep only the newest (post-jump) sample
        }
    }
}
