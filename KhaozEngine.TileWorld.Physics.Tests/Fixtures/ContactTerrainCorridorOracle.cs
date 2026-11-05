using System;
using System.Collections.Generic;
using KhaozEngine.Movement;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

// Frozen pre-lookup obstruction predicate for #1697. The all-face group scan stays test-only.
internal static class ContactTerrainCorridorOracle
{
    internal static bool CertifyColumn(PhysicsNavDescentEvidence evidence, int x, int z,
        double lower, double upper, double bodyHeight, NavAreaFilter areas)
    {
        if (evidence.Coverage(x, z) != DescentCoverage.Complete) return false;
        var o = evidence.Options;
        double left = o.MinX + x * (double)o.CellSize, bottom = o.MinZ + z * (double)o.CellSize;
        var ground = new List<(int Face, List<DescentPoint> Polygon)>();
        DescentNumber total = default;
        string? source = null;
        DescentNumber minimumBed = default; bool hasBed = false;
        foreach (var entry in evidence.Column(x, z))
        {
            var face = evidence.Face(entry.Face);
            if (!face.Analytic || face.Normal.Y <= 0) continue;
            if (!areas.Allows(entry.Areas)) return false;
            if (source is not null && source != face.Key.Source) return false;
            source = face.Key.Source;
            var clipped = DescentGeometry.Cell(face.Points, left, bottom, o.CellSize);
            var area = DescentGeometry.Area2(clipped);
            if (area.Sign == 0) continue;
            foreach (var p in clipped)
            {
                var y = DescentGeometry.Height(face.Points, p);
                if (!hasBed || y < minimumBed) { minimumBed = y; hasBed = true; }
                if (y < new DescentNumber(lower) || y > new DescentNumber(upper)) return false;
            }
            foreach (var previous in ground)
                if (DescentGeometry.Area2(DescentGeometry.Overlap(clipped, previous.Polygon)).Sign > 0) return false;
            ground.Add((entry.Face, clipped));
            total += area;
        }
        if (!total.Equals(new DescentNumber(2) * new DescentNumber(o.CellSize) * new DescentNumber(o.CellSize))) return false;
        foreach (var entry in evidence.Column(x, z))
        {
            var face = evidence.Face(entry.Face);
            if (face.Analytic) continue;
            if (face.Points.Length < 3) return false;
            if (face.Kind == PhysicsEvidenceKind.SolidPolygon)
            {
                double min = double.PositiveInfinity, max = double.NegativeInfinity;
                for (int f = 0; f < evidence.FaceCount; f++)
                {
                    var part = evidence.Face(f);
                    if (part.Kind != PhysicsEvidenceKind.SolidPolygon || part.Key.Source != face.Key.Source || part.Key.Address != face.Key.Address) continue;
                    foreach (var p in part.Points) { min = Math.Min(min, p.Y); max = Math.Max(max, p.Y); }
                }
                if (new DescentNumber(max) < minimumBed) continue;
                if (min <= upper + bodyHeight && max >= lower) return false;
            }
            if (face.Normal.Y == 0)
            {
                double min = double.PositiveInfinity, max = double.NegativeInfinity;
                foreach (var p in face.Points) { min = Math.Min(min, p.Y); max = Math.Max(max, p.Y); }
                if (new DescentNumber(max) >= minimumBed && min <= upper + bodyHeight && max >= lower) return false;
                continue;
            }
            var polygon = DescentGeometry.Cell(face.Points, left, bottom, o.CellSize);
            foreach (var floor in ground)
            {
                var overlap = DescentGeometry.Overlap(polygon, floor.Polygon);
                if (DescentGeometry.Area2(overlap).Sign == 0) continue;
                var support = evidence.Face(floor.Face);
                if (DescentObstruction.Intersects(face.Points, support.Points, overlap, lower, upper + bodyHeight))
                    return false;
            }
        }
        return true;
    }
}
