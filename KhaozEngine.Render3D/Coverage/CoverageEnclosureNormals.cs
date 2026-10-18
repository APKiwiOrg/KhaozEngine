using System;
using System.Numerics;
using I = KhaozEngine.Render3D.CoverageFloatInterval;
using F = KhaozEngine.Render3D.CoverageFloatIntervals;
using V = KhaozEngine.Render3D.CoverageVertexEnclosure;
using M = KhaozEngine.Render3D.CoverageEnclosureMath;

namespace KhaozEngine.Render3D;

internal readonly record struct CoverageEnclosureBasis(V U, V V, V N, V S, V T, V M, V Bu, V Bv, V Bn, V Au, V Av, V An);

/// <summary>Normal arithmetic is a position-selection guard even though it never displaces a vertex.</summary>
internal static class CoverageEnclosureNormals
{
    internal static bool Evaluate(CoverageRefinement topology, ReadOnlySpan<CoverageCommandPose> commands,
        CoverageReferenceScratch reference, CoverageReferenceEnclosureScratch scratch)
    {
        Span<int> signs = stackalloc int[CoverageScratch.MaxCommands];
        signs.Clear();
        for (int t = 0; t < topology.TriangleCount; t++)
        {
            int a = (int)topology.Indices[t * 3], b = (int)topology.Indices[t * 3 + 1], c = (int)topology.Indices[t * 3 + 2];
            if (Moved(a, topology, commands, reference, scratch) || Moved(b, topology, commands, reference, scratch) || Moved(c, topology, commands, reference, scratch))
            {
                if (!Basis(a, b, c, scratch, out var basis)) { scratch.Unbounded(CoverageEnclosureSite.Normals, t); return false; }
                scratch.Bases[t] = basis;
            }
            int region = topology.Triangles[t].RegionIndex;
            int slot = region < 0 || commands.Length == 0 ? -1 : reference.RegionCommands[region];
            if (slot < 0) continue;
            var frame = reference.Frames[slot];
            V x = M.Transform(scratch.After[a], frame.HostToCoverage), y = M.Transform(scratch.After[b], frame.HostToCoverage),
                z = M.Transform(scratch.After[c], frame.HostToCoverage), u = M.Subtract(y, x), v = M.Subtract(z, x);
            I area = F.Subtract(F.Multiply(u.X, v.Y), F.Multiply(u.Y, v.X)), scale = F.Multiply(M.Length(u), M.Length(v));
            var orientation = CoverageEnclosurePredicates.Edge(x, y, z);
            if (!area.IsFinite || !scale.IsFinite || !orientation.Value.IsFinite)
            { scratch.Unbounded(CoverageEnclosureSite.Normals, t); return false; }
            if (F.Abs(area).Min <= F.Multiply(F.Point(1e-9f), scale).Max || orientation.Zero || orientation.MinSign != orientation.MaxSign)
                scratch.MayRefuse = true;
            else
            {
                if (signs[slot] != 0 && signs[slot] != orientation.MinSign) scratch.MayRefuse = true;
                signs[slot] = orientation.MinSign;
            }
        }
        for (int vertex = 0; vertex < topology.VertexCount; vertex++)
        {
            if (!Moved(vertex, topology, commands, reference, scratch)) continue;
            int shading = topology.Vertices[vertex].ShadingGroup;
            if (!Absent(scratch.Normals[vertex]) && !Gather(scratch.Rows.ShadingCorners.AsSpan(scratch.Rows.ShadingCornerOffsets[shading],
                scratch.Rows.ShadingCornerOffsets[shading + 1] - scratch.Rows.ShadingCornerOffsets[shading]), false, topology, commands, reference, scratch))
            { scratch.Unbounded(CoverageEnclosureSite.Normals, vertex); return false; }
            if (!Absent(scratch.Tangents[vertex]) && !Gather(scratch.Rows.VertexCorners.AsSpan(scratch.Rows.VertexFaceOffsets[vertex],
                scratch.Rows.VertexFaceOffsets[vertex + 1] - scratch.Rows.VertexFaceOffsets[vertex]), true, topology, commands, reference, scratch))
            { scratch.Unbounded(CoverageEnclosureSite.Normals, vertex); return false; }
        }
        return true;
    }

