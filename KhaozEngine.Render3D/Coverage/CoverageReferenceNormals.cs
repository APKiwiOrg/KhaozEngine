using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Independent indexed gathers over the ordered corner rows. Face and vertex inputs stay immutable.</summary>
internal static class CoverageReferenceNormals
{
    /// <summary>Writes the completed candidate to Published. Caller output is copied only after this phase succeeds.</summary>
    internal static bool TryUpdate(CoverageRefinement topology, CoverageReferenceScratch scratch, out CoverageFailure failure)
    {
        CoverageFailureCode code;
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(scratch);
        if (!ReferenceEquals(scratch.ReferenceTopology.Topology, topology))
            throw new ArgumentException("Reference normal scratch must be built for this topology instance.", nameof(scratch));
        for (int v = 0; v < topology.VertexCount; v++)
            if (!ValidateVertex(scratch.Arithmetic, scratch, v))
                return Refuse(topology, CoverageFailureCode.UnsupportedTransform, out failure);
        // Finish face validation before publishing any attribute. Command signs reduce in triangle order.
        Span<int> signs = stackalloc int[CoverageScratch.MaxCommands];
        signs.Clear();
        for (int t = 0; t < topology.TriangleCount; t++)
        {
            if (!ValidateFace(scratch.Arithmetic, scratch, t, signs, out code))
                return Refuse(topology, code, out failure);
        }
        for (int vertex = 0; vertex < topology.VertexCount; vertex++)
            if (!UpdateVertex(scratch.Arithmetic, scratch, vertex, out code))
                return Refuse(topology, code, out failure);
        failure = default;
        return true;
    }

    [Binary32Kernel]
    static bool ValidateVertex(CoverageReferenceArithmetic f, CoverageReferenceScratch scratch, int vertex)
    {
        int region = scratch.ReferenceTopology.Vertices[vertex].RegionIndex;
        f.At(CoverageExactnessPhase.NormalGather, vertex, region < 0 ? -1 : scratch.RegionCommands[region]);
        return CoverageReferenceMath.IsFiniteVertex(f, scratch.Before[vertex]) && CoverageReferenceMath.IsFinite3(f, scratch.After[vertex].Position);
    }

    [Binary32Kernel]
    static bool ValidateFace(CoverageReferenceArithmetic f, CoverageReferenceScratch scratch,
        int t, Span<int> signs, out CoverageFailureCode code)
    {
        Basis checkedBasis;
        var data = scratch.ReferenceTopology;
        var face = data.Triangles[t];
        int slot = face.RegionIndex < 0 || scratch.ActiveCommands == 0 ? -1 : scratch.RegionCommands[face.RegionIndex];
        f.At(CoverageExactnessPhase.FaceValidation, t, slot);
        if (Changed(f, scratch, t) && !TryBasis(f, scratch, t, out checkedBasis, out code))
            return false;
        if (slot < 0) { code = default; return true; }
        var frame = scratch.Frames[slot];
        Vector3 a = CoverageReferenceMath.Point(f, scratch.After[(int)scratch.ReferenceTopology.Indices[t * 3]].Position, frame.HostToCoverage),
            b = CoverageReferenceMath.Point(f, scratch.After[(int)scratch.ReferenceTopology.Indices[(t * 3) + 1]].Position, frame.HostToCoverage),
            c = CoverageReferenceMath.Point(f, scratch.After[(int)scratch.ReferenceTopology.Indices[(t * 3) + 2]].Position, frame.HostToCoverage);
        Vector3 u = CoverageReferenceMath.Sub3(f, b, a), v = CoverageReferenceMath.Sub3(f, c, a);
        float area = f.Sub((f.Mul(u.X, v.Y)), (f.Mul(u.Y, v.X)));
        float scale = f.Mul(f.Sqrt(CoverageReferenceMath.Dot3(f, u, u)), f.Sqrt(CoverageReferenceMath.Dot3(f, v, v)));
        if (!CoverageReferenceMath.IsFinite3(f, a) || !CoverageReferenceMath.IsFinite3(f, b) || !CoverageReferenceMath.IsFinite3(f, c)
            || !f.IsFinite(area) || !f.IsFinite(scale))
        { code = CoverageFailureCode.UnsupportedTransform; return false; }
        // The magnitude threshold mirrors the double guard. The discrete orientation uses the B27 exact predicate.
        if (!(f.Greater(f.Abs(area), f.Mul(1e-9f, scale)))) { code = CoverageFailureCode.FoldedPatch; return false; }
        var orientation = f.Edge(a, b, c);
        if (!f.IsFinite(orientation.Value)) { code = CoverageFailureCode.UnsupportedTransform; return false; }
        if (orientation.Sign == 0 || signs[slot] != 0 && signs[slot] != orientation.Sign)
        { code = CoverageFailureCode.FoldedPatch; return false; }
        signs[slot] = orientation.Sign;
        code = default;
        return true;
    }

