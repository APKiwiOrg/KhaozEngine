using System;
using System.Numerics;

namespace KhaozEngine.Locomotion.Contacts;

internal sealed partial class LeasedContactQueryContext
{
    internal bool ConstrainGroundDescent(Vector3 from, Vector3 to, Vector3 supportNormal, out Vector2 move)
    {
        move = default;
        if (_waterMode != WaterTraversalMode.WadeOnly || to.Y >= from.Y || supportNormal.Y <= 0)
            return false;
        MovementBodyQuery body = Body(from);
        Vector3 delta = Body(to).Centre - body.Centre;
        Require(_lease.CheckContactBodyPath(body, delta));
        Span<Vector3> normals = stackalloc Vector3[MovementQueryLease.MaxDomainContacts];
        Require(MovementSegmentProof.Medium(body, delta, _lease, _waterMode, _tuning.SwimEnterDepthFraction,
            _boundary, normals, out int count, out var hit));
        if (!hit.Blocked || hit.InitialOverlap) return false;

        // The full body earns the coverage fact. Pull its depth constraint back to horizontal intent
        // using the selected support normal, then prove the resulting route and seat afresh.
        double length = Math.Sqrt((double)delta.X * delta.X + (double)delta.Y * delta.Y + (double)delta.Z * delta.Z);
        double retreat = 0;
        bool depthBoundary = false;
        Span<Vector3> horizontal = stackalloc Vector3[MovementQueryLease.MaxDomainContacts];
        for (int i = 0; i < count; i++)
        {
            Vector3 normal = normals[i];
            depthBoundary |= normal == Vector3.UnitY;
            double approach = -((double)normal.X * delta.X + (double)normal.Y * delta.Y + (double)normal.Z * delta.Z) / length;
            if (approach > 0) retreat = Math.Max(retreat, 2d * MovementQueryLease.CoverageSkinMetres / approach);
            horizontal[i] = new(normal.X - normal.Y * supportNormal.X / supportNormal.Y, 0,
                normal.Z - normal.Y * supportNormal.Z / supportNormal.Y);
            float across = horizontal[i].Length();
            if (!(across > 0) || !float.IsFinite(across)) return false;
            horizontal[i] /= across;
        }
        if (!depthBoundary) return false;
        float fraction = (float)(Math.Max(0, hit.Distance - retreat) / length);
        Vector3 desired = new(to.X - from.X, 0, to.Z - from.Z);
        Vector3 prefix = desired * fraction;
        Require(MovementSegmentProof.Project(desired - prefix, horizontal[..count], out Vector3 tangent));
        Vector3 proposal = prefix + tangent;
        move = new(proposal.X, proposal.Z);
        return move != new Vector2(desired.X, desired.Z);
    }
}
