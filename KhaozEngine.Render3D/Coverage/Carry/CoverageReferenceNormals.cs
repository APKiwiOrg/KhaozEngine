using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Independent indexed gathers over the ordered corner rows. Face and vertex inputs stay immutable.</summary>
internal static class CoverageReferenceNormals
{
    /// <summary>Writes the completed candidate to Published. Caller output is copied only after this phase succeeds.</summary>
    internal static bool TryUpdate(CoverageRefinement topology, CoverageCarryScratch scratch, out CoverageFailure failure)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(scratch);
        if (!ReferenceEquals(scratch.ReferenceTopology.Topology, topology))
            throw new ArgumentException("Reference normal scratch must be built for this topology instance.", nameof(scratch));
        for (int v = 0; v < topology.VertexCount; v++)
            if (!ValidateVertex(scratch.Arithmetic, scratch, v))
                return Refuse(topology, CoverageFailureCode.UnsupportedTransform, out failure);
        for (int vertex = 0; vertex < topology.VertexCount; vertex++)
            UpdateVertex(scratch.Arithmetic, scratch, vertex);
        failure = default;
        return true;
    }

    static bool ValidateVertex(CoverageReferenceArithmetic f, CoverageCarryScratch scratch, int vertex)
    {
        return CoverageReferenceMath.IsFinite3(f, scratch.BeforeVertices[vertex].Position) && CoverageReferenceMath.IsFinite3(f, scratch.After[vertex].Position);
    }

    static void UpdateVertex(CoverageReferenceArithmetic f, CoverageCarryScratch scratch, int vertex)
    {
        var rows = scratch.ReferenceTopology;
        var before = scratch.BeforeVertices[vertex];
        var result = before;
        bool moved = Moved(f, scratch, vertex);
        if (moved) result.Position = scratch.After[vertex].Position;
        int shading = scratch.ReferenceTopology.Vertices[vertex].ShadingGroup;
        bool normalValid = true, tangentValid = true;
        Vector3 normal = before.Normal, tangent = new(before.Tangent.X, before.Tangent.Y, before.Tangent.Z);
        if (moved && !CoverageReferenceMath.Equal3(f, before.Normal, default(Vector3)))
        {
            normalValid = Gather(f, scratch, rows.ShadingCorners.AsSpan(rows.ShadingCornerOffsets[shading],
                rows.ShadingCornerOffsets[shading + 1] - rows.ShadingCornerOffsets[shading]), false, out normal, out _);
        }
        if (moved && (f.NotEqual(before.Tangent.X, 0) || f.NotEqual(before.Tangent.Y, 0) || f.NotEqual(before.Tangent.Z, 0)))
        {
            tangentValid = Gather(f, scratch, rows.VertexCorners.AsSpan(rows.VertexFaceOffsets[vertex],
                rows.VertexFaceOffsets[vertex + 1] - rows.VertexFaceOffsets[vertex]), true, out tangent, out _);
        }
        result.Normal = CoverageDirectionRecovery.Resolve(normal, normalValid, before.Normal, CoverageDirectionRecovery.Authored(rows.Topology, vertex, false), false);
        result.Tangent = new(CoverageDirectionRecovery.Resolve(tangent, tangentValid,
            new(before.Tangent.X, before.Tangent.Y, before.Tangent.Z), CoverageDirectionRecovery.Authored(rows.Topology, vertex, true), true),
            CoverageDirectionRecovery.Handedness(before.Tangent.W));
        scratch.Published[vertex] = result;
    }

    static bool Gather(CoverageReferenceArithmetic f, CoverageCarryScratch scratch, ReadOnlySpan<int> corners,
        bool tangent, out Vector3 direction, out CoverageFailureCode code)
    {
        Vector3 sum = default;
        foreach (int corner in corners)
        {
            Basis basis;
            Vector3 unit;
            int vertex = (int)scratch.ReferenceTopology.Indices[corner];
            if (!Moved(f, scratch, vertex)) continue;
            var authored = scratch.BeforeVertices[vertex];
            Vector3 original = tangent ? new Vector3(authored.Tangent.X, authored.Tangent.Y, authored.Tangent.Z) : authored.Normal;
            if (CoverageReferenceMath.Equal3(f, original, default(Vector3))) continue;
            if (!TryBasis(f, scratch, corner / 3, out basis, out code)) { direction = default; return false; }
            Vector3 transformed = tangent
                ? Combine(f, basis.S, CoverageReferenceMath.Dot3(f, basis.Bu, original), basis.T, CoverageReferenceMath.Dot3(f, basis.Bv, original),
                    basis.M, CoverageReferenceMath.Dot3(f, basis.Bn, original))
                : Combine(f, basis.Au, CoverageReferenceMath.Dot3(f, basis.U, original), basis.Av, CoverageReferenceMath.Dot3(f, basis.V, original),
                    basis.An, CoverageReferenceMath.Dot3(f, basis.N, original));
            if (!Unit(f, transformed, out unit, out code)) { direction = default; return false; }
            sum = new Vector3(f.Add(sum.X, unit.X), f.Add(sum.Y, unit.Y), f.Add(sum.Z, unit.Z));
            if (!CoverageReferenceMath.IsFinite3(f, sum)) { direction = default; code = CoverageFailureCode.UnsupportedTransform; return false; }
        }
        return Unit(f, sum, out direction, out code);
    }

    static bool TryBasis(CoverageReferenceArithmetic f, CoverageCarryScratch scratch, int triangle,
        out Basis basis, out CoverageFailureCode code)
    {
        Vector3 n;
        Vector3 m;
        basis = default;
        int a = (int)scratch.ReferenceTopology.Indices[triangle * 3], b = (int)scratch.ReferenceTopology.Indices[(triangle * 3) + 1], c = (int)scratch.ReferenceTopology.Indices[(triangle * 3) + 2];
        Vector3 u = CoverageReferenceMath.Sub3(f, scratch.BeforeVertices[b].Position, scratch.BeforeVertices[a].Position), v = CoverageReferenceMath.Sub3(f, scratch.BeforeVertices[c].Position, scratch.BeforeVertices[a].Position),
            s = CoverageReferenceMath.Sub3(f, scratch.After[b].Position, scratch.After[a].Position), t = CoverageReferenceMath.Sub3(f, scratch.After[c].Position, scratch.After[a].Position);
        Vector3 normal = CoverageReferenceMath.Cross(f, u, v), finalNormal = CoverageReferenceMath.Cross(f, s, t);
        if (!Unit(f, normal, out n, out code) || !Unit(f, finalNormal, out m, out code)) return false;
        float alignment = CoverageReferenceMath.Dot3(f, normal, finalNormal);
        float beforeDet = CoverageReferenceMath.Dot3(f, u, CoverageReferenceMath.Cross(f, v, n)),
            afterDet = CoverageReferenceMath.Dot3(f, s, CoverageReferenceMath.Cross(f, t, m));
        float beforeScale = f.Sqrt(f.Mul(CoverageReferenceMath.Dot3(f, u, u), CoverageReferenceMath.Dot3(f, v, v))),
            afterScale = f.Sqrt(f.Mul(CoverageReferenceMath.Dot3(f, s, s), CoverageReferenceMath.Dot3(f, t, t)));
        if (!f.IsFinite(alignment) || !f.IsFinite(beforeDet) || !f.IsFinite(afterDet)
            || !f.IsFinite(beforeScale) || !f.IsFinite(afterScale))
        { code = CoverageFailureCode.UnsupportedTransform; return false; }
        if (!(f.Greater(alignment, 0)) || !(f.Greater(beforeDet, 0)) || !(f.Greater(afterDet, 0))
            || f.LessOrEqual(beforeDet, f.Mul(beforeScale, 1e-12f)) || f.LessOrEqual(afterDet, f.Mul(afterScale, 1e-12f)))
        { code = CoverageFailureCode.FoldedPatch; return false; }
        Vector3 bu = Divide(f, CoverageReferenceMath.Cross(f, v, n), beforeDet), bv = Divide(f, CoverageReferenceMath.Cross(f, n, u), beforeDet),
            bn = Divide(f, normal, beforeDet), au = Divide(f, CoverageReferenceMath.Cross(f, t, m), afterDet),
            av = Divide(f, CoverageReferenceMath.Cross(f, m, s), afterDet), an = Divide(f, finalNormal, afterDet);
        if (!CoverageReferenceMath.IsFinite3(f, bu) || !CoverageReferenceMath.IsFinite3(f, bv) || !CoverageReferenceMath.IsFinite3(f, bn)
            || !CoverageReferenceMath.IsFinite3(f, au) || !CoverageReferenceMath.IsFinite3(f, av) || !CoverageReferenceMath.IsFinite3(f, an))
        { code = CoverageFailureCode.UnsupportedTransform; return false; }
        basis = new Basis(u, v, n, s, t, m, bu, bv, bn, au, av, an);
        return true;
    }

    static bool Unit(CoverageReferenceArithmetic f, Vector3 value, out Vector3 unit, out CoverageFailureCode code)
    {
        unit = default;
        float length = f.Sqrt(CoverageReferenceMath.Dot3(f, value, value));
        code = CoverageFailureCode.UnsupportedTransform;
        if (!CoverageReferenceMath.IsFinite3(f, value) || !f.IsFinite(length)) return false;
        if (!(f.Greater(length, 0))) { code = CoverageFailureCode.FoldedPatch; return false; }
        unit = Divide(f, value, length);
        if (!CoverageReferenceMath.IsFinite3(f, unit)) return false;
        code = default;
        return true;
    }

    static Vector3 Combine(CoverageReferenceArithmetic f, Vector3 a, float x, Vector3 b, float y, Vector3 c, float z) => new Vector3(
        f.Add((f.Add((f.Mul(a.X, x)), (f.Mul(b.X, y)))), (f.Mul(c.X, z))), f.Add((f.Add((f.Mul(a.Y, x)), (f.Mul(b.Y, y)))), (f.Mul(c.Y, z))), f.Add((f.Add((f.Mul(a.Z, x)), (f.Mul(b.Z, y)))), (f.Mul(c.Z, z))));

    static Vector3 Divide(CoverageReferenceArithmetic f, Vector3 value, float divisor) => new Vector3(f.Div(value.X, divisor), f.Div(value.Y, divisor), f.Div(value.Z, divisor));

    static bool Moved(CoverageReferenceArithmetic f, CoverageCarryScratch scratch, int vertex) => !CoverageReferenceMath.Equal3(f, scratch.BeforeVertices[vertex].Position, scratch.After[vertex].Position);

    static bool Refuse(CoverageRefinement topology, CoverageFailureCode code, out CoverageFailure failure)
    { failure = new CoverageFailure(code, topology.HostIdentity, 0, 0, 0, 0); return false; }

    readonly record struct Basis(Vector3 U, Vector3 V, Vector3 N, Vector3 S, Vector3 T, Vector3 M,
        Vector3 Bu, Vector3 Bv, Vector3 Bn, Vector3 Au, Vector3 Av, Vector3 An);
}
