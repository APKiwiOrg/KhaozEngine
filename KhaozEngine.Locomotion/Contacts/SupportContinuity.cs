using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>One raw support member: a static and its released element id, a polyhedron face, a mesh triangle or a
/// curved part.</summary>
internal readonly record struct SupportMemberKey(StaticHandle Static, int FeatureId);

/// <summary>The complete footprint a physics support sample was certified from and the exact query that asked for it.
/// The result carries its receiver, source, lease, origin and geometry generation, so its issuer can authenticate it
/// again. Equality on this value is not geometric and is never used by the sample's own equality.</summary>
internal readonly record struct SupportContinuitySource(SupportFootprintResult Result, FootSupportQuery Query);

/// <summary>A sample's reference to its row in the read interval's <see cref="SupportContinuityArena"/>: the arena,
/// the row's slot and the arena epoch it was written in, 16 bytes. Default carries no evidence. A handle is only
/// meaningful while its arena is open in the same epoch, which <see cref="SupportContinuityArena.Resolve"/>
/// checks before any row is read.</summary>
internal readonly struct SupportContinuityHandle
{
    internal const int SlotBits = 20;
    const long SlotMask = (1L << SlotBits) - 1;
    readonly long _token;

    internal SupportContinuityHandle(SupportContinuityArena arena, long epoch, int slot)
    {
        Arena = arena;
        _token = (epoch << SlotBits) | (long)(uint)slot;
    }

    internal SupportContinuityArena? Arena { get; }
    internal long Epoch => _token >> SlotBits;
    internal int Slot => (int)(_token & SlotMask);
    internal bool IsEmpty => Arena is null;
}

/// <summary>Interval-owned storage for the selected member's direct joined walkable row (CF-4). The support memo owns
/// one arena and opens and closes it with its read interval. Rows are appended, never changed, until the interval
/// closes, when every row and its references are cleared and the epoch advances, so no earlier handle resolves again.
/// Fixed storage holds 64 rows and 1,024 keys, preserving the original provision (FA-R10). A row keeps every direct
/// neighbour, up to the raw bound of 255, or gets no handle if the complete row cannot fit. Find never grows it.</summary>
internal sealed class SupportContinuityArena
{
    readonly Row[] _rows = new Row[64];
    readonly SupportMemberKey[] _keys = new SupportMemberKey[4 * SupportFootprintResult.MaximumElements];
    int _rowCount, _keyCount;
    long _epoch;

    struct Row
    {
        internal SupportContinuitySource Source;
        internal SupportMemberKey Selected;
        internal int Start, Count;
    }

    /// <summary>Whether the arena belongs to an open read interval.</summary>
    internal bool IsOpen { get; private set; }

    /// <summary>Rows written in the current interval.</summary>
    internal int Count => _rowCount;

    internal void Open()
    {
        if (IsOpen) throw new InvalidOperationException("The continuity arena is already open.");
        checked { _epoch++; }
        IsOpen = true;
    }

    internal void Close()
    {
        Array.Clear(_rows, 0, _rowCount);
        (_rowCount, _keyCount) = (0, 0);
        checked { _epoch++; }
        IsOpen = false;
    }

