using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Independent indexed gathers over the ordered corner rows. Face and vertex inputs stay immutable.</summary>
internal static class CoverageReferenceNormals
{
    /// <summary>Writes the completed candidate to Published. Caller output is copied only after this phase succeeds.</summary>
    internal static bool TryUpdate(CoverageRefinement topology, CoverageReferenceScratch scratch, out CoverageFailure failure)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(scratch);
        if (!ReferenceEquals(scratch.ReferenceTopology.Topology, topology))
            throw new ArgumentException("Reference normal scratch must be built for this topology instance.", nameof(scratch));
        for (int v = 0; v < topology.VertexCount; v++)
            if (!CoverageRefinement.Finite(scratch.Before[v]) || !CoverageRefinement.Finite(scratch.After[v].Position))
                return Refuse(topology, CoverageFailureCode.UnsupportedTransform, out failure);
        // Finish face validation before publishing any attribute. Command signs reduce in triangle order.
        Span<int> signs = stackalloc int[CoverageScratch.MaxCommands];
        signs.Clear();
        for (int t = 0; t < topology.TriangleCount; t++)
        {
            if (Changed(topology, scratch, t) && !TryBasis(topology, scratch, t, out _, out var code))
                return Refuse(topology, code, out failure);
            var face = topology.Triangles[t];
            int slot = face.RegionIndex < 0 || scratch.ActiveCommands == 0 ? -1 : scratch.RegionCommands[face.RegionIndex];
            if (slot < 0) continue;
            var frame = scratch.Frames[slot];
            Vector3 a = CoverageReferenceMath.Point(scratch.After[(int)topology.Indices[t * 3]].Position, frame.HostToCoverage),
                b = CoverageReferenceMath.Point(scratch.After[(int)topology.Indices[(t * 3) + 1]].Position, frame.HostToCoverage),
                c = CoverageReferenceMath.Point(scratch.After[(int)topology.Indices[(t * 3) + 2]].Position, frame.HostToCoverage);
            Vector3 u = b - a, v = c - a;
            float area = (u.X * v.Y) - (u.Y * v.X);
            float scale = MathF.Sqrt(CoverageReferenceMath.Dot3(u, u)) * MathF.Sqrt(CoverageReferenceMath.Dot3(v, v));
            if (!CoverageRefinement.Finite(a) || !CoverageRefinement.Finite(b) || !CoverageRefinement.Finite(c)
                || !float.IsFinite(area) || !float.IsFinite(scale))
                return Refuse(topology, CoverageFailureCode.UnsupportedTransform, out failure);
            // The magnitude threshold mirrors the double guard. The discrete orientation uses the B27 exact predicate.
            if (!(MathF.Abs(area) > 1e-9f * scale)) return Refuse(topology, CoverageFailureCode.FoldedPatch, out failure);
            var orientation = CoverageReferencePredicates.Edge(a, b, c);
            if (!orientation.IsFinite) return Refuse(topology, CoverageFailureCode.UnsupportedTransform, out failure);
            if (orientation.Sign == 0 || signs[slot] != 0 && signs[slot] != orientation.Sign)
                return Refuse(topology, CoverageFailureCode.FoldedPatch, out failure);
            signs[slot] = orientation.Sign;
        }
        var rows = scratch.ReferenceTopology;
        for (int vertex = 0; vertex < topology.VertexCount; vertex++)
        {
            var before = scratch.Before[vertex];
            var result = before;
            if (!Moved(scratch, vertex)) { scratch.Published[vertex] = result; continue; }
            result.Position = scratch.After[vertex].Position;
            int shading = topology.Vertices[vertex].ShadingGroup;
            if (before.Normal != Vector3.Zero)
            {
                if (!Gather(topology, scratch, rows.ShadingCorners.AsSpan(rows.ShadingCornerOffsets[shading],
                    rows.ShadingCornerOffsets[shading + 1] - rows.ShadingCornerOffsets[shading]), false, out var normal, out var code))
                    return Refuse(topology, code, out failure);
                result.Normal = normal;
            }
            if (before.Tangent.X != 0 || before.Tangent.Y != 0 || before.Tangent.Z != 0)
            {
                if (!Gather(topology, scratch, rows.VertexCorners.AsSpan(rows.VertexFaceOffsets[vertex],
                    rows.VertexFaceOffsets[vertex + 1] - rows.VertexFaceOffsets[vertex]), true, out var tangent, out var code))
                    return Refuse(topology, code, out failure);
                result.Tangent = new(tangent, before.Tangent.W);
            }
            scratch.Published[vertex] = result;
        }
        failure = default;
        return true;
    }

    static bool Gather(CoverageRefinement topology, CoverageReferenceScratch scratch, ReadOnlySpan<int> corners,
        bool tangent, out Vector3 direction, out CoverageFailureCode code)
    {
        Vector3 sum = default;
        foreach (int corner in corners)
        {
            int vertex = (int)topology.Indices[corner];
            if (!Moved(scratch, vertex)) continue;
            var authored = scratch.Before[vertex];
            Vector3 original = tangent ? new(authored.Tangent.X, authored.Tangent.Y, authored.Tangent.Z) : authored.Normal;
            if (original == Vector3.Zero) continue;
            if (!TryBasis(topology, scratch, corner / 3, out var basis, out code)) { direction = default; return false; }
            Vector3 transformed = tangent
                ? Combine(basis.S, CoverageReferenceMath.Dot3(basis.Bu, original), basis.T, CoverageReferenceMath.Dot3(basis.Bv, original),
                    basis.M, CoverageReferenceMath.Dot3(basis.Bn, original))
                : Combine(basis.Au, CoverageReferenceMath.Dot3(basis.U, original), basis.Av, CoverageReferenceMath.Dot3(basis.V, original),
                    basis.An, CoverageReferenceMath.Dot3(basis.N, original));
            if (!Unit(transformed, out var unit, out code)) { direction = default; return false; }
            sum = new(sum.X + unit.X, sum.Y + unit.Y, sum.Z + unit.Z);
            if (!CoverageRefinement.Finite(sum)) { direction = default; code = CoverageFailureCode.UnsupportedTransform; return false; }
        }
        return Unit(sum, out direction, out code);
    }

    static bool TryBasis(CoverageRefinement topology, CoverageReferenceScratch scratch, int triangle,
        out Basis basis, out CoverageFailureCode code)
    {
        basis = default;
        int a = (int)topology.Indices[triangle * 3], b = (int)topology.Indices[(triangle * 3) + 1], c = (int)topology.Indices[(triangle * 3) + 2];
        Vector3 u = scratch.Before[b].Position - scratch.Before[a].Position, v = scratch.Before[c].Position - scratch.Before[a].Position,
            s = scratch.After[b].Position - scratch.After[a].Position, t = scratch.After[c].Position - scratch.After[a].Position;
        Vector3 normal = CoverageReferenceMath.Cross(u, v), finalNormal = CoverageReferenceMath.Cross(s, t);
        if (!Unit(normal, out var n, out code) || !Unit(finalNormal, out var m, out code)) return false;
        float alignment = CoverageReferenceMath.Dot3(normal, finalNormal);
        float beforeDet = CoverageReferenceMath.Dot3(u, CoverageReferenceMath.Cross(v, n)),
            afterDet = CoverageReferenceMath.Dot3(s, CoverageReferenceMath.Cross(t, m));
        float beforeScale = MathF.Sqrt(CoverageReferenceMath.Dot3(u, u) * CoverageReferenceMath.Dot3(v, v)),
            afterScale = MathF.Sqrt(CoverageReferenceMath.Dot3(s, s) * CoverageReferenceMath.Dot3(t, t));
        if (!float.IsFinite(alignment) || !float.IsFinite(beforeDet) || !float.IsFinite(afterDet)
            || !float.IsFinite(beforeScale) || !float.IsFinite(afterScale))
        { code = CoverageFailureCode.UnsupportedTransform; return false; }
        if (!(alignment > 0) || !(beforeDet > 0) || !(afterDet > 0)
            || beforeDet <= beforeScale * 1e-12f || afterDet <= afterScale * 1e-12f)
        { code = CoverageFailureCode.FoldedPatch; return false; }
        Vector3 bu = Divide(CoverageReferenceMath.Cross(v, n), beforeDet), bv = Divide(CoverageReferenceMath.Cross(n, u), beforeDet),
            bn = Divide(normal, beforeDet), au = Divide(CoverageReferenceMath.Cross(t, m), afterDet),
            av = Divide(CoverageReferenceMath.Cross(m, s), afterDet), an = Divide(finalNormal, afterDet);
        if (!CoverageRefinement.Finite(bu) || !CoverageRefinement.Finite(bv) || !CoverageRefinement.Finite(bn)
            || !CoverageRefinement.Finite(au) || !CoverageRefinement.Finite(av) || !CoverageRefinement.Finite(an))
        { code = CoverageFailureCode.UnsupportedTransform; return false; }
        basis = new(u, v, n, s, t, m, bu, bv, bn, au, av, an);
        return true;
    }

    static bool Unit(Vector3 value, out Vector3 unit, out CoverageFailureCode code)
    {
        unit = default;
        float length = MathF.Sqrt(CoverageReferenceMath.Dot3(value, value));
        code = CoverageFailureCode.UnsupportedTransform;
        if (!CoverageRefinement.Finite(value) || !float.IsFinite(length)) return false;
        if (!(length > 0)) { code = CoverageFailureCode.FoldedPatch; return false; }
        unit = Divide(value, length);
        if (!CoverageRefinement.Finite(unit)) return false;
        code = default;
        return true;
    }

    static Vector3 Combine(Vector3 a, float x, Vector3 b, float y, Vector3 c, float z) => new(
        ((a.X * x) + (b.X * y)) + (c.X * z), ((a.Y * x) + (b.Y * y)) + (c.Y * z), ((a.Z * x) + (b.Z * y)) + (c.Z * z));

    static Vector3 Divide(Vector3 value, float divisor) => new(value.X / divisor, value.Y / divisor, value.Z / divisor);

    static bool Moved(CoverageReferenceScratch scratch, int vertex) => scratch.Before[vertex].Position != scratch.After[vertex].Position;

    static bool Changed(CoverageRefinement topology, CoverageReferenceScratch scratch, int triangle) =>
        Moved(scratch, (int)topology.Indices[triangle * 3]) || Moved(scratch, (int)topology.Indices[(triangle * 3) + 1])
        || Moved(scratch, (int)topology.Indices[(triangle * 3) + 2]);

    static bool Refuse(CoverageRefinement topology, CoverageFailureCode code, out CoverageFailure failure)
    { failure = new(code, topology.HostIdentity, 0, 0, 0, 0); return false; }

    readonly record struct Basis(Vector3 U, Vector3 V, Vector3 N, Vector3 S, Vector3 T, Vector3 M,
        Vector3 Bu, Vector3 Bv, Vector3 Bn, Vector3 Au, Vector3 Av, Vector3 An);
}
