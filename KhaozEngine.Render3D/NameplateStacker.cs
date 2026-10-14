using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Collects nameplates by retained id and publishes panel rectangles using reusable frame storage.</summary>
/// <remarks>Call Begin, Add each natural panel, Resolve once, then query the resolved frame.</remarks>
public sealed class NameplateStacker
{
    /// <summary>Vertical separation between stacked panels, in the supplied rectangle's coordinate space.</summary>
    public const float Gap = 2f;

    /// <summary>Rank difference required to reverse retained plate order.</summary>
    public const float RankBand = 0.5f;

    /// <summary>Time constant in seconds for easing retained vertical offsets.</summary>
    public const float TimeConstant = 0.12f;

    private readonly float _topMargin;
    private readonly Dictionary<long, int> _entryById = new(16);
    private Entry[] _entries = new Entry[16];
    private int[] _order = new int[16];
    private long[] _drawOrder = new long[16];
    private int _entryCount;
    private int _drawCount;
    private bool _resolved;

    /// <summary>Creates a stacker whose storage is retained across frames.</summary>
    /// <param name="topMargin">Non-negative top margin in the same coordinate space as submitted rectangles.</param>
    /// <exception cref="ArgumentOutOfRangeException">The margin is negative or non-finite.</exception>
    public NameplateStacker(float topMargin = 4f)
    {
        _topMargin = topMargin;
        if (!float.IsFinite(_topMargin) || _topMargin < 0f)
            throw new ArgumentOutOfRangeException(nameof(topMargin));
    }

    /// <summary>Borrowed ids for the resolved frame, valid until the next Begin.</summary>
    public ReadOnlySpan<long> DrawOrder => _drawOrder.AsSpan(0, _drawCount);

    /// <summary>Invalidates public results and starts submissions while retaining private plate history.</summary>
    public void Begin()
    {
        _resolved = false;
        _drawCount = 0;
        for (int i = 0; i < _entryCount; i++)
            _entries[i].Seen = false;
    }

    /// <summary>Submits a natural panel. The last valid submission for an id replaces earlier submissions.</summary>
    /// <param name="id">Stable plate identity.</param>
    /// <param name="rect">Natural panel x, y, width and height. Invalid geometry is ignored.</param>
    /// <param name="rank">Finite distance rank, with larger values farther away.</param>
    /// <param name="pinned">Whether the panel must remain at its natural position.</param>
    public void Add(long id, Vector4 rect, float rank, bool pinned)
    {
        if (!IsValid(rect, rank))
            return;

        if (!_entryById.TryGetValue(id, out int index))
        {
            EnsureCapacity();
            index = _entryCount++;
            _entries[index] = new Entry { Id = id };
            _entryById.Add(id, index);
        }

        ref Entry entry = ref _entries[index];
        entry.NaturalRect = rect;
        entry.Rank = rank;
        entry.Pinned = pinned;
        entry.Seen = true;
    }

    /// <summary>Publishes natural rectangles for this frame and drops ids that were not submitted.</summary>
    /// <param name="dt">Elapsed frame time in seconds.</param>
    public void Resolve(float dt)
    {
        CompactEntries();
        for (int i = 0; i < _entryCount; i++)
        {
            ref Entry entry = ref _entries[_order[i]];
            entry.ResolvedRect = entry.NaturalRect;
            _drawOrder[i] = entry.Id;
        }

        _drawCount = _entryCount;
        _resolved = true;
    }

    /// <summary>Returns the resolved panel rectangle for an id in the current frame.</summary>
    /// <exception cref="KeyNotFoundException">The frame is unresolved or the id is absent.</exception>
    public Vector4 RectOf(long id)
    {
        if (!_resolved || !_entryById.TryGetValue(id, out int index))
            throw new KeyNotFoundException();

        return _entries[index].ResolvedRect;
    }

    private static bool IsValid(Vector4 rect, float rank) =>
        float.IsFinite(rect.X) && float.IsFinite(rect.Y) &&
        float.IsFinite(rect.Z) && float.IsFinite(rect.W) &&
        rect.Z > 0f && rect.W > 0f &&
        float.IsFinite(rect.X + rect.Z) && float.IsFinite(rect.Y + rect.W) &&
        float.IsFinite(rank);

    private void EnsureCapacity()
    {
        if (_entryCount < _entries.Length)
            return;

        int capacity = _entries.Length * 2;
        Array.Resize(ref _entries, capacity);
        Array.Resize(ref _order, capacity);
        Array.Resize(ref _drawOrder, capacity);
        _entryById.EnsureCapacity(capacity);
    }

    private void CompactEntries()
    {
        int count = 0;
        for (int i = 0; i < _entryCount; i++)
        {
            Entry entry = _entries[i];
            if (!entry.Seen)
            {
                _entryById.Remove(entry.Id);
                continue;
            }

            _entries[count] = entry;
            _entryById[entry.Id] = count;
            _order[count] = count;
            count++;
        }

        Array.Clear(_entries, count, _entryCount - count);
        _entryCount = count;
    }

    private struct Entry
    {
        public long Id;
        public Vector4 NaturalRect;
        public Vector4 ResolvedRect;
        public float Rank;
        public bool Pinned;
        public bool Seen;
    }
}
