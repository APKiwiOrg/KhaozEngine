using System;
using System.Collections.Generic;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

internal sealed record CapsuleFeaturePolyhedronCandidate(CapsuleFeaturePolyhedron Leaf, int FeatureId,
    CapsuleFeatureKind Kind, FeaturePoint Axis, FeaturePoint Geometry, FeatureNumber SquaredDistance,
    int[] IncidentFaces);

/// <summary>Complete finite boundary enumeration for a segment and an admitted convex solid.
/// Interior face, interior edge and vertex candidates are disjoint strata. Endpoints and stationary
/// segment/edge pairs cover their constrained minima. Parallel continua retain competing witnesses.</summary>
internal sealed class CapsuleFeaturePolyhedraClosest
{
    internal const int MaximumCandidates = 128;
    internal List<CapsuleFeaturePolyhedronCandidate> Candidates { get; } = [];
    readonly FeaturePoint _lower, _upper, _direction;
    readonly FeatureNumber _bandRadius, _axisSquared, _bandSquared;
    CapsuleFeatureStatus _status = CapsuleFeatureStatus.Complete;

    internal CapsuleFeaturePolyhedraClosest(FeaturePoint lower, FeaturePoint upper, FeatureNumber bandRadius)
    {
        _lower = lower;
        _upper = upper;
        _direction = FeaturePoint.Subtract(upper, lower);
        _axisSquared = FeaturePoint.Dot(_direction, _direction);
        _bandRadius = bandRadius;
        _bandSquared = bandRadius.Multiply(bandRadius);
    }

    internal CapsuleFeatureStatus Enumerate(CapsuleFeaturePolyhedron leaf)
    {
        GeometrySign lowInside = leaf.Contains(_lower), highInside = leaf.Contains(_upper);
        if (lowInside != GeometrySign.Negative || highInside != GeometrySign.Negative)
            return CapsuleFeatureStatus.Unresolved;
        for (int i = 0; i < leaf.Vertices.Length && _status == CapsuleFeatureStatus.Complete; i++)
            Vertex(leaf, i);
        for (int i = 0; i < leaf.Edges.Length && _status == CapsuleFeatureStatus.Complete; i++)
            Edge(leaf, i);
        for (int i = 0; i < leaf.Faces.Length && _status == CapsuleFeatureStatus.Complete; i++)
            Face(leaf, i);
        return _status;
    }

    void Vertex(CapsuleFeaturePolyhedron leaf, int vertex)
    {
        FeaturePoint geometry = leaf.Vertices[vertex];
        if (Far([geometry])) return;
        FeaturePoint axis = _lower;
        if (_axisSquared.Sign != GeometrySign.Zero)
        {
            FeatureNumber t = FeaturePoint.Dot(FeaturePoint.Subtract(geometry, _lower), _direction).Divide(_axisSquared);
            if (!Clamp(t, out FeatureNumber clamped)) return;
            axis = FeaturePoint.Add(_lower, FeaturePoint.Scale(_direction, clamped));
        }
        Admit(leaf, leaf.Faces.Length + leaf.Edges.Length + vertex, CapsuleFeatureKind.Vertex,
            axis, geometry, leaf.VertexFaces[vertex]);
    }

    void Edge(CapsuleFeaturePolyhedron leaf, int id)
    {
        CapsuleFeaturePolyhedronEdge edge = leaf.Edges[id];
        FeaturePoint first = leaf.Vertices[edge.A], second = leaf.Vertices[edge.B];
        if (Far([first, second])) return;
        FeaturePoint a = first, e = FeaturePoint.Subtract(second, a);
        FeatureNumber c = FeaturePoint.Dot(e, e);
        if (c.Sign != GeometrySign.Positive) { _status = CapsuleFeatureStatus.Unresolved; return; }
        EdgeEndpoint(leaf, id, a, e, c, _lower);
        if (!_lower.SameExact(_upper)) EdgeEndpoint(leaf, id, a, e, c, _upper);
        if (_status != CapsuleFeatureStatus.Complete || _axisSquared.Sign == GeometrySign.Zero) return;
        FeaturePoint r = FeaturePoint.Subtract(_lower, a);
        FeatureNumber b = FeaturePoint.Dot(_direction, e), d = FeaturePoint.Dot(_direction, r), f = FeaturePoint.Dot(e, r);
        FeatureNumber determinant = _axisSquared.Multiply(c).Subtract(b.Multiply(b));
        if (determinant.Sign == GeometrySign.Zero)
        {
            // A parallel minimizer reaches a parameter boundary. The endpoint/vertex candidates
            // already cover it, including two distinct witnesses when the minimum is a continuum.
            return;
        }
        if (determinant.Sign != GeometrySign.Positive) { _status = CapsuleFeatureStatus.Unresolved; return; }
        FeatureNumber t = b.Multiply(f).Subtract(c.Multiply(d)).Divide(determinant);
        FeatureNumber u = _axisSquared.Multiply(f).Subtract(b.Multiply(d)).Divide(determinant);
        if (!Interior(t) || !Interior(u)) return;
        AdmitEdge(leaf, id, FeaturePoint.Add(_lower, FeaturePoint.Scale(_direction, t)),
            FeaturePoint.Add(a, FeaturePoint.Scale(e, u)));
    }

