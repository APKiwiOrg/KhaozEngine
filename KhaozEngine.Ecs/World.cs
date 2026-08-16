using System;
using System.Collections.Generic;
using KhaozEngine.Primitives;

namespace KhaozEngine.Ecs;

/// <summary>
/// An archetype entity-component-system world. Entities are versioned handles; components are
/// structs stored in archetype columns. See docs/USING for the contract.
/// </summary>
public sealed partial class World
{
    private struct Record { public Archetype Archetype; public int Row; public uint Version; public bool Alive; }

    private Record[] _records = new Record[64];
    private int _nextId;
    private readonly Stack<int> _free = new();

    internal readonly ComponentRegistry Reg = new();
    internal readonly Dictionary<ArchetypeSignature, Archetype> Archetypes = new();
    internal readonly List<Archetype> ArchetypeOrder = new();   // archetypes in creation order (deterministic iteration)
    internal int ArchetypeGen;
    private readonly Archetype _empty;

    // Hazard guard for ParallelForEach: true while a parallel section runs. Any reentrant world structural /
    // component / query call from a worker action breaks per-row-purity and throws (see ThrowIfInParallelSection).
    private volatile bool _inParallelSection;

    /// <summary>
    /// Whether ParallelForEach actions are checked for per-row-purity: a reentrant world call from inside a parallel
    /// action throws <see cref="ParallelAccessViolationException"/>. On by default so dev/tests catch hazards; the
    /// cost is one bool check per world call (negligible outside a parallel section). A shipping server may set this
    /// false in a proven-pure hot loop for maximum speed.
    /// </summary>
    public bool ParallelHazardChecks { get; set; } = true;

    // Bracket a parallel section (ParallelForEach). Worker actions run while the flag is set.
    internal void BeginParallelSection() => _inParallelSection = true;
    internal void EndParallelSection() => _inParallelSection = false;

    // Called at the top of every world structural / component / query entry point. While a parallel section is
    // active, any such reentrant call breaks the per-row-pure contract, so throw (when the guard is enabled).
    private void ThrowIfInParallelSection(string operation)
    {
        if (ParallelHazardChecks && _inParallelSection)
            throw new ParallelAccessViolationException(operation);
    }

    /// <summary>
    /// Counts structural changes: every change that adds or removes an archetype ROW (Spawn, Despawn, and the
    /// archetype move behind adding or removing a component). That is exactly the set of changes that can
    /// swap-remove or resize the row range a serial <see cref="Query.ForEach{T1}(RefAction{T1})"/> or
    /// <see cref="Query.Entities"/> is walking, so iteration snapshots this and rechecks it after every callback
    /// (see <see cref="StructuralChangeDuringIterationException"/>). A <see cref="Set{T}"/> that OVERWRITES an
    /// already-present component moves no row and deliberately does not count: writing components mid-iteration is
    /// legal and every caller that does it has to keep working.
    /// </summary>
    internal int StructuralVersion { get; private set; }

    /// <summary>The world call behind the most recent structural change, so the iteration guard can name the
    /// operation instead of only reporting that something moved. Always a literal, so the store never allocates.</summary>
    internal string LastStructuralOp { get; private set; } = string.Empty;

    private void MarkStructuralChange(string operation)
    {
        StructuralVersion++;
        LastStructuralOp = operation;
    }

    public World()
    {
        _empty = new Archetype(Array.Empty<int>(), Reg);
        Archetypes[new ArchetypeSignature(Array.Empty<int>())] = _empty;
        ArchetypeOrder.Add(_empty);
        ArchetypeGen++;

        // Small prewarm covers a few levels of nested ForEach zero-alloc; deeper nesting falls back to
        // a fresh Query (see RentForEachQuery). Bound to this World so pooled queries match its archetypes.
        _forEachQueryPool = new ObjectPool<PoolableQuery>(() => new PoolableQuery(new Query(this)), prewarmCount: 4);
    }

    /// <summary>Creates a new entity with no components.</summary>
    public Entity Spawn()
    {
        ThrowIfInParallelSection(nameof(Spawn));
        int id = _free.Count > 0 ? _free.Pop() : _nextId++;
        if (id >= _records.Length) Array.Resize(ref _records, Math.Max(_records.Length * 2, id + 1));
        ref Record rec = ref _records[id];
        if (rec.Version == 0) rec.Version = 1;     // first use
        var e = new Entity(id, rec.Version);
        rec.Archetype = _empty;
        rec.Row = _empty.AddRow(e);
        rec.Alive = true;
        MarkStructuralChange(nameof(Spawn));
        return e;
    }

    /// <summary>Removes an entity (no-op on a stale/dead handle). Bumps the slot version and recycles the id.</summary>
    public void Despawn(Entity e)
    {
        ThrowIfInParallelSection(nameof(Despawn));
        if (!IsAlive(e)) return;
        DetachHierarchyOnDespawn(e);
        ref Record rec = ref _records[e.Id];
        foreach (int tid in rec.Archetype.TypeIds)
            TrackRemoved(e, tid);
        if (rec.Archetype.SwapRemove(rec.Row, out Entity moved))
            _records[moved.Id].Row = rec.Row;
        rec.Alive = false;
        rec.Version++;
        _free.Push(e.Id);
        MarkStructuralChange(nameof(Despawn));
    }

    /// <summary>True if the handle refers to a live entity (version still matches).</summary>
    public bool IsAlive(Entity e) =>
        (uint)e.Id < (uint)_records.Length && _records[e.Id].Alive && _records[e.Id].Version == e.Version;
}
