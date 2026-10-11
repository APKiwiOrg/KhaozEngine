using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Closed-stratum signs over the captured local vertices and their one shared coefficient box.
/// Squared radical comparisons are polynomials of degree at most eight. The outward path calls this owner
/// only when its sign is undecided. No rounded vertex or coefficient midpoint defines the source.</summary>
internal sealed class SupportFootprintSourcePredicates
{
    readonly SupportSourcePolynomialWorkspace _work = new();
    readonly SupportSourcePolynomial[] _map = new SupportSourcePolynomial[9];
    SupportPolygon _polygon = null!;

    readonly record struct Point(SupportSourcePolynomial X, SupportSourcePolynomial Y, SupportSourcePolynomial Z);

    bool Begin(SupportPolygon polygon)
    {
        if (!polygon.HasSource) return false;
        SupportQueryDiagnostics.Record(SupportQueryCounter.ExactFallbacks);
        _polygon = polygon;
        _work.Reset();
        var map = polygon.SourceMap;
        _map[0] = _work.Coefficient(map.X.X);
        _map[1] = _work.Coefficient(map.Y.X);
        _map[2] = _work.Coefficient(map.Z.X);
        _map[3] = _work.Coefficient(map.X.Y);
        _map[4] = _work.Coefficient(map.Y.Y);
        _map[5] = _work.Coefficient(map.Z.Y);
        _map[6] = _work.Coefficient(map.X.Z);
        _map[7] = _work.Coefficient(map.Y.Z);
        _map[8] = _work.Coefficient(map.Z.Z);
        _work.CompleteMap();
        return _work.IsResolved;
    }

    SupportSourcePolynomial Component(Vector3 a, Vector3 b, int axis, double translation = 0) =>
        _work.Linear(_map[axis * 3], _map[axis * 3 + 1], _map[axis * 3 + 2], a, b, translation);

    Point Offset(int vertex, FeaturePoint axis)
    {
        Vector3 local = _polygon.SourceVertices[vertex];
        return new(Sub(Component(local, default, 0, _polygon.SourceMap.Translation.X), _work.Constant(axis.X.Value)),
            _work.Constant(0), Sub(Component(local, default, 2, _polygon.SourceMap.Translation.Z), _work.Constant(axis.Z.Value)));
    }

    Point Edge(int edge)
    {
        Vector3 a = _polygon.SourceVertices[edge], b = _polygon.SourceVertices[(edge + 1) % _polygon.SourceVertices.Length];
        return new(Component(b, a, 0), Component(b, a, 1), Component(b, a, 2));
    }

    Point Normal()
    {
        Vector3 a = _polygon.SourceVertices[0], b = _polygon.SourceVertices[1], c = _polygon.SourceVertices[2];
        var u = new Point(Component(b, a, 0), Component(b, a, 1), Component(b, a, 2));
        var v = new Point(Component(c, a, 0), Component(c, a, 1), Component(c, a, 2));
        return new(Sub(Product(u.Y, v.Z), Product(u.Z, v.Y)),
            Sub(Product(u.Z, v.X), Product(u.X, v.Z)), Sub(Product(u.X, v.Y), Product(u.Y, v.X)));
    }

    SupportSourcePolynomial Product(SupportSourcePolynomial a, SupportSourcePolynomial b) => _work.Multiply(a, b);
    SupportSourcePolynomial Sub(SupportSourcePolynomial a, SupportSourcePolynomial b) => _work.Add(a, b, -1);
    SupportSourcePolynomial Dot(Point a, Point b) => _work.Add(Product(a.X, b.X), Product(a.Z, b.Z));
    SupportSourcePolynomial Dot3(Point a, Point b) => _work.Add(Dot(a, b), Product(a.Y, b.Y));
    SupportSourcePolynomial Cross(Point a, Point b) => Sub(Product(a.X, b.Z), Product(a.Z, b.X));
    SupportSourcePolynomial Square(SupportSourcePolynomial value) => Product(value, value);

    internal GeometrySign NormalComponent(SupportPolygon polygon, int axis, int direction = 1)
    {
        if (!Begin(polygon)) return GeometrySign.Unresolved;
        Point normal = Normal();
        return _work.Sign(_work.Scale(axis == 0 ? normal.X : axis == 1 ? normal.Y : normal.Z, direction));
    }

    internal GeometrySign NormalSquared(SupportPolygon polygon, bool projected = false)
    {
        if (!Begin(polygon)) return GeometrySign.Unresolved;
        Point normal = Normal();
        return _work.Sign(projected ? Dot(normal, normal) : Dot3(normal, normal));
    }

    internal GeometrySign EdgeLength(SupportPolygon polygon, int index)
    {
        if (!Begin(polygon)) return GeometrySign.Unresolved;
        Point edge = Edge(index);
        return _work.Sign(Dot(edge, edge));
    }

    internal GeometrySign VertexDisc(SupportPolygon polygon, int vertex, FeaturePoint axis, FeatureNumber radius)
    {
        if (!radius.IsExact || !Begin(polygon)) return GeometrySign.Unresolved;
        Point delta = Offset(vertex, axis);
        return _work.Sign(Sub(Dot(delta, delta), Square(_work.Constant(radius.Value))));
    }

    SupportSourcePolynomial Discriminant(Point delta, Point edge, FeatureNumber radius) =>
        Sub(Product(Dot(edge, edge), Square(_work.Constant(radius.Value))), Square(Cross(delta, edge)));

