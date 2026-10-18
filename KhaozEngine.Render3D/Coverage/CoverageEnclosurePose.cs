using System;
using System.Numerics;
using I = KhaozEngine.Render3D.CoverageFloatInterval;
using F = KhaozEngine.Render3D.CoverageFloatIntervals;
using V = KhaozEngine.Render3D.CoverageVertexEnclosure;
using M = KhaozEngine.Render3D.CoverageEnclosureMath;
using S = KhaozEngine.Render3D.CoverageEnclosureSupport;

namespace KhaozEngine.Render3D;

/// <summary>Skinning, canonical interpolation and pointwise full-target conformation.</summary>
internal static class CoverageEnclosurePose
{
    internal static bool Prepare(CoverageRefinement topology, ReadOnlySpan<Matrix4x4> palette, CoverageReferenceEnclosureScratch scratch)
    {
        var source = topology.Source;
        for (int v = 0; v < source.Positions.Length; v++)
        {
            if (source.SkinVertices.IsEmpty)
            {
                scratch.Source[v] = M.Point(source.Positions[v]);
                scratch.SourceNormals[v] = scratch.SourceTangents[v] = M.Point(Vector3.Zero);
            }
            else
            {
                var vertex = source.SkinVertices[v];
                scratch.Source[v] = M.Skin(vertex, palette);
                V normal = M.Skin(vertex, palette, true, vertex.Normal);
                scratch.SourceNormals[v] = SkinDirection(normal, vertex.Normal);
                Vector3 tangent = new(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z);
                I squared = M.Dot(M.Point(tangent), M.Point(tangent));
                scratch.SourceTangents[v] = squared.Max <= 1e-12f ? M.Point(Vector3.Zero)
                    : SkinDirection(M.Skin(vertex, palette, true, tangent), tangent);
                if (squared.Min <= 1e-12f) scratch.SourceTangents[v] = M.Hull(scratch.SourceTangents[v], M.Point(Vector3.Zero));
            }
            if (!M.Finite(scratch.Source[v]) || !M.Finite(scratch.SourceNormals[v]) || !M.Finite(scratch.SourceTangents[v]))
            { scratch.Unbounded(CoverageEnclosureSite.Skinning, v); return false; }
        }
        for (int g = 0; g < scratch.Groups.Length; g++) scratch.Groups[g] = M.Interpolate(scratch.Rows.Recipes[g], scratch.Source);
        for (int v = 0; v < topology.VertexCount; v++)
        {
            var record = topology.Vertices[v];
            if (record.SourceVertex >= 0)
            {
                scratch.Before[v] = scratch.Source[record.SourceVertex];
                scratch.Normals[v] = scratch.SourceNormals[record.SourceVertex];
                scratch.Tangents[v] = scratch.SourceTangents[record.SourceVertex];
            }
            else
            {
                scratch.Before[v] = scratch.Groups[record.PositionGroup];
                var t = source.Triangles[record.SourceTriangle];
                scratch.Normals[v] = Direction(scratch.SourceNormals[t.A], scratch.SourceNormals[t.B], scratch.SourceNormals[t.C], record, scratch);
                scratch.Tangents[v] = Direction(scratch.SourceTangents[t.A], scratch.SourceTangents[t.B], scratch.SourceTangents[t.C], record, scratch);
            }
            if (!M.Finite(scratch.Before[v]) || !M.Finite(scratch.Normals[v]) || !M.Finite(scratch.Tangents[v]))
            { scratch.Unbounded(CoverageEnclosureSite.Interpolation, v); return false; }
        }
        return true;
    }

    static V SkinDirection(V value, Vector3 fallback)
    {
        I length = M.Length(value);
        if (!length.IsFinite) return M.Unbounded;
        if (length.Max <= 1e-8f) return M.Point(fallback);
        V unit = M.Divide(value, new(Math.Max(length.Min, 1e-8f), length.Max));
        return length.Min <= 1e-8f ? M.Hull(unit, M.Point(fallback)) : unit;
    }

    static V Direction(V a, V b, V c, in CoverageRefinedVertex record, CoverageReferenceEnclosureScratch scratch)
    {
        V zero = M.Point(Vector3.Zero);
        // The absent-field path is exact: products and sums of finite zeros are zeros under every backend.
        if (a == zero && b == zero && c == zero) return zero;
        V x = M.Multiply(a, F.Point(record.A)), y = M.Multiply(b, F.Point(record.B)), z = M.Multiply(c, F.Point(record.C));
        V sum = M.Add(M.Add(x, y), z);
        V magnitude = M.Add(M.Add(Abs(x), Abs(y)), Abs(z));
        I length = M.Length(sum), bound = F.Multiply(M.Dot(magnitude, magnitude), F.Point(1f / (1L << 50)));
        if (!length.IsFinite || !bound.IsFinite) return M.Unbounded;
        if (M.Dot(sum, sum).Min <= bound.Max) scratch.MayRefuse = true;
        return M.Divide(sum, length);
    }

