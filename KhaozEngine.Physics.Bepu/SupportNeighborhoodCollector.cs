using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using KhaozEngine.Physics;

using BepuStaticHandle = BepuPhysics.StaticHandle;
using SeamStaticHandle = KhaozEngine.Physics.StaticHandle;

namespace KhaozEngine.Physics.Bepu;

/// <summary>One published element and, for a polygon, the geometry its joins are decided on.</summary>
internal readonly record struct SupportMember(SupportPolygon? Polygon, SupportElement Element);

/// <summary>Collects the support neighborhood of one upright probe. Membership is decided by the lower bound of
/// each element's separation interval, so an element is included whenever it may lie within the band. Elements
/// are ordered by static handle then element id. Capacity is checked atomically after every member and join is
/// known, and nothing reaches the caller's spans before the result is constructed.</summary>
internal sealed class SupportNeighborhoodCollector
{
    // Broadphase bounds are binary32 boxes from the backend. This margin keeps every static whose geometry may
    // reach the band in the candidate set, and the exact membership test then decides.
    const float BroadphaseMargin = 0.01f;

    readonly SupportSegmentPolygon _kernel = new();
    readonly List<SupportMember> _members = [];
    readonly FeaturePoint _start, _direction;
    readonly FeatureNumber _radius;
    readonly double _band;
    readonly double[] _low = new double[3], _high = new double[3];

    internal SupportNeighborhoodCollector(CapsuleShape probe, Pose pose, float bandMetres)
    {
        FeaturePoint center = FeaturePoint.Exact(pose.Position);
        FeatureNumber half = FeatureNumber.Exact(probe.Length).Multiply(FeatureNumber.Exact(0.5));
        _start = new FeaturePoint(center.X, center.Y.Subtract(half), center.Z);
        FeaturePoint end = new(center.X, center.Y.Add(half), center.Z);
        _direction = FeaturePoint.Subtract(end, _start);
        _radius = FeatureNumber.Exact(probe.Radius);
        _band = bandMetres;
        for (int axis = 0; axis < 3; axis++)
        {
            _low[axis] = Math.Min(SupportGeometry.Component(_start, axis).Lower, SupportGeometry.Component(end, axis).Lower);
            _high[axis] = Math.Max(SupportGeometry.Component(_start, axis).Upper, SupportGeometry.Component(end, axis).Upper);
        }
        IsResolved = _start.Within(2048) && end.Within(2048) && _direction.IsResolved;
    }

    internal bool IsResolved { get; }

    /// <summary>The selected statics whose broadphase bounds reach the probe inflated by the band, in seam handle
    /// order. Dynamic bodies and statics the view excludes are never candidates.</summary>
    internal static List<(SeamStaticHandle Seam, BepuStaticHandle Installed)> Candidates(Simulation simulation,
        IReadOnlyDictionary<int, int> reverseHandles, StaticQueryExclusions? exclusions, CapsuleShape probe,
        Pose pose, float bandMetres)
    {
        float reach = probe.Radius + bandMetres + BroadphaseMargin;
        var extent = new Vector3(reach, probe.Length * 0.5f + reach, reach);
        var found = new List<CollidableReference>();
        var collector = new OverlapCollector(found);
        simulation.BroadPhase.GetOverlaps(pose.Position - extent, pose.Position + extent, ref collector);
        var candidates = new List<(SeamStaticHandle Seam, BepuStaticHandle Installed)>();
        var seen = new HashSet<int>();
        foreach (CollidableReference collidable in found)
        {
            if (collidable.Mobility != CollidableMobility.Static) continue;
            if (exclusions is not null && !exclusions.Allows(collidable)) continue;
            if (!reverseHandles.TryGetValue(collidable.StaticHandle.Value, out int seam) || !seen.Add(seam)) continue;
            candidates.Add((new SeamStaticHandle(seam), collidable.StaticHandle));
        }
        candidates.Sort((a, b) => a.Seam.Value.CompareTo(b.Seam.Value));
        return candidates;
    }

    /// <summary>Adds the polygon when it is a member. Unresolved means the bounded arithmetic failed.</summary>
    internal GeometrySign Consider(SupportPolygon polygon)
    {
        double reach = _radius.Bounds.Upper + _band;
        if (SupportGeometry.BoxGap(_low, _high, polygon.Low, polygon.High) > reach) return GeometrySign.Negative;
        SupportDistance distance = _kernel.Measure(_start, _direction, polygon, _band);
        if (!distance.IsResolved) return GeometrySign.Unresolved;
        GeometryInterval separation = GeometryInterval.Enclose(distance.Lower, distance.Upper)
            .Subtract(_radius.Bounds);
        if (!separation.IsResolved) return GeometrySign.Unresolved;
        if (separation.Lower > _band || !distance.Front) return GeometrySign.Negative;
        GeometryVectorOutput normal = GeometryVectorOperations.Publish(
            GeometryVectorOperations.Normalize(polygon.Normal.Bounds));
        GeometryVectorOutput witness = GeometryVectorOperations.Publish(distance.Witness);
        if (!normal.IsResolved || !witness.IsResolved) return GeometrySign.Unresolved;
        _members.Add(new(polygon, new SupportElement(polygon.Static, SupportElementKind.Polygon, polygon.ElementId,
            normal.Value, normal.Error, witness.Value, witness.Error, separation.Lower, separation.Upper)));
        return GeometrySign.Positive;
    }

    /// <summary>Orders the members, decides every join and commits both spans together, or refuses atomically.</summary>
    internal SupportNeighborhoodResult Publish(IPhysicsWorld receiver, IPhysicsQueryLease lease,
        Span<SupportElement> elements, Span<SupportJoin> joins)
    {
        _members.Sort((a, b) => a.Element.Static.Value != b.Element.Static.Value
            ? a.Element.Static.Value.CompareTo(b.Element.Static.Value)
            : a.Element.ElementId.CompareTo(b.Element.ElementId));
        int count = _members.Count;
        if (count > SupportNeighborhoodResult.MaximumElements)
            return SupportNeighborhoodResult.Refused(CapsuleFeatureStatus.CapacityExceeded, count);
        var pairs = new List<SupportJoin>();
        for (int first = 0; first < count; first++)
        {
            SupportPolygon? a = _members[first].Polygon;
            if (a is null) continue;
            for (int second = first + 1; second < count; second++)
            {
                SupportPolygon? b = _members[second].Polygon;
                if (b is null) continue;
                GeometrySign joined = SupportNeighborhoodJoins.Joined(a, b, _band, _kernel);
                if (joined == GeometrySign.Unresolved)
                    return SupportNeighborhoodResult.Refused(CapsuleFeatureStatus.Unsupported);
                if (joined == GeometrySign.Positive) pairs.Add(new(first, second));
            }
        }
        if (pairs.Count > SupportNeighborhoodResult.MaximumJoins || elements.Length < count || joins.Length < pairs.Count)
            return SupportNeighborhoodResult.Refused(CapsuleFeatureStatus.CapacityExceeded, count, pairs.Count);
        // Construct first: lifecycle or structural failure must not expose a written prefix.
        SupportNeighborhoodResult result = SupportNeighborhoodResult.Completed(receiver, lease, count, pairs.Count);
        for (int i = 0; i < count; i++) elements[i] = _members[i].Element;
        for (int i = 0; i < pairs.Count; i++) joins[i] = pairs[i];
        return result;
    }
}
