using System;
using System.Runtime.CompilerServices;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;
using S = KhaozEngine.TileWorld.Physics.TileWaterPreparedQuery.BasisState;
using H = KhaozEngine.TileWorld.Physics.TileWaterPreparedQuery.Header;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>Per-trace parent basis projections lent to that parent's faces under an exact key.</summary>
/// <remarks>A face copies its parent's planes before appending its own, so a basis with the same ordered
/// plane indices executes the same projection expressions on the same operand bits. Only values the parent
/// already reached and resolved are copied. Every face still visits its own bases in order, clips, checks
/// failure and computes its distance, roots, uncertainty and seam. Owned by the per-trace preparation
/// storage, reserved at construction and emptied of every shape reference when the outermost trace ends.</remarks>
internal sealed class TileWaterSharedProjections
{
    // Contact traces select at most four prisms in the census corpus. Further parents fall back.
    internal const int Slots = 16;
    const int Rows = TileWaterPreparedQuery.MaxBases;

    /// <summary>One projection-only copy: Q, V and both multiplier sets, valid only when resolved.</summary>
    internal struct Row
    {
        internal bool Resolved;
        internal TileWaterPreparedQuery.Multipliers Lambda, Velocity;
        internal V Q, V;
    }

    internal readonly record struct Snapshot(int ReservedBytes, int Published, int Borrowed, int Duplicates,
        int Exhausted, int Lends, int Misses, int Nested, int Unsupported, int Bypassed, int Copies, int Fresh, int Stale);

    // Written only by tests, the test module's forced-off corpus switch and the paired benchmark. The bypass
    // runs the original expressions on identical inputs, so it changes counts and time, never output.
    internal static bool BypassForTests;

    readonly TileWaterConvexShape?[] _owners = new TileWaterConvexShape?[Slots];
    readonly H[] _keys = new H[Slots];
    readonly int[] _retained = new int[Slots];
    readonly Row[] _rows = new Row[Slots * Rows];
    int _count, _depth;
    bool _suspended, _lent;
    int _published, _borrowed, _duplicates, _exhausted, _lends, _misses, _nested, _unsupported, _bypassed, _copies, _fresh;

    internal Snapshot Last { get; private set; }
    internal static int ReservedPayloadBytes => Slots * Rows * Unsafe.SizeOf<Row>();

    internal int HeldOwnersForTests
    {
        get
        {
            int held = 0;
            foreach (var owner in _owners) if (owner is not null) held++;
            return held;
        }
    }

    internal void BeginTrace()
    {
        if (_depth++ == 0)
        {
            _published = _borrowed = _duplicates = _exhausted = _lends = _misses = 0;
            _nested = _unsupported = _bypassed = _copies = _fresh = 0;
            return;
        }
        // A reentrant trace reuses the workspace and the retained slab, so nothing published before it
        // remains trustworthy for the rest of the outer trace.
        _suspended = true;
        Clear();
    }

    internal void EndTrace()
    {
        if (_depth == 0 || --_depth > 0) return;
        Clear();
        _suspended = false;
        Last = new(ReservedPayloadBytes, _published, _borrowed, _duplicates, _exhausted, _lends, _misses,
            _nested, _unsupported, _bypassed, _copies, _fresh, 0);
    }

    /// <summary>Releases any lend on the workspace and publishes a settled parent preparation. A retained
    /// parent is borrowed from the storage slab at <paramref name="retainedOffset"/>, any other copies rows.</summary>
    internal void Settle(ref TileWaterPreparedQuery prepared, int retainedOffset)
    {
        _lent = false;
        TileWaterConvexShape shape = prepared.Shape;
        if (shape.ProjectionParent is not null || _depth == 0) return;
        if (BypassForTests) { _bypassed++; return; }
        if (_suspended) return;
        H key = prepared.SharingKey;
        if (!key.HasControlToken) { _unsupported++; return; }
        for (int i = 0; i < _count; i++)
            if (ReferenceEquals(_owners[i], shape) && Same(_keys[i], key)) { _duplicates++; return; }
        if (_count == Slots) { _exhausted++; return; }
        int slot = _count++;
        _owners[slot] = shape;
        _keys[slot] = key;
        _retained[slot] = retainedOffset;
        if (retainedOffset >= 0) _borrowed++;
        else
        {
            Copy(prepared.States, _rows.AsSpan(slot * Rows, shape.Bases.Length));
            _published++;
        }
    }