    static V Abs(V v) => new(F.Abs(v.X), F.Abs(v.Y), F.Abs(v.Z));

    internal static bool Conform(CoverageRefinement topology, ReadOnlySpan<CoverageCommandPose> commands,
        CoverageReferenceScratch reference, CoverageReferenceEnclosureScratch scratch)
    {
        for (int g = 0; g < scratch.Groups.Length; g++) scratch.Groups[g] = scratch.Before[scratch.Rows.GroupRepresentatives[g]];
        for (int v = 0; v < topology.VertexCount; v++)
        {
            var r = topology.Vertices[v];
            if (r.Strength > 0 && r.RegionIndex >= 0 && reference.RegionCommands[r.RegionIndex] >= 0 && scratch.Representatives[r.PositionGroup] < 0)
                scratch.Representatives[r.PositionGroup] = v;
        }
        for (int v = 0; v < topology.VertexCount; v++)
        {
            var r = topology.Vertices[v];
            int g = r.PositionGroup;
            if (scratch.Representatives[g] != v) continue;
            var frame = reference.Frames[reference.RegionCommands[r.RegionIndex]];
            var command = commands[frame.Command];
            V before = scratch.Before[v], q = M.Transform(before, frame.HostToCoverage);
            bool core = scratch.Rows.CoreFaceOffsets[g] != scratch.Rows.CoreFaceOffsets[g + 1];
            Caps(q, command, frame, scratch);
            bool hit = S.Query(command.Support.ReferenceData, q, out I height, out bool certain);
            if (hit && !height.IsFinite) { scratch.Unbounded(CoverageEnclosureSite.QueryHeight, v); return false; }
            I full = default;
            bool any = false;
            if (hit) S.Join(ref full, ref any, Request(height, F.Point(1)));
            if (!certain)
            {
                if (core) scratch.MayRefuse = true;
                else
                {
                    S.Boundary(command.Support.ReferenceData, q, out I boundaryHeight, out I distance);
                    if (!boundaryHeight.IsFinite || !distance.IsFinite)
                    { scratch.Unbounded(CoverageEnclosureSite.Boundary, v); return false; }
                    if (distance.Max >= command.CollarWidth) S.Join(ref full, ref any, F.Point(0));
                    if (distance.Min < command.CollarWidth)
                    {
                        I u = F.Clamp(F.Subtract(F.Point(1), F.Divide(distance, F.Point(command.CollarWidth))), 0, 1);
                        I spatial = F.Multiply(F.Multiply(u, u), F.Subtract(F.Point(3), F.Multiply(F.Point(2), u)));
                        S.Join(ref full, ref any, Request(boundaryHeight, spatial));
                    }
                }
            }
            if (!any || !full.IsFinite) { scratch.Unbounded(CoverageEnclosureSite.Conformation, v); return false; }
            scratch.Full[g] = full;
            scratch.Groups[g] = Move(before, full, frame.Recess);
            if (full.Contains(0)) scratch.Groups[g] = M.Hull(scratch.Groups[g], before);
            scratch.GroupSites[g] = CoverageEnclosureSite.Conformation;
            I world = F.Multiply(full, F.Point(frame.ZScale));
            if (!world.IsFinite || !M.Finite(scratch.Groups[g])) { scratch.Unbounded(CoverageEnclosureSite.Conformation, v); return false; }
            if (world.Max > command.MaxWorldRecession) scratch.MayRefuse = true;
            Caps(M.Transform(scratch.Groups[g], frame.HostToCoverage), command, frame, scratch);
            I Request(I h, I spatial)
            {
                I target = F.Subtract(h, F.Point(frame.ClearanceCoverage));
                I request = F.Max(F.Point(0), F.Subtract(q.Z, target));
                return F.Multiply(F.Multiply(F.Point(core ? 1 : r.Strength), spatial), request);
            }
        }
        for (int v = 0; v < topology.VertexCount; v++)
        {
            int g = topology.Vertices[v].PositionGroup;
            scratch.After[v] = scratch.Representatives[g] < 0 ? scratch.Before[v] : scratch.Groups[g];
        }
        return true;
    }

    internal static V Move(V before, I scalar, Vector3 recess) => M.Subtract(before, M.Multiply(M.Point(recess), scalar));

    internal static void Caps(V q, in CoverageCommandPose command, in CoverageReferenceFrame frame, CoverageReferenceEnclosureScratch scratch)
    {
        I lower = F.Add(F.Point(command.MinZ), F.Point(frame.CapMargin)), upper = F.Subtract(F.Point(command.MaxZ), F.Point(frame.CapMargin));
        if (!M.Finite(q) || !lower.IsFinite || !upper.IsFinite || q.Z.Min < lower.Max || q.Z.Max > upper.Min) scratch.MayRefuse = true;
    }
}
