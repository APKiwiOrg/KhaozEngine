using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>One raw support member: a static and its released element id, a polyhedron face, a mesh triangle or a
/// curved part.</summary>
internal readonly record struct SupportMemberKey(StaticHandle Static, int FeatureId);

/// <summary>The complete footprint a physics support sample was certified from and the exact query that asked for it.
/// The result carries its receiver, source, lease, origin and geometry generation, so its issuer can authenticate it
/// again. Equality on this value is not geometric and is never used by the sample's own equality.</summary>
internal readonly record struct SupportContinuitySource(SupportFootprintResult Result, FootSupportQuery Query);

/// <summary>Inline key storage for one selected member's direct joins, one slot per raw member the footprint can
/// publish.</summary>
[InlineArray(SupportFootprintResult.MaximumElements)]
internal struct SupportJoinedMemberKeys
{
    SupportMemberKey _element;
}

/// <summary>The selected raw member and the walkable polygons its complete footprint directly joins to it, in raw
/// member order and excluding itself. A value copy of the certified join row, never a reference into scratch, a row
/// index, a component or a path. <see cref="Count"/> is 0 to 255.</summary>
internal readonly struct SupportJoinedMembers
{
    readonly SupportJoinedMemberKeys _keys;

    SupportJoinedMembers(SupportMemberKey selected, in SupportJoinedMemberKeys keys, int count)
    {
        Selected = selected;
        _keys = keys;
        Count = count;
    }

    /// <summary>The raw member the sample selected.</summary>
    internal SupportMemberKey Selected { get; }

    /// <summary>The number of directly joined walkable members.</summary>
    internal int Count { get; }

    /// <summary>The joined member at <paramref name="index"/>, in raw member order.</summary>
    internal SupportMemberKey this[int index] =>
        (uint)index < (uint)Count ? _keys[index] : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>Whether the selected member's certified row directly joins <paramref name="key"/>.</summary>
    internal bool Contains(SupportMemberKey key)
    {
        for (int i = 0; i < Count; i++)
            if (_keys[i] == key) return true;
        return false;
    }

    internal static SupportJoinedMembers Create(SupportMemberKey selected, in SupportJoinedMemberKeys keys, int count) =>
        new(selected, keys, count);
}

/// <summary>Whether two walkable support samples lie on one continuous height field (CF-4, CF-5). The same raw member
/// always does. Two different members do only when an authenticated endpoint's complete footprint directly joins the
/// selected pair.</summary>
internal static class SupportContinuity
{
    /// <summary>Copies the selected raw member's direct joined walkable polygons, excluding itself, from a complete
    /// footprint whose spans are still current. Context-only polygons count, since they may support at the next
    /// axis. A curved or non-walkable selection keeps its key with no joins.</summary>
    internal static SupportJoinedMembers Capture(in SupportFootprintResult result, in FootSupportQuery query,
        ReadOnlySpan<SupportFootprintMember> members, ReadOnlySpan<ulong> joins, int selectedMember)
    {
        if (result.Status != CapsuleFeatureStatus.Complete)
            throw new ArgumentException("Only a complete footprint certifies joins.", nameof(result));
        int count = result.Members, stride = result.JoinWordsPerRow;
        if ((uint)selectedMember >= (uint)count)
            throw new ArgumentOutOfRangeException(nameof(selectedMember));
        if (members.Length < count || joins.Length < count * stride)
            throw new ArgumentException("The spans must hold every published member and join row.");
        SupportFootprintMember selected = members[selectedMember];
        SupportJoinedMemberKeys keys = default;
        int written = 0;
        if (Walkable(selected, query.CosMaxSlope))
        {
            ReadOnlySpan<ulong> row = joins.Slice(selectedMember * stride, stride);
            for (int j = 0; j < count; j++)
            {
                if (j == selectedMember || (row[j / 64] & (1UL << (j % 64))) == 0 ||
                    !Walkable(members[j], query.CosMaxSlope)) continue;
                keys[written++] = new(members[j].Static, members[j].ElementId);
            }
        }
        return SupportJoinedMembers.Create(new(selected.Static, selected.ElementId), keys, written);
    }

    /// <summary>True on one raw member, as before. Across members, true only when both samples are walkable physics
    /// support whose sources authenticate through their issuer as one receiver, source, lease, origin and generation,
    /// whose exact queries were asked at <paramref name="startAxis"/> and <paramref name="axis"/> with one foot
    /// radius and the traction gate <paramref name="cosMaxSlope"/>, and whose selected pair either endpoint's copied
    /// row directly joins. A disposed lease throws <see cref="ObjectDisposedException"/> and another receiver or
    /// interval throws <see cref="InvalidOperationException"/>, as the backend does.</summary>
    internal static bool IsContinuous(in SupportSample start, Vector2 startAxis, in SupportSample next, Vector2 axis,
        float cosMaxSlope)
    {
        if (SameMember(start, next)) return true;
        if (start.Status != SupportStatus.Walkable || next.Status != SupportStatus.Walkable ||
            start.Static is not StaticHandle startStatic || next.Static is not StaticHandle nextStatic)
            return false;
        SupportFootprintResult first = start.Source.Result, second = next.Source.Result;
        if (first.Status != CapsuleFeatureStatus.Complete || second.Status != CapsuleFeatureStatus.Complete)
            return false;
        if (first.QueryWorld is not IPhysicsSupportFootprint issuer || first.Lease is not IPhysicsQueryLease lease)
            throw new InvalidOperationException("The support evidence has no issuer.");
        // The issuer authenticates its own result, then proves the other endpoint belongs to the same receiver,
        // lease, source, origin and generation.
        issuer.AssertFootprintCurrent(first, lease);
        issuer.AssertFootprintCurrent(second, lease);
        FootSupportQuery from = start.Source.Query, to = next.Source.Query;
        if (from.Axis != startAxis || to.Axis != axis || from.FootRadius != to.FootRadius ||
            from.CosMaxSlope != cosMaxSlope || to.CosMaxSlope != cosMaxSlope)
            return false;
        var startKey = new SupportMemberKey(startStatic, start.FeatureId);
        var nextKey = new SupportMemberKey(nextStatic, next.FeatureId);
        if (start.Joined.Selected != startKey || next.Joined.Selected != nextKey) return false;
        return start.Joined.Contains(nextKey) || next.Joined.Contains(startKey);
    }

    // One raw member is one convex polygon or curved part of one static: a polyhedron face, a mesh triangle or a
    // curved leaf part, named by its element id. Analytic terrain has no static and is never one member.
    static bool SameMember(in SupportSample start, in SupportSample next) =>
        start.Static is StaticHandle owner && next.Static == owner && start.FeatureId == next.FeatureId;

    // Certification classifies a polygon walkable from its certified upward lower bound at the query's gate.
    static bool Walkable(in SupportFootprintMember member, float cosMaxSlope) =>
        member.Kind == SupportElementKind.Polygon && member.UpwardLower > 0 && member.UpwardLower >= cosMaxSlope;
}
