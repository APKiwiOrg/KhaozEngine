using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Updates only displaced vertices through per-face tangent-plane deformation gradients.
/// Matching authored normal fields share accumulation across welded material and UV seams. Hard normals remain
/// separate. Tangent accumulation retains each indexed UV neighborhood and its original handedness.</summary>
public static class CoverageSurfaceNormals
{
    /// <summary>Requires exact topology-sized, disjoint before/after buffers and scratch for this topology.
    /// Degenerate or uncertifiable gradients refuse without writing after. Undisplaced vertices copy before exactly.
    /// Tangents use the forward gradient, normals its inverse transpose. Tangent handedness and no-TBN survive.</summary>
    public static bool Update(CoverageRefinement topology, ReadOnlySpan<ModelVertex> before,
        Span<ModelVertex> after, CoverageScratch scratch, out CoverageFailure failure)
    {
        ArgumentNullException.ThrowIfNull(topology); ArgumentNullException.ThrowIfNull(scratch);
        if (before.Length != topology.VertexCount || after.Length != topology.VertexCount || before.Overlaps(after))
            throw new ArgumentException("Normal deformation requires exact topology lengths and disjoint buffers.");
        if (!ReferenceEquals(scratch.Topology, topology))
        {
            failure = new(CoverageFailureCode.CapacityExceeded, topology.HostIdentity, 0, 0, topology.VertexCount, scratch.VertexCapacity);
            return false;
        }
        var normals = scratch.NormalSums.AsSpan(); var tangents = scratch.TangentSums.AsSpan();
        normals.Clear(); tangents.Clear();
        for (int i = 0; i < before.Length; i++)
        {
            if (!CoverageRefinement.Finite(before[i]) || !CoverageRefinement.Finite(after[i].Position)) return Refuse(topology, out failure);
            scratch.Staged[i] = before[i];
            if (before[i].Position != after[i].Position) scratch.Staged[i].Position = after[i].Position;
        }
        var indices = topology.Indices;
        for (int i = 0; i < indices.Length; i += 3)
        {
            int a = (int)indices[i], b = (int)indices[i + 1], c = (int)indices[i + 2];
            V u = V.From(before[b].Position) - V.From(before[a].Position), v = V.From(before[c].Position) - V.From(before[a].Position);
            V s = V.From(after[b].Position) - V.From(after[a].Position), t = V.From(after[c].Position) - V.From(after[a].Position);
            V normal = V.Cross(u, v), finalNormal = V.Cross(s, t);
            if (!normal.TryUnit(out V n) || !finalNormal.TryUnit(out V m)) return Refuse(topology, out failure);
            // Dual bases avoid an ill-scaled float Matrix4x4 inverse and retain full source-float differences.
            double beforeDet = V.Dot(u, V.Cross(v, n)), afterDet = V.Dot(s, V.Cross(t, m));
            if (!double.IsFinite(beforeDet) || !double.IsFinite(afterDet) || beforeDet <= 0 || afterDet <= 0)
                return Refuse(topology, out failure);
            V bu = V.Cross(v, n) / beforeDet, bv = V.Cross(n, u) / beforeDet, bn = V.Cross(u, v) / beforeDet;
            V au = V.Cross(t, m) / afterDet, av = V.Cross(m, s) / afterDet, an = V.Cross(s, t) / afterDet;
            // A near-collinear basis cannot safely supply a deformation gradient.
            if (beforeDet <= Math.Sqrt(u.LengthSquared * v.LengthSquared) * 1e-12
                || afterDet <= Math.Sqrt(s.LengthSquared * t.LengthSquared) * 1e-12) return Refuse(topology, out failure);
            for (int corner = 0; corner < 3; corner++)
            {
                int index = (int)indices[i + corner];
                if (before[index].Position == after[index].Position) continue;
                V authored = V.From(before[index].Normal);
                V transformed = au * V.Dot(u, authored) + av * V.Dot(v, authored) + an * V.Dot(n, authored);
                if (authored.LengthSquared != 0)
                {
                    if (!transformed.TryUnit(out var unit)) return Refuse(topology, out failure);
                    normals[topology.Vertices[index].ShadingGroup] += unit.Float;
                }
                var original = before[index].Tangent;
                V direction = new(original.X, original.Y, original.Z);
                if (direction.LengthSquared == 0) continue;
                V forward = s * V.Dot(bu, direction) + t * V.Dot(bv, direction) + m * V.Dot(bn, direction);
                if (!forward.TryUnit(out var tangent)) return Refuse(topology, out failure);
                tangents[index] += tangent.Float;
            }
        }
        for (int i = 0; i < before.Length; i++)
        {
            if (before[i].Position == after[i].Position) continue;
            if (before[i].Normal != Vector3.Zero)
            {
                if (!V.From(normals[topology.Vertices[i].ShadingGroup]).TryUnit(out var n)) return Refuse(topology, out failure);
                scratch.Staged[i].Normal = n.Float;
            }
            var tangent = before[i].Tangent;
            if (tangent.X != 0 || tangent.Y != 0 || tangent.Z != 0)
            {
                if (!V.From(tangents[i]).TryUnit(out var t)) return Refuse(topology, out failure);
                scratch.Staged[i].Tangent = new(t.Float, tangent.W);
            }
        }
        scratch.Staged.AsSpan().CopyTo(after);
        failure = default; return true;
    }

    static bool Refuse(CoverageRefinement topology, out CoverageFailure failure)
    {
        failure = new(CoverageFailureCode.FoldedPatch, topology.HostIdentity, 0, 0, 0, 0); return false;
    }

    readonly record struct V(double X, double Y, double Z)
    {
        internal static V From(Vector3 v) => new(v.X, v.Y, v.Z);
        internal Vector3 Float => new((float)X, (float)Y, (float)Z);
        internal double LengthSquared => Dot(this, this);
        internal bool TryUnit(out V unit)
        {
            double length = Math.Sqrt(LengthSquared);
            unit = this / length;
            return length > 0 && double.IsFinite(length) && double.IsFinite(unit.X) && double.IsFinite(unit.Y) && double.IsFinite(unit.Z);
        }
        internal static double Dot(V a, V b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        internal static V Cross(V a, V b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public static V operator +(V a, V b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V operator -(V a, V b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V operator *(V a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public static V operator /(V a, double s) => a * (1 / s);
    }
}
