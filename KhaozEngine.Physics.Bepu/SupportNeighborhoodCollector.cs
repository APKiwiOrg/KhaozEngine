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
/// known, and nothing reaches the caller's spans before the result is constructed. One instance is reused by its
/// owner world for query after query, under the world's query monitor, so a warm query allocates nothing.</summary>
internal sealed class SupportNeighborhoodCollector
{
    // Broadphase bounds are binary32 boxes from the backend. This margin keeps every static whose geometry may
    // reach the band in the candidate set, and the exact membership test then decides.
    const float BroadphaseMargin = 0.01f;

    readonly SupportSegmentPolygon _kernel = new();
    readonly SupportTangentKernel _tangents = new();
    readonly SupportPolygonPool _pool = new();
    readonly CapsuleFeatureCaptureScratch _capture = new();
    readonly List<SupportMember> _members = [];
    readonly List<CapsuleFeaturePolyhedron> _leaves = [];
    readonly List<SupportCurvedLeaf> _curved = [];
    readonly List<SupportPolygon> _polygons = [];
    readonly List<int> _triangles = [];
    readonly List<CollidableReference> _found = [];
    readonly List<(SeamStaticHandle Seam, BepuStaticHandle Installed)> _candidates = [];
    readonly HashSet<int> _seen = [];
    readonly double[] _low = new double[3], _high = new double[3];
    ulong[] _matrix = [];
    FeaturePoint _start, _end, _direction;
    FeatureNumber _radius;
    double _band;

    /// <summary>Starts a query for <paramref name="probe"/> at <paramref name="pose"/>, dropping everything the last
    /// query left behind.</summary>
    internal void Begin(CapsuleShape probe, Pose pose, float bandMetres)
    {
        _members.Clear();
        _pool.Reset();
        _capture.Reset();
        FeaturePoint center = FeaturePoint.Exact(pose.Position);
        FeatureNumber half = FeatureNumber.Exact(probe.Length).Multiply(FeatureNumber.Exact(0.5));
        _start = new FeaturePoint(center.X, center.Y.Subtract(half), center.Z);
        FeaturePoint end = new(center.X, center.Y.Add(half), center.Z);
        _end = end;
        _direction = FeaturePoint.Subtract(end, _start);
        _radius = FeatureNumber.Exact(probe.Radius);
        _band = bandMetres;
        for (int axis = 0; axis < 3; axis++)
        {
            _low[axis] = Math.Min(SupportGeometry.Component(_start, axis).Lower, SupportGeometry.Component(end, axis).Lower);
            _high[axis] = Math.Max(SupportGeometry.Component(_start, axis).Upper, SupportGeometry.Component(end, axis).Upper);
        }
        IsResolved = _start.Within(2048) && end.Within(2048) && _direction.IsResolved;
        _tangents.Reset(_start, end, _radius.Bounds, _band);
    }

    internal bool IsResolved { get; private set; }

    /// <summary>The selected statics whose broadphase bounds reach the probe inflated by the band, in seam handle
    /// order. Dynamic bodies and statics the view excludes are never candidates. The list is reused by the next
    /// query.</summary>
    internal List<(SeamStaticHandle Seam, BepuStaticHandle Installed)> Candidates(Simulation simulation,
        IReadOnlyDictionary<int, int> reverseHandles, StaticQueryExclusions? exclusions, CapsuleShape probe,
        Pose pose, float bandMetres)
    {
        // Every bound is summed outward in binary64 and rounded outward to binary32.
        GeometryInterval reach = GeometryInterval.Exact(probe.Radius).Add(GeometryInterval.Exact(bandMetres))
            .Add(GeometryInterval.Exact(BroadphaseMargin));
        GeometryInterval height = GeometryInterval.Exact(probe.Length * 0.5).Add(reach);
        _found.Clear();
        _candidates.Clear();
        _seen.Clear();
        var collector = new OverlapCollector(_found);
        simulation.BroadPhase.GetOverlaps(
            new Vector3(Down(pose.Position.X, reach), Down(pose.Position.Y, height), Down(pose.Position.Z, reach)),
            new Vector3(Up(pose.Position.X, reach), Up(pose.Position.Y, height), Up(pose.Position.Z, reach)),
            ref collector);
        foreach (CollidableReference collidable in _found)
        {
            if (collidable.Mobility != CollidableMobility.Static) continue;
            if (exclusions is not null && !exclusions.Allows(collidable)) continue;
            if (!reverseHandles.TryGetValue(collidable.StaticHandle.Value, out int seam) || !_seen.Add(seam)) continue;
            _candidates.Add((new SeamStaticHandle(seam), collidable.StaticHandle));
        }
        _candidates.Sort(static (a, b) => a.Seam.Value.CompareTo(b.Seam.Value));
        return _candidates;
    }

    static float Down(float centre, GeometryInterval extent) =>
        MathF.BitDecrement((float)GeometryInterval.Exact(centre).Subtract(GeometryInterval.Exact(extent.Upper)).Lower);

    static float Up(float centre, GeometryInterval extent) =>
        MathF.BitIncrement((float)GeometryInterval.Exact(centre).Add(GeometryInterval.Exact(extent.Upper)).Upper);