    internal GeometrySign EdgeDiscriminant(SupportPolygon polygon, int index, FeaturePoint axis, FeatureNumber radius)
    {
        if (!radius.IsExact || !Begin(polygon)) return GeometrySign.Unresolved;
        return _work.Sign(Discriminant(Offset(index, axis), Edge(index), radius));
    }

    internal GeometrySign EdgeRoot(SupportPolygon polygon, int index, FeaturePoint axis, FeatureNumber radius,
        int direction, bool high)
    {
        if (!radius.IsExact || !Begin(polygon)) return GeometrySign.Unresolved;
        Point delta = Offset(index, axis), edge = Edge(index);
        var dot = Dot(delta, edge);
        var k = high ? _work.Add(Dot(edge, edge), dot) : _work.Scale(dot, -1);
        return Radical(k, _work.Constant(high ? -direction : direction), Discriminant(delta, edge, radius));
    }

    internal GeometrySign EdgeOtherRoot(SupportPolygon polygon, int index, FeaturePoint axis, bool endpoint, bool high)
    {
        if (!Begin(polygon)) return GeometrySign.Unresolved;
        Point delta = Offset(index, axis), edge = Edge(index);
        var a = Dot(edge, edge);
        var k = _work.Scale(Dot(delta, edge), -2);
        if (endpoint) k = Sub(k, a);
        return _work.Sign(high ? Sub(a, k) : k);
    }

    internal GeometrySign Interior(SupportPolygon polygon, int index, FeaturePoint axis, FeatureNumber radius,
        int direction, bool spatial = false)
    {
        if (!radius.IsExact || !Begin(polygon)) return GeometrySign.Unresolved;
        Point delta = Offset(index, axis), edge = Edge(index);
        var k = _work.Scale(Cross(delta, edge), -1);
        if (direction == 0) return _work.Sign(k);
        Point normal = Normal();
        var l = _work.Scale(Cross(normal, edge), radius.Value * direction);
        return Radical(l, k, spatial ? Dot3(normal, normal) : Dot(normal, normal));
    }

    internal GeometrySign ProposalInterior(SupportPolygon polygon, FeaturePoint point, FeaturePoint axis, FeatureNumber radius)
    {
        bool boundary = false, unknown = false;
        for (int i = 0; i < polygon.Edges.Length; i++)
        {
            GeometrySign side = polygon.EdgeSide(point, i);
            if (side == GeometrySign.Unresolved) side = Interior(polygon, i, axis, radius, -1, spatial: true);
            if (side == GeometrySign.Negative) return side;
            boundary |= side == GeometrySign.Zero;
            unknown |= side == GeometrySign.Unresolved;
        }
        return unknown ? GeometrySign.Unresolved : boundary ? GeometrySign.Zero : GeometrySign.Positive;
    }

    internal GeometrySign EdgeStationary(SupportPolygon polygon, int index, FeaturePoint axis,
        FeatureNumber radius, bool high)
    {
        if (!radius.IsExact || !Begin(polygon)) return GeometrySign.Unresolved;
        Point delta = Offset(index, axis), edge = Edge(index);
        var dot = Dot(delta, edge);
        var k = high ? _work.Add(Dot(edge, edge), dot) : _work.Scale(dot, -1);
        var l = high ? _work.Scale(edge.Y, -1) : edge.Y;
        var d = Discriminant(delta, edge, radius);
        var total = Dot3(edge, edge);
        // k*sqrt(total) + l*sqrt(d) has the same ordering as the stationary parameter.
        GeometrySign ks = _work.Sign(k), ls = _work.Sign(l), ds = _work.Sign(d);
        if (_work.Sign(total) != GeometrySign.Positive || ds is GeometrySign.Negative or GeometrySign.Unresolved)
            return GeometrySign.Unresolved;
        if (ls == GeometrySign.Zero || ds == GeometrySign.Zero) return ks;
        if (ks == GeometrySign.Zero || ks == ls) return ls;
        if (ks == GeometrySign.Unresolved || ls == GeometrySign.Unresolved) return GeometrySign.Unresolved;
        GeometrySign comparison = _work.Sign(Sub(Product(Square(k), total), Product(Square(l), d)));
        return comparison is GeometrySign.Zero or GeometrySign.Unresolved ? comparison :
            comparison == GeometrySign.Positive ? ks : ls;
    }

    GeometrySign Radical(SupportSourcePolynomial k, SupportSourcePolynomial l, SupportSourcePolynomial d)
    {
        GeometrySign ks = _work.Sign(k), ls = _work.Sign(l), ds = _work.Sign(d);
        if (ds == GeometrySign.Negative || ds == GeometrySign.Unresolved) return GeometrySign.Unresolved;
        if (ls == GeometrySign.Zero || ds == GeometrySign.Zero) return ks;
        if (ks == GeometrySign.Zero) return ls;
        if (ks == GeometrySign.Unresolved || ls == GeometrySign.Unresolved) return GeometrySign.Unresolved;
        if (ks == ls) return ks;
        GeometrySign comparison = _work.Sign(Sub(Square(k), Product(Square(l), d)));
        return comparison == GeometrySign.Unresolved || comparison == GeometrySign.Zero ? comparison :
            comparison == GeometrySign.Positive ? ks : ls;
    }
}
