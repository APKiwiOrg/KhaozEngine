using System;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

internal sealed partial class SupportFootprintSourcePredicates
{
    /// <summary>Certifies min(n dot (vertex-axis)) against radius*|n| under the represented source map.
    /// The projection's sign is proved before squaring. Equality never excludes a face.</summary>
    internal GeometrySign ProjectedSeparation(SupportPolygon polygon, double nx, double nz,
        FeatureNumber axisX, FeatureNumber axisZ, FeatureNumber radius)
    {
        if (!axisX.IsExact || !axisZ.IsExact || !radius.IsExact || radius.Value < 0 ||
            !double.IsFinite(nx) || !double.IsFinite(nz) || (nx == 0 && nz == 0) || polygon.Vertices.Length == 0)
            return GeometrySign.Unresolved;
        bool boundary = false, unknown = false;
        for (int i = 0; i < polygon.Vertices.Length; i++)
        {
            // Reset per vertex so the fixed expression bound is independent of the face's vertex count.
            if (!Begin(polygon)) return GeometrySign.Unresolved;
            Point delta = Offset(i, new(axisX, FeatureNumber.Exact(0), axisZ));
            var projection = _work.Add(_work.Scale(delta.X, nx), _work.Scale(delta.Z, nz));
            GeometrySign forward = _work.Sign(projection);
            if (forward == GeometrySign.Negative) return forward;
            var squaredNormal = _work.Add(Square(_work.Constant(nx)), Square(_work.Constant(nz)));
            var squaredSupport = Product(Square(_work.Constant(radius.Value)), squaredNormal);
            GeometrySign comparison = _work.Sign(Sub(Square(projection), squaredSupport));
            if (comparison == GeometrySign.Negative) return comparison;
            unknown |= forward == GeometrySign.Unresolved || comparison == GeometrySign.Unresolved;
            boundary |= comparison == GeometrySign.Zero;
        }
        return unknown ? GeometrySign.Unresolved : boundary ? GeometrySign.Zero : GeometrySign.Positive;
    }
}
