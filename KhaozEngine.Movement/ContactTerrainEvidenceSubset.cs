using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

internal static class ContactTerrainEvidenceSubset
{
    internal static PhysicsNavDescentEvidence Build(PhysicsNavDescentEvidence evidence,
        ContactTerrainEvidenceReads reads, MoveTuning tuning, ContactTerrainEvidenceReads? workerFacts = null)
    {
        if (!reads.Matches(evidence)) throw new ArgumentException("The dependency receipt belongs to another capture.", nameof(reads));
        var cells = reads.Coverage.Concat(reads.Columns).Concat(reads.Bindings.Keys.Select(k => (k.X, k.Z)))
            .Where(c => (uint)c.X < (uint)evidence.Width && (uint)c.Z < (uint)evidence.Height).ToHashSet();
        var keep = new HashSet<int>(reads.FaceRecords.Concat(reads.Adjacency));
        for (int i = 0; i < evidence.FaceCount; i++)
            if (reads.Normals.Contains(evidence.Face(i).Key) || reads.Points.Contains(evidence.Face(i).Key)) keep.Add(i);
        foreach (var (x, z) in cells)
            foreach (var row in evidence.Column(x, z)) keep.Add(row.Face);
        foreach (var raw in evidence.RawBindings)
            if (cells.Contains((raw.X, raw.Z))) keep.Add(raw.Face);
        var solids = keep.Where(i => evidence.Face(i).Kind == PhysicsEvidenceKind.SolidPolygon)
            .Select(i => (evidence.Face(i).Key.Source, evidence.Face(i).Key.Address)).ToHashSet();
        for (int i = 0; i < evidence.FaceCount; i++)
            if (evidence.Face(i).Kind == PhysicsEvidenceKind.SolidPolygon &&
                solids.Contains((evidence.Face(i).Key.Source, evidence.Face(i).Key.Address))) keep.Add(i);
        if (workerFacts is not null)
        {
            if (!workerFacts.Matches(evidence)) throw new ArgumentException("The worker receipt belongs to another capture.", nameof(workerFacts));
            var keys = keep.Select(i => evidence.Face(i).Key).ToHashSet();
            if (workerFacts.FaceRecords.Any(i => !keep.Contains(i)) || workerFacts.Adjacency.Any(i => !keep.Contains(i)) ||
                workerFacts.Normals.Any(key => !keys.Contains(key)) || workerFacts.Points.Any(key => !keys.Contains(key)))
                throw new InvalidOperationException("A worker dependency is absent from the frozen evidence closure.");
        }
        int[] original = keep.Order().ToArray();
        var map = original.Select((old, index) => (old, index)).ToDictionary(p => p.old, p => p.index);
        var faces = original.Select(evidence.Face).ToArray();
        int count = checked(evidence.Width * evidence.Height);
        var starts = new int[count + 1];
        var coverage = Enumerable.Repeat(DescentCoverage.Excluded, count).ToArray();
        var columns = new List<DescentColumnFace>();
        for (int z = 0; z < evidence.Height; z++) for (int x = 0; x < evidence.Width; x++)
        {
            int cell = z * evidence.Width + x;
            starts[cell] = columns.Count;
            if (!cells.Contains((x, z))) continue;
            coverage[cell] = evidence.Coverage(x, z);
            foreach (var row in evidence.Column(x, z)) columns.Add(new(map[row.Face], row.Areas));
        }
        starts[^1] = columns.Count;
        var rawBindings = new List<DescentRawBinding>();
        foreach (var raw in evidence.RawBindings)
            if (cells.Contains((raw.X, raw.Z))) rawBindings.Add(raw with { Face = map[raw.Face] });
        var joins = original.Select(old => evidence.Joins(old).ToArray().Where(keep.Contains).Select(i => map[i]).ToArray()).ToArray();
        return new(evidence.Options, evidence.Width, evidence.Height, faces, starts, columns.ToArray(), coverage,
            rawBindings.ToArray(), joins);
    }
}
