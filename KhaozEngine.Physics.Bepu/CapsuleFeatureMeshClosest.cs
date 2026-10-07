using System;
using System.Collections.Generic;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

internal sealed record CapsuleFeatureMeshCandidate(int FeatureId, CapsuleFeatureKind Kind,
    FeaturePoint Axis, FeaturePoint Geometry, FeatureNumber SquaredDistance, int[] IncidentFaces);

/// <summary>Complete segment/finite-triangle minimum cases over the admitted mesh's disjoint
/// face, edge and vertex strata. Internal coplanar seams retain raw incidence as one finite patch.</summary>
internal sealed class CapsuleFeatureMeshClosest
{
    internal const int MaximumCandidates = 128;
    internal List<CapsuleFeatureMeshCandidate> Candidates { get; } = [];
    readonly CapsuleFeatureMesh _mesh;
    readonly FeaturePoint _lower, _upper, _direction;
    readonly FeatureNumber _axisSquared, _bandRadius, _bandSquared;
    CapsuleFeatureStatus _status = CapsuleFeatureStatus.Complete;

    internal CapsuleFeatureMeshClosest(CapsuleFeatureMesh mesh, FeaturePoint lower, FeaturePoint upper,
        FeatureNumber bandRadius)
    {
        _mesh = mesh;
        _lower = lower;
        _upper = upper;
        _direction = FeaturePoint.Subtract(upper, lower);
        _axisSquared = FeaturePoint.Dot(_direction, _direction);
        _bandRadius = bandRadius;
        _bandSquared = bandRadius.Multiply(bandRadius);
    }

    internal CapsuleFeatureStatus Enumerate()
    {
        for (int i = 0; i < _mesh.Vertices.Length && _status == CapsuleFeatureStatus.Complete; i++) Vertex(i);
        for (int i = 0; i < _mesh.Edges.Length && _status == CapsuleFeatureStatus.Complete; i++) Edge(i);
        for (int i = 0; i < _mesh.Faces.Length && _status == CapsuleFeatureStatus.Complete; i++) Face(i);
        return _status;
    }

    void Vertex(int id)
    {
        FeaturePoint geometry = _mesh.Vertices[id];
        if (Far([geometry])) return;
        FeaturePoint axis = _lower;
        if (_axisSquared.Sign != GeometrySign.Zero)
        {
            FeatureNumber t = FeaturePoint.Dot(FeaturePoint.Subtract(geometry, _lower), _direction).Divide(_axisSquared);
            if (!Clamp(t, out FeatureNumber parameter)) return;
            axis = FeaturePoint.Add(_lower, FeaturePoint.Scale(_direction, parameter));
        }
        Admit(_mesh.Faces.Length + _mesh.Edges.Length + id, _mesh.VertexKinds[id], axis, geometry,
            _mesh.VertexFaces[id]);
    }

    void Edge(int id)
    {
        CapsuleFeatureMeshEdge edge = _mesh.Edges[id];
        FeaturePoint a = _mesh.Vertices[edge.A], second = _mesh.Vertices[edge.B];
        if (Far([a, second])) return;
        FeaturePoint e = FeaturePoint.Subtract(second, a);
        FeatureNumber c = FeaturePoint.Dot(e, e);
        if (c.Sign != GeometrySign.Positive) { _status = CapsuleFeatureStatus.Unresolved; return; }
        EdgeEndpoint(id, a, e, c, _lower);
        if (!_lower.SameExact(_upper)) EdgeEndpoint(id, a, e, c, _upper);
        if (_status != CapsuleFeatureStatus.Complete || _axisSquared.Sign == GeometrySign.Zero) return;
        FeaturePoint r = FeaturePoint.Subtract(_lower, a);
        FeatureNumber b = FeaturePoint.Dot(_direction, e), d = FeaturePoint.Dot(_direction, r), f = FeaturePoint.Dot(e, r);
        FeatureNumber determinant = _axisSquared.Multiply(c).Subtract(b.Multiply(b));
        // A parallel minimum reaches the closed parameter rectangle's boundary. Endpoint and
        // vertex cases cover it, including different equal witnesses for a nontrivial continuum.
        if (determinant.Sign == GeometrySign.Zero) return;
        if (determinant.Sign != GeometrySign.Positive) { _status = CapsuleFeatureStatus.Unresolved; return; }
        FeatureNumber t = b.Multiply(f).Subtract(c.Multiply(d)).Divide(determinant);
        FeatureNumber u = _axisSquared.Multiply(f).Subtract(b.Multiply(d)).Divide(determinant);
        if (!Interior(t) || !Interior(u)) return;
        AdmitEdge(id, FeaturePoint.Add(_lower, FeaturePoint.Scale(_direction, t)),
            FeaturePoint.Add(a, FeaturePoint.Scale(e, u)));
    }

    void EdgeEndpoint(int id, FeaturePoint a, FeaturePoint e, FeatureNumber squared, FeaturePoint axis)
    {
        FeatureNumber u = FeaturePoint.Dot(FeaturePoint.Subtract(axis, a), e).Divide(squared);
        if (Interior(u)) AdmitEdge(id, axis, FeaturePoint.Add(a, FeaturePoint.Scale(e, u)));
    }

    void AdmitEdge(int id, FeaturePoint axis, FeaturePoint geometry)
    {
        CapsuleFeatureMeshEdge edge = _mesh.Edges[id];
        int[] incident = edge.SecondFace < 0 ? [edge.FirstFace] : [edge.FirstFace, edge.SecondFace];
        Admit(_mesh.Faces.Length + id, edge.Kind, axis, geometry, incident);
    }

