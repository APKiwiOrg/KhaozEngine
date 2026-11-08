using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>One canonical container union. Profiles never borrow another profile's tuning or acceptance bits.</summary>
internal static class TerrainEvidenceUnion
{
    internal static PhysicsNavDescentEvidence Build(PhysicsNavBakeOptions options, PhysicsNavColumns columns,
        IEnumerable<PhysicsNavDescentEvidence> evidence)
    {
        var sets = evidence.ToArray();
        var records = new Dictionary<MovementSurfaceFeatureKey, DescentFace>();
        var edges = new Dictionary<MovementSurfaceFeatureKey, HashSet<MovementSurfaceFeatureKey>>();
        foreach (var e in sets)
        {
            if (e.Options != options || e.Width != columns.Width || e.Height != columns.Height)
                throw new InvalidOperationException("Terrain evidence belongs to a different capture.");
            for (int i = 0; i < e.FaceCount; i++)
            {
                var f = e.Face(i);
                if (records.TryGetValue(f.Key, out var prior) && !ReferenceEquals(prior, f))
                    throw new InvalidOperationException("Terrain evidence has ambiguous canonical keys.");
                records[f.Key] = f;
                if (!edges.TryGetValue(f.Key, out var joins)) edges.Add(f.Key, joins = []);
                foreach (int j in e.Joins(i)) joins.Add(e.Face(j).Key);
            }
        }
        var faces = records.Values.OrderBy(f => f.Key.Source, StringComparer.Ordinal).ThenBy(f => f.Key.Address).ThenBy(f => f.Key.Part).ToArray();
        var ids = faces.Select((f, i) => (f.Key, i)).ToDictionary(p => p.Key, p => p.i);
        int cells = checked(columns.Width * columns.Height);
        var coverage = Enumerable.Repeat(DescentCoverage.Excluded, cells).ToArray();
        var rows = new Dictionary<int, DescentColumnFace[]>();
        var raw = new Dictionary<(int X, int Z, int Ordinal), DescentRawBinding>();
        foreach (var e in sets)
        {
            for (int z = 0; z < e.Height; z++) for (int x = 0; x < e.Width; x++)
            {
                var state = e.StoredCoverage(x, z);
                if (state == DescentCoverage.Excluded) continue;
                int cell = z * e.Width + x;
                var row = e.Column(x, z).ToArray().Select(r => new DescentColumnFace(ids[e.Face(r.Face).Key], r.Areas)).ToArray();
                if (rows.TryGetValue(cell, out var prior) && (coverage[cell] != state || !prior.SequenceEqual(row)))
                    throw new InvalidOperationException("Terrain columns disagree between profiles.");
                coverage[cell] = state; rows[cell] = row;
            }
            foreach (var r in e.RawBindings)
            {
                var key = (r.X, r.Z, r.Ordinal); var mapped = r with { Face = ids[e.Face(r.Face).Key] };
                if (raw.TryGetValue(key, out var prior) && prior != mapped)
                    throw new InvalidOperationException("Terrain raw identities disagree between profiles.");
                raw[key] = mapped;
            }
        }
        var starts = new int[cells + 1]; var references = new List<DescentColumnFace>();
        for (int i = 0; i < cells; i++) { starts[i] = references.Count; if (rows.TryGetValue(i, out var row)) references.AddRange(row); }
        starts[^1] = references.Count;
        var bindings = raw.Values.OrderBy(r => r.Z).ThenBy(r => r.X).ThenBy(r => r.Ordinal).ToArray();
        var adjacency = faces.Select(f => edges[f.Key].Select(k => ids[k]).Order().ToArray()).ToArray();
        return new(options, columns.Width, columns.Height, faces, starts, references.ToArray(), coverage, bindings, adjacency);
    }
}