    /// <summary>Writes one complete row and returns its handle, or an empty handle when either fixed retention budget
    /// would be exceeded, so the sample keeps the unjoined rule. Existing rows remain immutable.</summary>
    internal SupportContinuityHandle Write(in SupportFootprintResult result, in FootSupportQuery query,
        ReadOnlySpan<SupportFootprintMember> members, ReadOnlySpan<ulong> joins, int selectedMember)
    {
        if (!IsOpen) throw new InvalidOperationException("The continuity arena is closed.");
        if (_rowCount == _rows.Length) return default;
        SupportFootprintMember selected = members[selectedMember];
        int start = _keyCount, end = start;
        if (Walkable(selected, query.CosMaxSlope))
        {
            int stride = result.JoinWordsPerRow;
            ReadOnlySpan<ulong> row = joins.Slice(selectedMember * stride, stride);
            for (int j = 0; j < result.Members; j++)
            {
                if (j == selectedMember || (row[j / 64] & (1UL << (j % 64))) == 0 ||
                    !Walkable(members[j], query.CosMaxSlope)) continue;
                // Only the unused suffix is touched. Publish its end after the whole row fits, so an overflow
                // retains neither a prefix nor a slot and cannot invalidate any earlier handle.
                if (end == _keys.Length) return default;
                _keys[end++] = new(members[j].Static, members[j].ElementId);
            }
        }
        _rows[_rowCount] = new Row
        {
            Source = new(result, query),
            Selected = new(selected.Static, selected.ElementId),
            Start = start,
            Count = end - start,
        };
        _keyCount = end;
        return new(this, _epoch, _rowCount++);
    }

    /// <summary>Authenticates <paramref name="handle"/> as this arena's, in its current open epoch and naming a written
    /// slot, then returns the row's source. A stale handle throws <see cref="InvalidOperationException"/>.</summary>
    internal SupportContinuitySource Resolve(in SupportContinuityHandle handle) => RowOf(handle).Source;

    /// <summary>The authenticated row's selected member and direct joined members.</summary>
    internal SupportJoinedMembers Joined(in SupportContinuityHandle handle)
    {
        ref readonly Row row = ref RowOf(handle);
        return new(_keys, row.Selected, row.Start, row.Count);
    }

    ref readonly Row RowOf(in SupportContinuityHandle handle)
    {
        if (!ReferenceEquals(handle.Arena, this) || !IsOpen || handle.Epoch != _epoch || handle.Slot >= _rowCount)
            throw new InvalidOperationException("The continuity evidence belongs to another or a closed read interval.");
        return ref _rows[handle.Slot];
    }

    // Certification classifies a polygon walkable from its certified upward lower bound at the query's gate.
    static bool Walkable(in SupportFootprintMember member, float cosMaxSlope) =>
        member.Kind == SupportElementKind.Polygon && member.UpwardLower > 0 && member.UpwardLower >= cosMaxSlope;
}

/// <summary>A read-only view of one authenticated arena row: the selected raw member and the walkable polygons its
/// complete footprint directly joins to it, in raw member order and excluding itself. <see cref="Count"/> is 0 to
/// 255. The view is valid only while its interval is open.</summary>
internal readonly struct SupportJoinedMembers
{
    readonly SupportMemberKey[]? _keys;
    readonly int _start;

    internal SupportJoinedMembers(SupportMemberKey[] keys, SupportMemberKey selected, int start, int count)
    {
        (_keys, Selected, _start, Count) = (keys, selected, start, count);
    }

    /// <summary>The raw member the sample selected.</summary>
    internal SupportMemberKey Selected { get; }

    /// <summary>The number of directly joined walkable members.</summary>
    internal int Count { get; }

    /// <summary>The joined member at <paramref name="index"/>, in raw member order.</summary>
    internal SupportMemberKey this[int index] =>
        (uint)index < (uint)Count ? _keys![_start + index] : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>Whether the selected member's certified row directly joins <paramref name="key"/>.</summary>
    internal bool Contains(SupportMemberKey key)
    {
        for (int i = 0; i < Count; i++)
            if (_keys![_start + i] == key) return true;
        return false;
    }
}