    void EdgeEndpoint(CapsuleFeaturePolyhedron leaf, int id, FeaturePoint a, FeaturePoint e,
        FeatureNumber squared, FeaturePoint axis)
    {
        FeatureNumber u = FeaturePoint.Dot(FeaturePoint.Subtract(axis, a), e).Divide(squared);
        if (Interior(u)) AdmitEdge(leaf, id, axis, FeaturePoint.Add(a, FeaturePoint.Scale(e, u)));
    }

    void AdmitEdge(CapsuleFeaturePolyhedron leaf, int id, FeaturePoint axis, FeaturePoint geometry)
    {
        CapsuleFeaturePolyhedronEdge edge = leaf.Edges[id];
        Admit(leaf, leaf.Faces.Length + id, CapsuleFeatureKind.ConvexCrease, axis, geometry,
            [edge.FirstFace, edge.SecondFace]);
    }

    void Face(CapsuleFeaturePolyhedron leaf, int id)
    {
        int[] indices = leaf.Faces[id];
        var points = new FeaturePoint[indices.Length];
        for (int i = 0; i < points.Length; i++) points[i] = leaf.Vertices[indices[i]];
        if (Far(points)) return;
        FeaturePoint origin = points[0], n = leaf.Normals[id];
        FeatureNumber squared = FeaturePoint.Dot(n, n);
        if (squared.Sign != GeometrySign.Positive) { _status = CapsuleFeatureStatus.Unresolved; return; }
        FaceEndpoint(leaf, id, origin, n, squared, _lower);
        if (!_lower.SameExact(_upper)) FaceEndpoint(leaf, id, origin, n, squared, _upper);
        if (_status != CapsuleFeatureStatus.Complete || _axisSquared.Sign == GeometrySign.Zero) return;
        FeatureNumber derivative = FeaturePoint.Dot(n, _direction);
        if (derivative.Sign == GeometrySign.Zero) return;
        if (derivative.Sign == GeometrySign.Unresolved) { _status = CapsuleFeatureStatus.Unresolved; return; }
        FeatureNumber t = FeaturePoint.Dot(n, FeaturePoint.Subtract(origin, _lower)).Divide(derivative);
        if (!Interior(t)) return;
        FeaturePoint intersection = FeaturePoint.Add(_lower, FeaturePoint.Scale(_direction, t));
        GeometrySign membership = leaf.FaceInterior(id, intersection);
        if (membership == GeometrySign.Unresolved || membership == GeometrySign.Positive)
            _status = CapsuleFeatureStatus.Unresolved;
        // A boundary intersection belongs to the separately enumerated exact edge/vertex stratum.
    }

    void FaceEndpoint(CapsuleFeaturePolyhedron leaf, int id, FeaturePoint origin, FeaturePoint normal,
        FeatureNumber squared, FeaturePoint axis)
    {
        FeatureNumber scale = FeaturePoint.Dot(FeaturePoint.Subtract(axis, origin), normal).Divide(squared);
        FeaturePoint geometry = FeaturePoint.Subtract(axis, FeaturePoint.Scale(normal, scale));
        GeometrySign membership = leaf.FaceInterior(id, geometry);
        if (membership == GeometrySign.Unresolved) { _status = CapsuleFeatureStatus.Unresolved; return; }
        if (membership == GeometrySign.Positive)
            Admit(leaf, id, CapsuleFeatureKind.FaceInterior, axis, geometry, [id]);
    }

