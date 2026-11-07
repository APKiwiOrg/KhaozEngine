using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

/// <summary>Certifies one non-overlapping incident surface before the analytic field selects its member.</summary>
internal static class DescentIncidentFan
{
    internal static int Select(DescentFace[] faces, List<int> incident, IMovementAnalyticSupportSource source, Vector2 axis)
    {
        if (incident.Count == 0) throw Refuse();
        if (incident.Count == 1 && !faces[incident[0]].Analytic) return incident[0];
        foreach (int f in incident)
            if (!faces[f].Analytic || faces[f].CanonicalOwner != faces[incident[0]].CanonicalOwner) throw Refuse();

        // Every pair matters. Two later members can overlap without overlapping the first triangle.
        for (int i = 0; i < incident.Count; i++)
            for (int j = i + 1; j < incident.Count; j++)
            {
                var a = faces[incident[i]]; var b = faces[incident[j]];
                if (a.Key == b.Key || DescentGeometry.Area2(DescentGeometry.Overlap(
                    DescentGeometry.Project(a.Points), DescentGeometry.Project(b.Points))).Sign > 0) throw Refuse();
            }

        var reached = new bool[incident.Count];
        reached[0] = true;
        int count = 1;
        bool changed;
        do
        {
            changed = false;
            for (int i = 0; i < incident.Count; i++)
            {
                if (!reached[i]) continue;
                for (int j = 0; j < incident.Count; j++)
                {
                    if (reached[j]) continue;
                    var a = faces[incident[i]]; var b = faces[incident[j]];
                    if (!source.AreAdjacent(a.Key, b.Key) || !SharedEdge(a.Points, b.Points, axis)) continue;
                    reached[j] = true; count++; changed = true;
                }
            }
        } while (changed && count < incident.Count);
        if (count != incident.Count || source.Describe(axis, out var named) != MovementAvailability.Known) throw Refuse();
        foreach (int f in incident)
            if (faces[f].Key == named.Key && faces[f].CanonicalOwner == named.Owner && SameTriangle(faces[f].Points, named)) return f;
        throw Refuse();
    }

    static bool SameTriangle(ReadOnlySpan<PhysicsEvidencePoint> points, MovementSurfaceFeature feature)
    {
        foreach (var p in points)
            if (!Same(p, feature.A) && !Same(p, feature.B) && !Same(p, feature.C)) return false;
        return points.Length == 3;
    }
    static bool Same(PhysicsEvidencePoint p, Vector3 v) => p.X == v.X && p.Y == v.Y && p.Z == v.Z;

    static bool SharedEdge(ReadOnlySpan<PhysicsEvidencePoint> a, ReadOnlySpan<PhysicsEvidencePoint> b, Vector2 axis)
    {
        for (int i = 0; i < a.Length; i++)
            for (int j = 0; j < b.Length; j++)
                if (SharedSegment(a[i], a[(i + 1) % a.Length], b[j], b[(j + 1) % b.Length], axis)) return true;
        return false;
    }

    static bool SharedSegment(PhysicsEvidencePoint a, PhysicsEvidencePoint b, PhysicsEvidencePoint c, PhysicsEvidencePoint d, Vector2 axis)
    {
        var x = new DescentNumber(b.X) - new DescentNumber(a.X);
        var z = new DescentNumber(b.Z) - new DescentNumber(a.Z);
        var y = new DescentNumber(b.Y) - new DescentNumber(a.Y);
        if (x.Sign == 0 && z.Sign == 0) return false;
        bool OnLine(PhysicsEvidencePoint p)
        {
            var px = new DescentNumber(p.X) - new DescentNumber(a.X);
            var pz = new DescentNumber(p.Z) - new DescentNumber(a.Z);
            var py = new DescentNumber(p.Y) - new DescentNumber(a.Y);
            return (x * pz - z * px).Sign == 0 &&
                (x.Sign != 0 ? x * py - y * px : z * py - y * pz).Sign == 0;
        }
        if (!OnLine(c) || !OnLine(d) ||
            (x * (new DescentNumber(axis.Y) - new DescentNumber(a.Z)) - z * (new DescentNumber(axis.X) - new DescentNumber(a.X))).Sign != 0) return false;
        var av = new DescentNumber(x.Sign != 0 ? a.X : a.Z); var bv = new DescentNumber(x.Sign != 0 ? b.X : b.Z);
        var cv = new DescentNumber(x.Sign != 0 ? c.X : c.Z); var dv = new DescentNumber(x.Sign != 0 ? d.X : d.Z);
        var start = Max(Min(av, bv), Min(cv, dv)); var end = Min(Max(av, bv), Max(cv, dv));
        var at = new DescentNumber(x.Sign != 0 ? axis.X : axis.Y);
        return start < end && start <= at && at <= end;
    }
    static DescentNumber Min(DescentNumber a, DescentNumber b) => a < b ? a : b;
    static DescentNumber Max(DescentNumber a, DescentNumber b) => a > b ? a : b;
    static PhysicsNavDescentEvidenceException Refuse() => new(PhysicsNavDescentEvidenceReason.RawAssociationAmbiguous, PhysicsEvidenceStatus.Unavailable);
}
