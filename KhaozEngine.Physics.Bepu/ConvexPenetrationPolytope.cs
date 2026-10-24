using System;
using System.Numerics;
using P = KhaozEngine.Physics.Bepu.ConvexExactPoint;
using Q = KhaozEngine.Physics.Bepu.ConvexQuadratic;

namespace KhaozEngine.Physics.Bepu;

/// <summary>A bounded rational inner CSO hull. Expansion is a proposal, but every vertex
/// belongs to the original CSO and every retained facet is an exact hull halfspace.</summary>
internal sealed class ConvexPenetrationPolytope
{
    readonly P[] _vertices;
    readonly Facet[] _faces;
    readonly bool[] _visible;
    readonly ConvexPolynomialWorkspace _arena;
    readonly (int A, int B)[] _horizon = new (int, int)[CapsuleGeneralSweepPolicy.MaximumPending];
    P _interior;
    internal int VertexCount { get; private set; }
    internal int FaceCount { get; private set; }
    internal readonly record struct Facet(int A, int B, int C, P Normal, Q Offset, Q Squared);

    internal ConvexPenetrationPolytope(int vertices, int faces, ConvexPolynomialWorkspace arena)
    {
        if (vertices < 4 || vertices > CapsuleGeneralSweepPolicy.MaximumVertices ||
            faces < 4 || faces > CapsuleGeneralSweepPolicy.MaximumFaces) throw new ArgumentOutOfRangeException(nameof(vertices));
        _vertices = new P[vertices]; _faces = new Facet[faces]; _visible = new bool[faces];
        _arena = arena;
    }

    internal void Reset()
    {
        Array.Clear(_vertices, 0, VertexCount); Array.Clear(_faces, 0, FaceCount);
        VertexCount = FaceCount = 0;
    }

    internal bool TrySeed(in ConvexQueryRequest request, in ConvexPlanarSource source, P feet, ref ConvexQueryWork work)
    {
        Reset();
        if (_arena.Available < 4 * 3 + 4 * 5) return work.Refuse();
        foreach (Vector3 direction in new[] { new Vector3(1, 1, 1), new(-1, -1, 1), new(-1, 1, -1), new(1, -1, -1) })
        {
            if (!TrySupport(request, source, feet, P.From(direction), ref work, out var point)) return false;
            _vertices[VertexCount++] = point;
        }
        _interior = (_vertices[0] + _vertices[1] + _vertices[2] + _vertices[3]) * Q.From(0.25);
        if (P.Dot(P.Cross(_vertices[1] - _vertices[0], _vertices[2] - _vertices[0]), _vertices[3] - _vertices[0]).Sign == 0)
            return false;
        return AddFace(0, 1, 2, ref work) && AddFace(0, 3, 1, ref work) &&
            AddFace(0, 2, 3, ref work) && AddFace(1, 3, 2, ref work);
    }

    bool AddFace(int a, int b, int c, ref ConvexQueryWork work)
    {
        if (FaceCount == _faces.Length || 3 * (FaceCount + 1) > CapsuleGeneralSweepPolicy.MaximumFaceIndices ||
            3 * VertexCount + 5 * (FaceCount + 1) > _arena.Available) return work.Refuse();
        if (!work.Feature()) return false;
        P p = _vertices[a], n = P.Cross(_vertices[b] - p, _vertices[c] - p);
        Q squared = P.Dot(n, n), side = P.Dot(n, _interior - p);
        if (squared.Sign <= 0 || side.Sign == 0) return false;
        if (side.Sign > 0) { (b, c) = (c, b); n *= Q.From(-1); }
        _faces[FaceCount++] = new(a, b, c, n, P.Dot(n, p), squared);
        return true;
    }

    // A hull enclosing the origin is the intersection of its closed facet halfspaces.
    // The minimum positive offset/|normal| is therefore a certified centred inball radius.
    internal bool TryLower(ref ConvexQueryWork work, out Facet closest, out GeometryInterval radius)
    {
        closest = default; radius = default;
        if (FaceCount < 4 || !work.Cell()) return false;
        for (int i = 0; i < FaceCount; i++)
        {
            if (!work.Feature()) return false;
            var face = _faces[i];
            if (i == 0 || Less(face, closest)) closest = face;
        }
        if (closest.Offset.Sign > 0) radius = Q.RootBounds(closest.Offset * closest.Offset / closest.Squared);
        return true;
    }

    static bool Less(Facet a, Facet b)
    {
        if (a.Offset.Sign != b.Offset.Sign) return a.Offset.Sign < b.Offset.Sign;
        int compare = (a.Offset * a.Offset * b.Squared - b.Offset * b.Offset * a.Squared).Sign;
        return a.Offset.Sign < 0 ? compare > 0 : compare < 0;
    }

