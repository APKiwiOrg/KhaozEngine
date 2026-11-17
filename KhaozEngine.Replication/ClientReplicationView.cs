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
/// <para>This adapter never advances the supplied world's tick. A direct owner calling Apply, ApplyDelta or
/// driving ClientDeltaRebuild must call World.AdvanceTick once per receive boundary before applying the batch.
/// Read changes and events before the next boundary. WorldClient owns this for its private world, and
/// TileWorldClient does so by default for its public world.</para>
/// </summary>
public sealed partial class ClientReplicationView
{
    private readonly ReplicationRegistry registry;
    private readonly Dictionary<long, Entity> entityByNetId = new();
    private readonly Dictionary<(long netId, ushort typeId), byte[]> currentBytes = new();
    private readonly Dictionary<(long netId, ushort typeId), byte[]> previousBytes = new();

    // Fixed-delay interpolation buffer: a per-component timestamped history of the interpolatable bytes, so
    // InterpolateAt can render at an arbitrary render time by lerping the two bracketing samples by their true
    // timestamps (see RecordInterpolationSample / InterpolateAt). Independent of the previous/current double-buffer
    // that drives the legacy alpha-ramp Interpolate.
    private readonly Dictionary<(long netId, ushort typeId), InterpolationSampleHistory> sampleHistory = new();

    // Which typeIds are buffered under each netId, so dropping one entity's state is a lookup rather than three full
    // key scans over every (netId, typeId) pair the client tracks. Removal is called once per departed entity from
    // three sites on the delta/snapshot apply path, so the scans cost O(removed x tracked): an AoI boundary crossing
    // in a dense area drops many entities in one delta, which is exactly where a hitch shows (#138).
    // Fed from the ONE place currentBytes gains a key: previousBytes and sampleHistory only ever take copies of
    // currentBytes' keys, so tracking that single write covers all three dictionaries. Deliberately allowed to be a
    // SUPERSET (a component removed from a still-present entity leaves its typeId listed), because a stale entry
    // costs a no-op Remove while a missing one would strand a buffer. The whole entry goes when the entity does.
    private readonly Dictionary<long, HashSet<ushort>> typeIdsByNetId = new();
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

    // The window an extension codec actually reads through: re-pointed at each component's own length-prefixed
    // payload so a codec cannot reach the bytes of whatever rides behind it (see FramedPayloadStream for why that
    // matters). One pair per view rather than one per component, so bounding a payload allocates nothing.
    private readonly FramedPayloadStream framedPayload = new();
    private readonly BinaryReader framedReader;

    // Test seam (InternalsVisibleTo): how many bytes of a snapshot buffer this view's framed window still points at.
    // Zero is the only value an apply may leave behind, a FAILED apply included, because a non-zero one means the
    // view is holding that snapshot's byte[] alive until the next Retarget.
    internal long FramedWindowLength => framedPayload.Length;