/// <summary>Whether two walkable support samples lie on one continuous height field (CF-4, CF-5). The same raw member
/// always does. Two different members do only when an authenticated endpoint's complete footprint directly joins the
/// selected pair.</summary>
internal static class SupportContinuity
{
    /// <summary>Writes the selected raw member's direct joined walkable polygons, excluding itself, from a complete
    /// footprint whose spans are still current, into <paramref name="arena"/>. Context-only polygons count, since
    /// they may support at the next axis. A curved or non-walkable selection keeps its key with no joins.</summary>
    internal static SupportContinuityHandle Capture(SupportContinuityArena arena, in SupportFootprintResult result,
        in FootSupportQuery query, ReadOnlySpan<SupportFootprintMember> members, ReadOnlySpan<ulong> joins,
        int selectedMember)
    {
        ArgumentNullException.ThrowIfNull(arena);
        if (result.Status != CapsuleFeatureStatus.Complete)
            throw new ArgumentException("Only a complete footprint certifies joins.", nameof(result));
        int count = result.Members, stride = result.JoinWordsPerRow;
        if ((uint)selectedMember >= (uint)count)
            throw new ArgumentOutOfRangeException(nameof(selectedMember));
        if (members.Length < count || joins.Length < count * stride)
            throw new ArgumentException("The spans must hold every published member and join row.");
        return arena.Write(result, query, members, joins, selectedMember);
    }

    /// <summary>True on one raw member, as before. Across members, true only when both samples are walkable physics
    /// support whose handles name rows of one open interval arena, whose sources authenticate through their issuer as
    /// one receiver, source, lease, origin and generation, whose exact queries were asked at
    /// <paramref name="startAxis"/> and <paramref name="axis"/> with one foot radius and the traction gate
    /// <paramref name="cosMaxSlope"/>, and whose selected pair either endpoint's row directly joins. A stale handle or
    /// another receiver or interval throws <see cref="InvalidOperationException"/> and a disposed lease throws
    /// <see cref="ObjectDisposedException"/>, as the backend does.</summary>
    internal static bool IsContinuous(in SupportSample start, Vector2 startAxis, in SupportSample next, Vector2 axis,
        float cosMaxSlope)
    {
        if (SameMember(start, next)) return true;
        if (start.Status != SupportStatus.Walkable || next.Status != SupportStatus.Walkable ||
            start.Static is not StaticHandle startStatic || next.Static is not StaticHandle nextStatic)
            return false;
        SupportContinuityHandle first = start.Continuity, second = next.Continuity;
        if (first.Arena is not SupportContinuityArena arena || second.IsEmpty) return false;
        if (!ReferenceEquals(second.Arena, arena))
            throw new InvalidOperationException("The continuity evidence belongs to another read interval.");
        // Both handles authenticate before any row is read.
        SupportContinuitySource from = arena.Resolve(first), to = arena.Resolve(second);
        if (from.Result.QueryWorld is not IPhysicsSupportFootprint issuer ||
            from.Result.Lease is not IPhysicsQueryLease lease)
            throw new InvalidOperationException("The support evidence has no issuer.");
        // The issuer authenticates its own result, then proves the other endpoint belongs to the same receiver,
        // lease, source, origin and generation.
        issuer.AssertFootprintCurrent(from.Result, lease);
        issuer.AssertFootprintCurrent(to.Result, lease);
        if (from.Query.Axis != startAxis || to.Query.Axis != axis || from.Query.FootRadius != to.Query.FootRadius ||
            from.Query.CosMaxSlope != cosMaxSlope || to.Query.CosMaxSlope != cosMaxSlope)
            return false;
        var startKey = new SupportMemberKey(startStatic, start.FeatureId);
        var nextKey = new SupportMemberKey(nextStatic, next.FeatureId);
        SupportJoinedMembers startRow = arena.Joined(first), nextRow = arena.Joined(second);
        if (startRow.Selected != startKey || nextRow.Selected != nextKey) return false;
        return startRow.Contains(nextKey) || nextRow.Contains(startKey);
    }

    // One raw member is one convex polygon or curved part of one static: a polyhedron face, a mesh triangle or a
    // curved leaf part, named by its element id. Analytic terrain has no static and is never one member.
    static bool SameMember(in SupportSample start, in SupportSample next) =>
        start.Static is StaticHandle owner && next.Static == owner && start.FeatureId == next.FeatureId;
}