    bool Clamp(FeatureNumber parameter, out FeatureNumber result)
    {
        result = default;
        GeometrySign low = parameter.Sign, high = parameter.Compare(FeatureNumber.Exact(1));
        if (low == GeometrySign.Unresolved || high == GeometrySign.Unresolved)
        { _status = CapsuleFeatureStatus.Unresolved; return false; }
        result = low is GeometrySign.Negative or GeometrySign.Zero ? FeatureNumber.Exact(0)
            : high is GeometrySign.Positive or GeometrySign.Zero ? FeatureNumber.Exact(1) : parameter;
        return true;
    }

    bool Interior(FeatureNumber parameter)
    {
        GeometrySign low = parameter.Sign, high = parameter.Compare(FeatureNumber.Exact(1));
        // A decided excluded side suffices, even when the other comparison cannot be decided.
        if (low is GeometrySign.Negative or GeometrySign.Zero || high is GeometrySign.Positive or GeometrySign.Zero)
            return false;
        if (low != GeometrySign.Positive || high != GeometrySign.Negative)
        { _status = CapsuleFeatureStatus.Unresolved; return false; }
        return true;
    }

    void Admit(CapsuleFeaturePolyhedron leaf, int feature, CapsuleFeatureKind kind, FeaturePoint axis,
        FeaturePoint geometry, int[] faces)
    {
        if (_status != CapsuleFeatureStatus.Complete) return;
        FeaturePoint delta = FeaturePoint.Subtract(axis, geometry);
        FeatureNumber distance = FeaturePoint.Dot(delta, delta);
        if (!distance.IsResolved || distance.Sign != GeometrySign.Positive)
        { _status = CapsuleFeatureStatus.Unresolved; return; }
        GeometrySign inBand = _bandRadius.IsExact
            ? FeaturePoint.CompareDistanceToRadius(axis, geometry, _bandRadius.Value) : GeometrySign.Unresolved;
        if (inBand == GeometrySign.Unresolved) inBand = distance.Compare(_bandSquared);
        if (inBand == GeometrySign.Positive) return;
        if (inBand == GeometrySign.Unresolved) { _status = CapsuleFeatureStatus.Unresolved; return; }
        // No prefix is published. A failed cap makes the entire enumeration unusable.
        if (Candidates.Count == MaximumCandidates) { _status = CapsuleFeatureStatus.CapacityExceeded; return; }
        Candidates.Add(new(leaf, feature, kind, axis, geometry, distance, faces));
    }

    bool Far(ReadOnlySpan<FeaturePoint> points)
    {
        if (!_lower.IsResolved || !_upper.IsResolved || !_bandSquared.IsResolved)
            return false;
        GeometryInterval bound = GeometryInterval.Exact(0);
        for (int i = 0; i < 3; i++)
        {
            double axisLow = Math.Min(Component(_lower, i).Lower, Component(_upper, i).Lower);
            double axisHigh = Math.Max(Component(_lower, i).Upper, Component(_upper, i).Upper);
            double shapeLow = double.PositiveInfinity, shapeHigh = double.NegativeInfinity;
            foreach (FeaturePoint point in points)
            {
                GeometryInterval component = Component(point, i);
                if (!component.IsResolved) return false;
                shapeLow = Math.Min(shapeLow, component.Lower);
                shapeHigh = Math.Max(shapeHigh, component.Upper);
            }
            GeometryInterval gap = axisHigh < shapeLow
                ? GeometryInterval.Exact(shapeLow).Subtract(GeometryInterval.Exact(axisHigh))
                : shapeHigh < axisLow ? GeometryInterval.Exact(axisLow).Subtract(GeometryInterval.Exact(shapeHigh))
                : GeometryInterval.Exact(0);
            bound = bound.Add(gap.Square());
        }
        // A Cartesian bounding box is a superset of every finite polygon/edge/vertex tested here.
        return bound.IsResolved && bound.Lower > _bandSquared.Bounds.Upper;
    }

    static GeometryInterval Component(FeaturePoint p, int axis) => axis == 0 ? p.X.Bounds : axis == 1 ? p.Y.Bounds : p.Z.Bounds;
}