    /// <summary>Considers every element of one selected static: the faces of its box and hull leaves and the tangent
    /// elements of its sphere, capsule and cylinder leaves, or the triangles of its mesh near the probe. A capture
    /// refusal is the neighborhood's status, and failed bounded arithmetic in a kernel is Unsupported.</summary>
    internal CapsuleFeatureStatus Collect(Simulation simulation, SeamStaticHandle seam,
        in StaticDescription description)
    {
        _polygons.Clear();
        _curved.Clear();
        CapsuleFeatureStatus captured;
        if (SupportNeighborhoodMesh.IsMesh(description.Shape))
        {
            captured = SupportNeighborhoodMesh.Polygons(simulation, description.Shape, description.Pose, seam, _start,
                _end, _low, _high, _radius.Bounds.Add(GeometryInterval.Exact(_band)), _triangles, _pool, _polygons);
            if (captured != CapsuleFeatureStatus.Complete) return captured;
        }
        else
        {
            captured = SupportNeighborhoodPolyhedra.Capture(simulation, description.Shape, description.Pose, _capture,
                _leaves);
            if (captured != CapsuleFeatureStatus.Complete) return captured;
            foreach (CapsuleFeaturePolyhedron leaf in _leaves)
                SupportNeighborhoodPolyhedra.Polygons(seam, leaf, _pool, _polygons);
            captured = SupportNeighborhoodCurved.Capture(simulation, description.Shape, description.Pose, _curved);
            if (captured != CapsuleFeatureStatus.Complete) return captured;
        }
        foreach (SupportPolygon polygon in _polygons)
            if (Consider(polygon) == GeometrySign.Unresolved) return CapsuleFeatureStatus.Unsupported;
        foreach (SupportCurvedLeaf leaf in _curved)
        {
            for (int part = 0; part < leaf.Parts; part++)
            {
                GeometrySign member = _tangents.Measure(seam, leaf, part, out SupportElement element);
                if (member == GeometrySign.Unresolved) return CapsuleFeatureStatus.Unsupported;
                // Tangent elements are never joined, so they carry no polygon.
                if (member == GeometrySign.Positive) _members.Add(new(null, element));
            }
        }
        return CapsuleFeatureStatus.Complete;
    }

    /// <summary>Adds the polygon when it is a member. Unresolved means the bounded arithmetic failed.</summary>
    internal GeometrySign Consider(SupportPolygon polygon)
    {
        double reach = _radius.Bounds.Add(GeometryInterval.Exact(_band)).Upper;
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

    /// <summary>Orders the members, decides every join into the symmetric bit matrix and commits both spans
    /// together, or refuses atomically when the element span or the matrix for the member count does not fit.</summary>
    internal SupportNeighborhoodResult Publish(IPhysicsWorld receiver, IPhysicsQueryLease lease,
        Span<SupportElement> elements, Span<ulong> joins)
    {
        _members.Sort(static (a, b) => a.Element.Static.Value != b.Element.Static.Value
            ? a.Element.Static.Value.CompareTo(b.Element.Static.Value)
            : a.Element.ElementId.CompareTo(b.Element.ElementId));
        int count = _members.Count;
        int stride = SupportNeighborhoodResult.JoinWordsFor(count);
        if (count > SupportNeighborhoodResult.MaximumElements || elements.Length < count ||
            joins.Length < count * stride)
            return SupportNeighborhoodResult.Refused(CapsuleFeatureStatus.CapacityExceeded, count);
        if (_matrix.Length < count * stride) _matrix = new ulong[count * stride];
        Span<ulong> matrix = _matrix.AsSpan(0, count * stride);
        matrix.Clear();
        double apart = 2 * _band + Apart;
        for (int first = 0; first < count; first++)
        {
            SupportPolygon? a = _members[first].Polygon;
            if (a is null) continue;
            for (int second = first + 1; second < count; second++)
            {
                SupportPolygon? b = _members[second].Polygon;
                if (b is null || Separated(a, b, apart)) continue;
                GeometrySign joined = SupportNeighborhoodJoins.Joined(a, b, _band, _kernel);
                if (joined == GeometrySign.Unresolved)
                    return SupportNeighborhoodResult.Refused(CapsuleFeatureStatus.Unsupported);
                if (joined != GeometrySign.Positive) continue;
                matrix[first * stride + second / 64] |= 1UL << (second % 64);
                matrix[second * stride + first / 64] |= 1UL << (first % 64);
            }
        }
        // Construct first: lifecycle or structural failure must not expose a written prefix.
        SupportNeighborhoodResult result = SupportNeighborhoodResult.Completed(receiver, lease, count);
        for (int i = 0; i < count; i++) elements[i] = _members[i].Element;
        matrix.CopyTo(joins);
        return result;
    }

    // Lossless pruning of the join pass. Joined returns Negative at its first test whenever the BoxGap lower bound
    // of the two polygons' bounds exceeds the band, before any arithmetic that could refuse. When the raw gap on one
    // axis exceeds twice the band plus 2^-400, that bound does: each of BoxGap's outward steps (the difference, its
    // square, three sums and the square root) gives up at most one or two units in the last place, a relative loss
    // far below one half, and the margin keeps every intermediate value normal. A pair is skipped only then.
    // Certification never reads a pair whose member may face sideways or down, but those joins are part of the
    // published matrix, so they are still decided.
    static readonly double Apart = Math.ScaleB(1, -400);

    static bool Separated(SupportPolygon a, SupportPolygon b, double apart)
    {
        for (int axis = 0; axis < 3; axis++)
            if (b.Low[axis] - a.High[axis] > apart || a.Low[axis] - b.High[axis] > apart) return true;
        return false;
    }
}
