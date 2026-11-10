using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

public sealed record CoverageBuildResult(CoverageRecessionTable Table, CoverageEvidenceManifest Evidence);

/// <summary>Exact candidates, bitwise deduplication and deterministic greedy cover over keys' pose intersections.</summary>
public static class CoverageTableBuilder
{
    sealed record Candidate(float[] Scalars, byte[] Bytes, string Hash);
    public static CoverageBuildResult Build(CoverageBuildInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var topology = input.Topology;
        var constructor = new CoverageExactConstructor(topology, input.InverseBind);
        var candidates = new List<Candidate>();
        var candidateEvidence = new List<CoverageCandidateEvidence>();
        int[] representatives = CoverageSampleVerifier.Representatives(topology, input.Samples[0]);
        foreach (string id in input.CandidateSampleIds)
        {
            var sample = input.Samples.Single(s => s.Id == id);
            var scalars = new float[topology.PositionGroupCount];
            var reps = new int[topology.PositionGroupCount];
            if (!constructor.TryConstruct(sample.Palette, sample.ActorModel, sample.Commands,
                new ModelVertex[topology.VertexCount], scalars, reps, out var failure))
                throw new CoverageBuildValidationException(CoverageBuildValidationRefusal.CandidateConstruction, "Exact candidate construction refused.") { SampleId = id, Failure = failure };
            CoverageSampleVerifier.CheckRepresentatives(representatives, reps, id);
            byte[] bytes = ScalarBytes(scalars);
            string hash = CoverageBuildInputJson.Hash(bytes);
            candidateEvidence.Add(new(id, hash));
            if (!candidates.Any(c => c.Bytes.AsSpan().SequenceEqual(bytes))) candidates.Add(new(scalars, bytes, hash));
        }
        candidates.Sort((a, b) => { int h = StringComparer.Ordinal.Compare(a.Hash, b.Hash); return h != 0 ? h : a.Bytes.AsSpan().SequenceCompareTo(b.Bytes); });
        var states = input.Samples.GroupBy(s => s.StateKey.Value).OrderBy(g => g.Key).ToArray();
        var all = CoverageRecessionTable.Create(input.Identity, representatives, candidates.SelectMany(c => c.Scalars).ToArray(),
            states.Select(g => new CoverageStateEntry(new(g.Key), -1)).ToArray());
        var pairs = CoverageSampleVerifier.Pairs(input, all);
        var valid = pairs.ToDictionary(p => (p.EntryIndex, p.SampleId), p => p.Valid);
        var eligible = states.ToDictionary(g => g.Key, g => Enumerable.Range(0, candidates.Count).Where(e => g.All(s => valid[(e, s.Id)])).ToHashSet());
        var assignments = new Dictionary<ulong, int>();
        while (true)
        {
            int winner = -1, best = 0;
            // Candidates are sorted by canonical scalar hash and bytes. First wins every equal-sized cover.
            for (int entry = 0; entry < candidates.Count; entry++)
            {
                int count = eligible.Count(p => !assignments.ContainsKey(p.Key) && p.Value.Contains(entry));
                if (count > best) { best = count; winner = entry; }
            }
            if (winner < 0) break;
            foreach (var pair in eligible)
                if (!assignments.ContainsKey(pair.Key) && pair.Value.Contains(winner)) assignments.Add(pair.Key, winner);
        }
        int[] selected = assignments.Values.Distinct().Order().ToArray();
        var remap = selected.Select((candidate, index) => (candidate, index)).ToDictionary(p => p.candidate, p => p.index);
        var table = CoverageRecessionTable.Create(input.Identity, representatives, selected.SelectMany(i => candidates[i].Scalars).ToArray(),
            states.Select(g => new CoverageStateEntry(new(g.Key), assignments.TryGetValue(g.Key, out int candidate) ? remap[candidate] : -1)).ToArray());
        // Re-read canonical bytes and independently apply every serialized row. No candidate generation in this verifier.
        var serialized = CoverageRecessionTableBinary.Read(CoverageRecessionTableBinary.Write(table), "built-table");
        var verified = CoverageSampleVerifier.Verify(input, serialized);
        var entries = candidates.Select((c, i) => new CoverageEntryEvidence(i, c.Hash, remap.GetValueOrDefault(i, -1)));
        return new(serialized, new(input, serialized, pairs, verified.SelectedClaims, verified.Holes, candidateEvidence, entries));
    }

    internal static byte[] ScalarBytes(float[] scalars)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (float scalar in scalars) writer.Write(BitConverter.SingleToUInt32Bits(scalar));
        return stream.ToArray();
    }
}