    /// <summary>Lends a face its parent's rows when the exact parent object and every key bit match. The
    /// lend lasts until the workspace is settled. Any refusal returns an empty source.</summary>
    internal TileWaterProjectionSource Lend(TileWaterConvexShape shape, scoped in H key, ReadOnlySpan<byte> slab)
    {
        TileWaterConvexShape? parent = shape.ProjectionParent;
        if (parent is null || _depth == 0) return default;
        if (BypassForTests) { _bypassed++; return default; }
        if (_suspended || _depth > 1 || _lent) { _nested++; return default; }
        if (!key.HasControlToken) { _unsupported++; return default; }
        for (int i = 0; i < _count; i++)
        {
            if (!ReferenceEquals(_owners[i], parent) || !Same(_keys[i], key)) continue;
            int offset = _retained[i];
            if (offset < 0)
            {
                _lent = true;
                _lends++;
                return new(shape.ProjectionBasisMap, _rows.AsSpan(i * Rows, parent.Bases.Length), default, this);
            }
            var states = TileWaterPreparedQuery.RetainedStates(parent, slab[offset..], out H stored);
            // The slab offset belongs to this trace. Rechecking its header keeps a reused slab from lending.
            if (!Same(stored, key)) break;
            _lent = true;
            _lends++;
            return new(shape.ProjectionBasisMap, default, states, this);
        }
        _misses++;
        return default;
    }

    /// <summary>Copies only reached and resolved projections. Every other row is marked unusable.</summary>
    internal static void Copy(ReadOnlySpan<S> states, Span<Row> rows)
    {
        for (int i = 0; i < states.Length; i++)
        {
            ref readonly S state = ref states[i];
            ref Row row = ref rows[i];
            row.Resolved = (state.Mask & 1) != 0 && state.Projected;
            if (!row.Resolved) continue;
            row.Q = state.Q;
            row.V = state.V;
            row.Lambda = state.Lambda;
            row.Velocity = state.Velocity;
        }
    }

    internal void Count(bool copied)
    {
        if (copied) _copies++;
        else _fresh++;
    }

    void Clear()
    {
        _owners.AsSpan(0, _count).Clear();
        _count = 0;
        _lent = false;
    }

    // Raw start and drive endpoints, both core endpoints, the radius bits, both error scalars and the controls.
    static bool Same(in H a, in H b) => a.HasControlToken && b.HasControlToken && a.ControlToken == b.ControlToken &&
        Same(a.Start, b.Start) && Same(a.Drive, b.Drive) && Same(a.Core, b.Core) &&
        BitConverter.SingleToInt32Bits(a.Radius) == BitConverter.SingleToInt32Bits(b.Radius) &&
        Bits(a.HeightError) == Bits(b.HeightError) && Bits(a.HorizontalError) == Bits(b.HorizontalError);
    static bool Same(V a, V b) => Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z);
    static bool Same(TileWaterArithmetic a, TileWaterArithmetic b) => Bits(a.Low) == Bits(b.Low) && Bits(a.High) == Bits(b.High);
    static long Bits(double value) => BitConverter.DoubleToInt64Bits(value);
}

/// <summary>A face's read-only view of its parent's reached projections, valid until the face is settled.</summary>
internal readonly ref struct TileWaterProjectionSource
{
    readonly ReadOnlySpan<int> _map;
    readonly ReadOnlySpan<TileWaterSharedProjections.Row> _rows;
    readonly ReadOnlySpan<S> _retained;
    readonly TileWaterSharedProjections? _owner;

    /// <summary>Either <paramref name="rows"/> or <paramref name="retained"/> holds the parent's values,
    /// indexed by parent basis ordinal through the face's capture-owned <paramref name="map"/>.</summary>
    internal TileWaterProjectionSource(ReadOnlySpan<int> map, ReadOnlySpan<TileWaterSharedProjections.Row> rows,
        ReadOnlySpan<S> retained, TileWaterSharedProjections? owner = null)
    {
        _map = map;
        _rows = rows;
        _retained = retained;
        _owner = owner;
    }

    internal bool IsEmpty => _map.IsEmpty;

    /// <summary>Copies the mapped parent projection by value. False leaves the state untouched for Project.</summary>
    internal bool TryCopy(int ordinal, ref S state)
    {
        if (_map.IsEmpty) return false;
        int parent = _map[ordinal];
        bool copied = parent >= 0 && (_rows.IsEmpty ? From(in _retained[parent], ref state) : From(in _rows[parent], ref state));
        _owner?.Count(copied);
        return copied;
    }

    static bool From(in TileWaterSharedProjections.Row row, ref S state)
    {
        if (!row.Resolved) return false;
        state.Q = row.Q;
        state.V = row.V;
        state.Lambda = row.Lambda;
        state.Velocity = row.Velocity;
        state.Projected = true;
        return true;
    }

    static bool From(in S saved, ref S state)
    {
        if ((saved.Mask & 1) == 0 || !saved.Projected) return false;
        state.Q = saved.Q;
        state.V = saved.V;
        state.Lambda = saved.Lambda;
        state.Velocity = saved.Velocity;
        state.Projected = true;
        return true;
    }
}