    public ClientReplicationView(ReplicationRegistry registry)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        framedReader = new BinaryReader(framedPayload);
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
    /// listed components), and maintains interpolation buffers so <see cref="Interpolate"/> keeps working. Legacy
    /// reliable contract: the writers diff against the projection last sent to this receiver and the caller ships every
    /// delta exactly once, in order, over a reliable-ordered channel, so each delta names the state this view already
    /// holds and the overlay is exact. A delta whose baseline is at or before <see cref="LastAppliedSeq"/> is still
    /// accepted, but that overlay is not a reconstruction and cannot restore what a newer applied delta changed, so it
    /// is not loss recovery. Unreliable delivery uses <see cref="ClientDeltaRebuild"/>. A baseline AHEAD of
    /// <see cref="LastAppliedSeq"/> is a gap and throws. A <c>baseline -1</c> delta is a full snapshot: entities the
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
                    // All three lost the key, so the index may lose it too. Only a tightening: leaving it would be
                    // correct as well, since the index is allowed to name typeIds no dictionary still holds.
                    if (typeIdsByNetId.TryGetValue(netId, out HashSet<ushort>? tracked)) tracked.Remove(rtid);
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
    /// <para>A KNOWN extension codec is handed a reader bounded to exactly its own framed payload, never the reader
    /// over the whole stream, so a payload that lies about a declared length of its own cannot be satisfied out of
    /// the components behind it. A built-in frame is unframed and has no such bound to give: it reads through the
    /// stream reader as before.</para>
    /// </summary>
    private void ReadEntityComponents(World world, Entity entity, long netId, MemoryStream ms, BinaryReader br,
        byte[] backing, string streamKind, Action<long, ushort, byte[]>? unknownExtensionSink)
    {
        // The finally is the point: every exit from this loop has to drop the window, an ABORTED apply included. A
        // lying length and an unregistered built-in id both throw from inside here, TryApply turns that into a false,
        // and the caller drops the frame and carries on, so without the finally the view kept the failed snapshot's
        // byte[] alive until the next Retarget happened to re-point the window.
        try
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
                    if (extension)
                    {
                        // Bounded to this component's own payload. Unbounded, a codec that reads a declared length out
                        // of its payload has the WHOLE remaining snapshot to satisfy it, so a lie is caught only when it
                        // runs off the end of the stream and is otherwise served out of the following components' bytes.
                        framedPayload.Retarget(backing, (int)posBefore, len);
                        codec.Deserialize(world, entity, framedReader);
                    }
                    else
                    {
                        codec.Deserialize(world, entity, br);
                    }
                    long end = extension ? posBefore + len : ms.Position;   // codec consumes exactly len for extensions
                    if (extension) ms.Position = end;                       // the framed read never moved ms: step it over
                    if (codec.FixedDelaySampled)   // interpolated OR discrete nearest-sampled: both need the timestamped bytes
                    {
                        var slice = new byte[end - posBefore];
                        Array.Copy(backing, (int)posBefore, slice, 0, slice.Length);
                        SetCurrent(netId, typeId, slice);
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
        finally
        {
            framedPayload.Release();   // do not keep this snapshot's buffer alive until the next apply re-points it
        }
    }

    // The ONE place currentBytes gains a key, and therefore the one place the netId index has to be fed.
    private void SetCurrent(long netId, ushort typeId, byte[] bytes)
    {
        currentBytes[(netId, typeId)] = bytes;
        if (!typeIdsByNetId.TryGetValue(netId, out HashSet<ushort>? typeIds))
            typeIdsByNetId[netId] = typeIds = new HashSet<ushort>();
        typeIds.Add(typeId);
    }

    // Drops every buffer belonging to one departed entity: the entity's own typeId list, not a scan of all three
    // dictionaries. Costs the components on that entity rather than everything the client tracks, and allocates
    // nothing (the old scans needed a collector list per call).
    private void RemoveEntityBuffers(long netIdToRemove)
    {
        if (!typeIdsByNetId.Remove(netIdToRemove, out HashSet<ushort>? typeIds)) return;
        foreach (ushort typeId in typeIds)
        {
            currentBytes.Remove((netIdToRemove, typeId));
            previousBytes.Remove((netIdToRemove, typeId));
            sampleHistory.Remove((netIdToRemove, typeId));
        }
    }

    // Drops ONLY the fixed-delay interpolation history for one net id (leaves the current/previous double-buffer that
    // feeds the legacy Interpolate). Used when the interpolation-excluded id changes: the newly-excluded local id's
    // stale samples (buffered while it was a remote, pre-reconnect) are cleared so a later un-exclude cannot lerp them.
    private void RemoveSampleHistory(long netIdToRemove)
    {
        // The index entry STAYS: current/previous still hold this entity's keys, and only its history is being dropped.
        if (!typeIdsByNetId.TryGetValue(netIdToRemove, out HashSet<ushort>? typeIds)) return;
        foreach (ushort typeId in typeIds) sampleHistory.Remove((netIdToRemove, typeId));
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
}