    void Face(int id)
    {
        int[] face = _mesh.Faces[id];
        FeaturePoint origin = _mesh.Vertices[face[0]], n = _mesh.Normals[id];
        if (Far([origin, _mesh.Vertices[face[1]], _mesh.Vertices[face[2]]])) return;
        GeometrySign lowSide = FeaturePoint.Dot(n, FeaturePoint.Subtract(_lower, origin)).Sign;
        GeometrySign highSide = FeaturePoint.Dot(n, FeaturePoint.Subtract(_upper, origin)).Sign;
        if (lowSide == GeometrySign.Negative && highSide == GeometrySign.Negative) return;
        // A segment crossing the front half-space introduces a clipped-side parameter boundary.
        // This slice refuses that local case instead of omitting its possible constrained minimum.
        if (lowSide == GeometrySign.Unresolved || highSide == GeometrySign.Unresolved ||
            lowSide == GeometrySign.Negative || highSide == GeometrySign.Negative)
        { _status = CapsuleFeatureStatus.Unresolved; return; }
        FeatureNumber squared = FeaturePoint.Dot(n, n);
        if (squared.Sign != GeometrySign.Positive) { _status = CapsuleFeatureStatus.Unresolved; return; }
        FaceEndpoint(id, origin, n, squared, _lower);
        if (!_lower.SameExact(_upper)) FaceEndpoint(id, origin, n, squared, _upper);
        if (_status != CapsuleFeatureStatus.Complete || _axisSquared.Sign == GeometrySign.Zero) return;
        FeatureNumber derivative = FeaturePoint.Dot(n, _direction);
        if (derivative.Sign == GeometrySign.Zero) return;
        if (derivative.Sign == GeometrySign.Unresolved) { _status = CapsuleFeatureStatus.Unresolved; return; }
        FeatureNumber t = FeaturePoint.Dot(n, FeaturePoint.Subtract(origin, _lower)).Divide(derivative);
        if (!Interior(t)) return;
        FeaturePoint intersection = FeaturePoint.Add(_lower, FeaturePoint.Scale(_direction, t));
        GeometrySign membership = _mesh.FaceMembership(id, intersection);
        if (membership is GeometrySign.Positive or GeometrySign.Unresolved)
            _status = CapsuleFeatureStatus.Unresolved;
        // A plane boundary crossing is covered by its edge or vertex and also has zero distance.
    }

    void FaceEndpoint(int id, FeaturePoint origin, FeaturePoint normal, FeatureNumber squared, FeaturePoint axis)
    {
        FeatureNumber plane = FeaturePoint.Dot(FeaturePoint.Subtract(axis, origin), normal);
        if (plane.Sign == GeometrySign.Negative) return;
        FeaturePoint geometry = FeaturePoint.Subtract(axis, FeaturePoint.Scale(normal, plane.Divide(squared)));
        GeometrySign membership = _mesh.FaceMembership(id, geometry);
        if (membership == GeometrySign.Unresolved) { _status = CapsuleFeatureStatus.Unresolved; return; }
        if (membership == GeometrySign.Positive)
            Admit(id, CapsuleFeatureKind.FaceInterior, axis, geometry, [id]);
    }

    void Admit(int featureId, CapsuleFeatureKind kind, FeaturePoint axis, FeaturePoint geometry, int[] faces)
    {
        if (_status != CapsuleFeatureStatus.Complete) return;
        FeaturePoint delta = FeaturePoint.Subtract(axis, geometry);
        FeatureNumber squared = FeaturePoint.Dot(delta, delta);
        GeometrySign inBand = _bandRadius.IsExact
            ? FeaturePoint.CompareDistanceToRadius(axis, geometry, _bandRadius.Value) : GeometrySign.Unresolved;
        if (inBand == GeometrySign.Unresolved) inBand = squared.Compare(_bandSquared);
        if (inBand == GeometrySign.Positive) return;
        if (inBand == GeometrySign.Unresolved || !squared.IsResolved)
        { _status = CapsuleFeatureStatus.Unresolved; return; }
        bool front = false, back = false, uncertain = false;
        foreach (int face in faces)
        {
            GeometrySign side = FeaturePoint.Dot(_mesh.Normals[face],
                FeaturePoint.Subtract(axis, _mesh.Vertices[_mesh.Faces[face][0]])).Sign;
            front |= side == GeometrySign.Positive;
            back |= side == GeometrySign.Negative;
            uncertain |= side == GeometrySign.Unresolved;
        }
        // Strictly back-sided candidates cannot qualify. Mixed neighborhoods and purely grazing
        // directions have no one-sided certificate in this slice. Exact zero on a wall is allowed
        // with a positively front-sided incident top. No contact threshold replaces these signs.
        if (back && !front && !uncertain) return;
        if (!front || back || uncertain || squared.Sign != GeometrySign.Positive)
        { _status = CapsuleFeatureStatus.Unresolved; return; }
        if (Candidates.Count == MaximumCandidates) { _status = CapsuleFeatureStatus.CapacityExceeded; return; }
        Candidates.Add(new(featureId, kind, axis, geometry, squared, faces));
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
        if (low is GeometrySign.Negative or GeometrySign.Zero || high is GeometrySign.Positive or GeometrySign.Zero)
            return false;
        if (low != GeometrySign.Positive || high != GeometrySign.Negative)
        { _status = CapsuleFeatureStatus.Unresolved; return false; }
        return true;
    }

    bool Far(ReadOnlySpan<FeaturePoint> points) =>
        CapsuleFeatureMeshBounds.Far(_lower, _upper, _bandSquared, points);
}
