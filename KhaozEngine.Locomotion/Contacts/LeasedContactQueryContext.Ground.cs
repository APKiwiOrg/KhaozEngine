using System;
using System.Numerics;

namespace KhaozEngine.Locomotion.Contacts;

internal sealed partial class LeasedContactQueryContext
{
    internal bool ConstrainGroundDescent(Vector3 from, Vector3 to, Vector3 supportNormal,
        float footRadius, float cosMaxSlope, out Vector2 move)
    {
        move = default;
        if (_waterMode is not (WaterTraversalMode.WadeOnly or WaterTraversalMode.DryOnly) ||
            to.Y >= from.Y || supportNormal.Y <= 0)
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
        Span<Vector3> horizontal = stackalloc Vector3[MovementQueryLease.MaxDomainContacts];
        for (int i = 0; i < count; i++)
        {
            Vector3 normal = normals[i];
            double approach = -((double)normal.X * delta.X + (double)normal.Y * delta.Y + (double)normal.Z * delta.Z) / length;
            if (approach > 0) retreat = Math.Max(retreat, 2d * MovementQueryLease.CoverageSkinMetres / approach);
            horizontal[i] = new(normal.X - normal.Y * supportNormal.X / supportNormal.Y, 0,
                normal.Z - normal.Y * supportNormal.Z / supportNormal.Y);
            float across = horizontal[i].Length();
            if (!(across > 0))
            {
                // Flat treads carry no grade. Component seats locate the height discontinuity
                // that made this down pass forbidden. The retry proves its shell, route and seat.
                if (!FlatDescentConstraint(from, to, footRadius, cosMaxSlope, out horizontal[i])) return false;
                across = horizontal[i].Length();
            }
            if (!float.IsFinite(across)) return false;
            horizontal[i] /= across;
        }
        float fraction = (float)(Math.Max(0, hit.Distance - retreat) / length);
        Vector3 desired = new(to.X - from.X, 0, to.Z - from.Z);
        Vector3 prefix = desired * fraction;
        Require(MovementSegmentProof.Project(desired - prefix, horizontal[..count], out Vector3 tangent));
        Vector3 proposal = prefix + tangent;
        move = new(proposal.X, proposal.Z);
        return move != new Vector2(desired.X, desired.Z);
    }

    bool FlatDescentConstraint(Vector3 from, Vector3 to, float footRadius, float cosMaxSlope, out Vector3 normal)
    {
        float dx = to.X - from.X, dz = to.Z - from.Z;
        float x = dx == 0 ? 0 : Drop(new(to.X, from.Y, from.Z)) / dx;
        float z = dz == 0 ? 0 : Drop(new(from.X, from.Y, to.Z)) / dz;
        normal = new(-x, 0, -z);
        return normal.LengthSquared() > 0;

        float Drop(Vector3 feet)
        {
            SupportSample support = FindSupport(new(new(feet.X, feet.Z), from.Y,
                footRadius, _tuning.StepHeight, _tuning.StepHeight, cosMaxSlope));
            return support.Status == SupportStatus.Walkable &&
                (double)from.Y - support.Height > ShellMotion.ContactSkin + support.HeightError
                ? from.Y - support.Height : 0;
        }
    }
}