    static bool Moved(int v, CoverageRefinement topology, ReadOnlySpan<CoverageCommandPose> commands,
        CoverageReferenceScratch reference, CoverageReferenceEnclosureScratch scratch)
    {
        int representative = scratch.Representatives[topology.Vertices[v].PositionGroup];
        if (representative < 0) return false;
        int slot = reference.RegionCommands[topology.Vertices[representative].RegionIndex];
        return commands[reference.Frames[slot].Command].TransitionWeight != 0;
    }

    static bool Gather(ReadOnlySpan<int> corners, bool tangent, CoverageRefinement topology,
    ReadOnlySpan<CoverageCommandPose> commands, CoverageReferenceScratch reference, CoverageReferenceEnclosureScratch scratch)
    {
        V sum = M.Point(Vector3.Zero);
        foreach (int corner in corners)
        {
            int vertex = (int)topology.Indices[corner];
            if (!Moved(vertex, topology, commands, reference, scratch)) continue;
            V authored = tangent ? scratch.Tangents[vertex] : scratch.Normals[vertex];
            if (Absent(authored)) continue;
            var b = scratch.Bases[corner / 3];
            V transformed = tangent
                ? Combine(b.S, M.Dot(b.Bu, authored), b.T, M.Dot(b.Bv, authored), b.M, M.Dot(b.Bn, authored))
                : Combine(b.Au, M.Dot(b.U, authored), b.Av, M.Dot(b.V, authored), b.An, M.Dot(b.N, authored));
            if (!Unit(transformed, scratch, out var unit)) return false;
            sum = M.Add(sum, unit);
        }
        return Unit(sum, scratch, out _);
    }

    static bool Basis(int a, int b, int c, CoverageReferenceEnclosureScratch scratch, out CoverageEnclosureBasis basis)
    {
        basis = default;
        V u = M.Subtract(scratch.Before[b], scratch.Before[a]), v = M.Subtract(scratch.Before[c], scratch.Before[a]),
            s = M.Subtract(scratch.After[b], scratch.After[a]), t = M.Subtract(scratch.After[c], scratch.After[a]);
        V normal = M.Cross(u, v), final = M.Cross(s, t);
        if (!Unit(normal, scratch, out V n) || !Unit(final, scratch, out V m)) return false;
        I alignment = M.Dot(normal, final), before = M.Dot(u, M.Cross(v, n)), after = M.Dot(s, M.Cross(t, m));
        I beforeScale = M.Root(F.Multiply(M.Dot(u, u), M.Dot(v, v))), afterScale = M.Root(F.Multiply(M.Dot(s, s), M.Dot(t, t)));
        if (!alignment.IsFinite || !before.IsFinite || !after.IsFinite || !beforeScale.IsFinite || !afterScale.IsFinite) return false;
        if (alignment.Min <= 0 || before.Min <= 0 || after.Min <= 0
            || before.Min <= F.Multiply(beforeScale, F.Point(1e-12f)).Max || after.Min <= F.Multiply(afterScale, F.Point(1e-12f)).Max)
            scratch.MayRefuse = true;
        basis = new(u, v, n, s, t, m, M.Divide(M.Cross(v, n), before), M.Divide(M.Cross(n, u), before),
            M.Divide(normal, before), M.Divide(M.Cross(t, m), after), M.Divide(M.Cross(m, s), after), M.Divide(final, after));
        return M.Finite(basis.Bu) && M.Finite(basis.Bv) && M.Finite(basis.Bn)
            && M.Finite(basis.Au) && M.Finite(basis.Av) && M.Finite(basis.An);
    }

    static bool Unit(V value, CoverageReferenceEnclosureScratch scratch, out V unit)
    {
        I length = M.Length(value);
        if (length.Min <= 0) scratch.MayRefuse = true;
        unit = M.Divide(value, length);
        return M.Finite(unit);
    }

    static bool Absent(V v) => v == M.Point(Vector3.Zero);
    static V Combine(V a, I x, V b, I y, V c, I z) => M.Add(M.Add(M.Multiply(a, x), M.Multiply(b, y)), M.Multiply(c, z));
}