    [Binary32Kernel]
    static bool UpdateVertex(CoverageReferenceArithmetic f, CoverageReferenceScratch scratch,
        int vertex, out CoverageFailureCode code)
    {
        var rows = scratch.ReferenceTopology;
        int region = scratch.ReferenceTopology.Vertices[vertex].RegionIndex;
        f.At(CoverageExactnessPhase.NormalGather, vertex, region < 0 ? -1 : scratch.RegionCommands[region]);
        var before = scratch.Before[vertex];
        var result = before;
        if (!Moved(f, scratch, vertex)) { scratch.Published[vertex] = result; code = default; return true; }
        result.Position = scratch.After[vertex].Position;
        int shading = scratch.ReferenceTopology.Vertices[vertex].ShadingGroup;
        if (!CoverageReferenceMath.Equal3(f, before.Normal, default(Vector3)))
        {
            Vector3 normal;
            if (!Gather(f, scratch, rows.ShadingCorners.AsSpan(rows.ShadingCornerOffsets[shading],
                rows.ShadingCornerOffsets[shading + 1] - rows.ShadingCornerOffsets[shading]), false, out normal, out code))
                return false;
            result.Normal = normal;
        }
        if (f.NotEqual(before.Tangent.X, 0) || f.NotEqual(before.Tangent.Y, 0) || f.NotEqual(before.Tangent.Z, 0))
        {
            Vector3 tangent;
            if (!Gather(f, scratch, rows.VertexCorners.AsSpan(rows.VertexFaceOffsets[vertex],
                rows.VertexFaceOffsets[vertex + 1] - rows.VertexFaceOffsets[vertex]), true, out tangent, out code))
                return false;
            result.Tangent = new Vector4(tangent, before.Tangent.W);
        }
        scratch.Published[vertex] = result;
        code = default;
        return true;
    }

    [Binary32Kernel]
    static bool Gather(CoverageReferenceArithmetic f, CoverageReferenceScratch scratch, ReadOnlySpan<int> corners,
        bool tangent, out Vector3 direction, out CoverageFailureCode code)
    {
        Vector3 sum = default;
        foreach (int corner in corners)
        {
            Basis basis;
            Vector3 unit;
            int vertex = (int)scratch.ReferenceTopology.Indices[corner];
            if (!Moved(f, scratch, vertex)) continue;
            var authored = scratch.Before[vertex];
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

    [Binary32Kernel]
    static bool TryBasis(CoverageReferenceArithmetic f, CoverageReferenceScratch scratch, int triangle,
        out Basis basis, out CoverageFailureCode code)
    {
        Vector3 n;
        Vector3 m;
        basis = default;
        int a = (int)scratch.ReferenceTopology.Indices[triangle * 3], b = (int)scratch.ReferenceTopology.Indices[(triangle * 3) + 1], c = (int)scratch.ReferenceTopology.Indices[(triangle * 3) + 2];
        Vector3 u = CoverageReferenceMath.Sub3(f, scratch.Before[b].Position, scratch.Before[a].Position), v = CoverageReferenceMath.Sub3(f, scratch.Before[c].Position, scratch.Before[a].Position),
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

    [Binary32Kernel]
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

    [Binary32Kernel]
    static Vector3 Combine(CoverageReferenceArithmetic f, Vector3 a, float x, Vector3 b, float y, Vector3 c, float z) => new Vector3(
        f.Add((f.Add((f.Mul(a.X, x)), (f.Mul(b.X, y)))), (f.Mul(c.X, z))), f.Add((f.Add((f.Mul(a.Y, x)), (f.Mul(b.Y, y)))), (f.Mul(c.Y, z))), f.Add((f.Add((f.Mul(a.Z, x)), (f.Mul(b.Z, y)))), (f.Mul(c.Z, z))));

    [Binary32Kernel]
    static Vector3 Divide(CoverageReferenceArithmetic f, Vector3 value, float divisor) => new Vector3(f.Div(value.X, divisor), f.Div(value.Y, divisor), f.Div(value.Z, divisor));

    [Binary32Kernel]
    static bool Moved(CoverageReferenceArithmetic f, CoverageReferenceScratch scratch, int vertex) => !CoverageReferenceMath.Equal3(f, scratch.Before[vertex].Position, scratch.After[vertex].Position);

    [Binary32Kernel]
    static bool Changed(CoverageReferenceArithmetic f, CoverageReferenceScratch scratch, int triangle) =>
        Moved(f, scratch, (int)scratch.ReferenceTopology.Indices[triangle * 3]) || Moved(f, scratch, (int)scratch.ReferenceTopology.Indices[(triangle * 3) + 1])
        || Moved(f, scratch, (int)scratch.ReferenceTopology.Indices[(triangle * 3) + 2]);

    [Binary32Kernel]
    static bool Refuse(CoverageRefinement topology, CoverageFailureCode code, out CoverageFailure failure)
    { failure = new CoverageFailure(code, topology.HostIdentity, 0, 0, 0, 0); return false; }

    readonly record struct Basis(Vector3 U, Vector3 V, Vector3 N, Vector3 S, Vector3 T, Vector3 M,
        Vector3 Bu, Vector3 Bv, Vector3 Bn, Vector3 Au, Vector3 Av, Vector3 An);
}
