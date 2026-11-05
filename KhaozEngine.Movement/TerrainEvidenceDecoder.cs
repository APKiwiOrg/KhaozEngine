using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using static KhaozEngine.Movement.TerrainEvidenceReader;

namespace KhaozEngine.Movement;

/// <summary>Canonical bounded evidence decoding. No graph candidate is allocated here.</summary>
internal static class TerrainEvidenceDecoder
{
    internal static PhysicsNavDescentEvidence Decode(ReadOnlySpan<byte> payload, PhysicsNavBakeOptions options, PhysicsNavColumns columns)
    {
        var r = new TerrainEvidenceReader(payload);
        var budget = new DescentCaptureIndexBudget();
        budget.Reserve(payload.Length, 1);
        int namesCount = r.Count(3L * options.MaxLayerCells, 2);
        budget.Reserve(namesCount, 48);
        var names = new string[namesCount]; var usedNames = new bool[namesCount];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = r.Text(budget);
            if (i > 0 && string.CompareOrdinal(names[i - 1], names[i]) >= 0) throw Corrupt();
        }
        int pointsCount = r.Count(Array.MaxLength, 13);
        budget.Reserve(pointsCount, 80);
        var points = new PhysicsEvidencePoint[pointsCount]; var usedPoints = new bool[pointsCount];
        var uniquePoints = new HashSet<(long, long, long)>();
        for (int i = 0; i < points.Length; i++)
        {
            bool single = r.Boolean();
            var p = single ? new PhysicsEvidencePoint(r.Single(), r.Single(), r.Single()) : new(r.Double(), r.Double(), r.Double());
            if (!single && TerrainEvidenceBinary.SingleExact(p.X) && TerrainEvidenceBinary.SingleExact(p.Y) && TerrainEvidenceBinary.SingleExact(p.Z)) throw Corrupt();
            if (!uniquePoints.Add((BitConverter.DoubleToInt64Bits(p.X), BitConverter.DoubleToInt64Bits(p.Y), BitConverter.DoubleToInt64Bits(p.Z)))) throw Corrupt();
            points[i] = p;
        }
        int count = r.Count(options.MaxLayerCells, 16);
        budget.Reserve(count, 72);
        var faces = new DescentFace[count]; var joins = new int[count][];
        int nextPoint = 0;
        for (int i = 0; i < count; i++)
        {
            int source = r.Index(names.Length); usedNames[source] = true;
            long address = r.Address(); uint part = r.Unsigned();
            if (part > int.MaxValue) throw Corrupt();
            var key = new MovementSurfaceFeatureKey(names[source], address, (int)part);
            if (!key.IsValid) throw Corrupt();
            if (i > 0 && Compare(faces[i - 1].Key, key) >= 0) throw Corrupt();
            int world = r.Index(names.Length), local = r.Index(names.Length); usedNames[world] = usedNames[local] = true;
            var owner = new MovementSupportKey(names[world], names[local]);
            bool analytic = r.Boolean(); byte tag = r.Byte();
            if (tag > (byte)PhysicsEvidenceKind.Cylinder) throw Corrupt();
            var kind = (PhysicsEvidenceKind)tag;
            int n = r.Count(Array.MaxLength, 1); budget.Reserve(n, 48);
            bool curved = kind is PhysicsEvidenceKind.Sphere or PhysicsEvidenceKind.Capsule or PhysicsEvidenceKind.Cylinder;
            if (curved ? n != 0 || analytic : n < 3 || kind == PhysicsEvidenceKind.Triangle && n != 3 || analytic && n != 3) throw Corrupt();
            var vertices = new PhysicsEvidencePoint[n];
            for (int j = 0; j < n; j++)
            {
                int p = r.Index(points.Length);
                if (!usedPoints[p] && p != nextPoint++) throw Corrupt();
                usedPoints[p] = true; vertices[j] = points[p];
            }
            Pose pose = default; float radius = 0, half = 0;
            if (curved)
            {
                var position = new Vector3(r.Single(), r.Single(), r.Single());
                var rotation = new Quaternion(r.Single(), r.Single(), r.Single(), r.Single());
                double norm = (double)rotation.X * rotation.X + (double)rotation.Y * rotation.Y + (double)rotation.Z * rotation.Z + (double)rotation.W * rotation.W;
                radius = r.Single(); half = r.Single();
                if (norm <= 0 || radius <= 0 || half < 0 || kind == PhysicsEvidenceKind.Sphere && half != 0) throw Corrupt();
                pose = new(position, rotation);
            }
            var face = new DescentFace(key, owner, analytic, kind, vertices, pose, radius, half, owner);
            var normal = face.Normal;
            if (!float.IsFinite(normal.X) || !float.IsFinite(normal.Y) || !float.IsFinite(normal.Z) || analytic && normal == Vector3.Zero) throw Corrupt();
            faces[i] = face;
            int edges = r.Count(Array.MaxLength, 1); budget.Reserve(edges, 4);
            if (!analytic && edges != 0) throw Corrupt();
            var adjacent = joins[i] = new int[edges];
            for (int j = 0; j < edges; j++)
            {
                adjacent[j] = r.Index(count);
                if (adjacent[j] == i || j > 0 && adjacent[j] <= adjacent[j - 1]) throw Corrupt();
            }
        }
        for (int i = 0; i < count; i++) foreach (int j in joins[i]) if (!faces[j].Analytic) throw Corrupt();
        if (usedNames.Any(used => !used) || usedPoints.Any(used => !used)) throw Corrupt();
        int cells = checked(columns.Width * columns.Height);
        budget.Reserve(cells, 5); budget.Reserve(1, 4);
        var starts = new int[cells + 1]; var coverage = new DescentCoverage[cells]; Array.Fill(coverage, DescentCoverage.Excluded);
        var references = new List<DescentColumnFace>(); var raw = new List<DescentRawBinding>();
        int included = r.Count(cells, 3), cell = 0, pending = 0;
        for (int i = 0; i < included; i++)
        {
            uint delta = r.Unsigned();
            if (i > 0 && delta == 0 || (long)cell + delta >= cells) throw Corrupt();
            cell = checked(cell + (int)delta);
            while (pending <= cell) starts[pending++] = references.Count;
            int x = cell % columns.Width, z = cell / columns.Width;
            byte tag = r.Byte();
            if (tag is not ((byte)DescentCoverage.Complete) and not ((byte)DescentCoverage.KnownEmpty) and not ((byte)DescentCoverage.OutOfBounds)) throw Corrupt();
            coverage[cell] = (DescentCoverage)tag;
            int n = r.Count(options.MaxLayerCells - references.Count, 2); budget.Reserve(n, 16);
            if (coverage[cell] == DescentCoverage.Complete ? n == 0 : n != 0) throw Corrupt();
            var unique = new HashSet<int>();
            for (int j = 0; j < n; j++)
            {
                int face = r.Index(count); uint areas = r.Unsigned();
                if (!unique.Add(face)) throw Corrupt();
                references.Add(new(face, areas));
            }
            var originals = columns.GetColumn(x, z);
            if (originals.Length > r.Remaining) throw Corrupt();
            for (int ordinal = 0; ordinal < originals.Length; ordinal++)
            {
                uint encoded = r.Unsigned();
                if (encoded > count || coverage[cell] == DescentCoverage.OutOfBounds && encoded != 0 ||
                    coverage[cell] != DescentCoverage.OutOfBounds && encoded == 0) throw Corrupt();
                if (encoded == 0) continue;
                budget.Reserve(1, 40);
                raw.Add(new(x, z, ordinal, BitConverter.SingleToInt32Bits(originals[ordinal].Height), (int)encoded - 1));
            }
        }
        while (pending <= cells) starts[pending++] = references.Count;
        if (r.Remaining != 0) throw Corrupt();
        return new PhysicsNavDescentEvidence(options, columns.Width, columns.Height, faces, starts, references.ToArray(), coverage, raw.ToArray(), joins, budget).RequireDependencies();
    }
    internal static int Compare(MovementSurfaceFeatureKey a, MovementSurfaceFeatureKey b)
    {
        int order = string.CompareOrdinal(a.Source, b.Source);
        if (order == 0) order = a.Address.CompareTo(b.Address);
        return order != 0 ? order : a.Part.CompareTo(b.Part);
    }
}