    internal bool TryExpand(in ConvexQueryRequest request, in ConvexPlanarSource source, P feet,
        Facet closest, ref ConvexQueryWork work)
    {
        if (VertexCount == _vertices.Length) return work.Refuse();
        if (!TrySupport(request, source, feet, closest.Normal, ref work, out var point)) return false;
        if ((P.Dot(closest.Normal, point) - closest.Offset).Sign <= 0) return false;
        int horizon = 0, visible = 0;
        for (int i = 0; i < FaceCount; i++)
        {
            if (!work.Feature()) return false;
            var f = _faces[i];
            _visible[i] = (P.Dot(f.Normal, point) - f.Offset).Sign > 0;
            if (!_visible[i]) continue;
            visible++;
            if (!Edge(f.A, f.B, ref horizon, ref work) || !Edge(f.B, f.C, ref horizon, ref work) ||
                !Edge(f.C, f.A, ref horizon, ref work)) return false;
        }
        if (visible == 0 || horizon < 3) return false;
        if (FaceCount - visible + horizon > _faces.Length ||
            3 * (VertexCount + 1) + 5 * (FaceCount - visible + horizon) > _arena.Available ||
            3 * (FaceCount - visible + horizon) > CapsuleGeneralSweepPolicy.MaximumFaceIndices) return work.Refuse();
        int retained = 0;
        for (int i = 0; i < FaceCount; i++) if (!_visible[i]) _faces[retained++] = _faces[i];
        Array.Clear(_faces, retained, FaceCount - retained); FaceCount = retained;
        int vertex = VertexCount++; _vertices[vertex] = point;
        for (int i = 0; i < horizon; i++)
            if (!AddFace(_horizon[i].A, _horizon[i].B, vertex, ref work)) return false;
        return true;
    }

    bool Edge(int a, int b, ref int count, ref ConvexQueryWork work)
    {
        for (int i = 0; i < count; i++)
            if (_horizon[i] == (b, a)) { _horizon[i] = _horizon[--count]; return true; }
        if (count == _horizon.Length) return work.Refuse();
        _horizon[count++] = (a, b); return true;
    }

    static bool TrySupport(in ConvexQueryRequest request, in ConvexPlanarSource source, P feet, P direction,
        ref ConvexQueryWork work, out P point)
    {
        point = default;
        if (!work.Cell() || !work.Feature()) return false;
        P t = source.Vertex(0, feet);
        Q projection = P.Dot(t, direction);
        for (int i = 1; i < source.VertexCount; i++)
        {
            if (!work.Feature()) return false;
            P v = source.Vertex(i, feet); Q next = P.Dot(v, direction);
            if ((next - projection).Sign < 0) { t = v; projection = next; }
        }
        // Rounding proposes dyadic inner points. Exact membership, rather than a numerical
        // EPA stopping criterion, authenticates each one before it enters the lower hull.
        Q scale = direction.X * direction.X;
        if ((direction.Y * direction.Y - scale).Sign > 0) scale = direction.Y * direction.Y;
        if ((direction.Z * direction.Z - scale).Sign > 0) scale = direction.Z * direction.Z;
        if (scale.Sign <= 0) return false;
        double divisor = Q.RootBounds(scale).Upper;
        Vector3 n = new((float)(direction.X.Bounds.Lower / divisor), (float)(direction.Y.Bounds.Lower / divisor),
            (float)(direction.Z.Bounds.Lower / divisor));
        Vector3 proposed = ConvexQueryOperations.Support(request.Shape.WithMargin(0), n);
        P q = P.From(proposed);
        float factor = 1;
        while (!ConvexPlanarFeatures.Member(request.Shape, q))
        {
            if (!work.Feature()) return false;
            factor = MathF.BitDecrement(factor);
            q = new(Q.From(proposed.X) * Q.From(factor), Q.From(proposed.Y), Q.From(proposed.Z) * Q.From(factor));
        }
        P rounding = default;
        if (request.Shape.Margin > 0)
        {
            Vector3 m = Vector3.Normalize(n) * request.Shape.Margin;
            rounding = P.From(m); factor = 1;
            Q margin = Q.From(request.Shape.Margin);
            while ((P.Dot(rounding, rounding) - margin * margin).Sign > 0)
            {
                if (!work.Feature()) return false;
                factor = MathF.BitDecrement(factor); rounding = P.From(m) * Q.From(factor);
            }
        }
        point = q + rounding - t;
        return point.X.Rational && point.Y.Rational && point.Z.Rational;
    }
}
