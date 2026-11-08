using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Reapplies supplied rows and exactly certifies all declared entry/sample pairs. Never generates candidates.</summary>
public static class CoverageSampleVerifier
{
    public static CoverageEvidenceManifest Verify(CoverageBuildInput input, CoverageRecessionTable table)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(table);
        if (input.Identity != table.Identity)
            throw new CoverageBuildValidationException(CoverageBuildValidationRefusal.ProvenanceMismatch, "Table provenance differs from the checked inputs.");
        int[] expected = Representatives(input.Topology, input.Samples[0]);
        CheckRepresentatives(expected, table.Representatives);
        foreach (var sample in input.Samples)
            CheckRepresentatives(expected, Representatives(input.Topology, sample), sample.Id);
        var keys = input.Samples.Select(s => s.StateKey.Value).Distinct().Order().ToArray();
        if (!keys.SequenceEqual(table.States.ToArray().Select(s => s.Key.Value)))
            throw new CoverageBuildValidationException(CoverageBuildValidationRefusal.StateInventoryMismatch, "Table state inventory differs.");
        if ((long)table.EntryCount * input.Samples.Count > CoverageBuildInput.MaxPairs)
            throw new CoverageBuildDataException(CoverageBuildInputRefusal.CapacityExceeded, "Verification pair matrix exceeds capacity.")
            { Count = (long)table.EntryCount * input.Samples.Count, Limit = CoverageBuildInput.MaxPairs };
        var pairs = Pairs(input, table);
        var byPair = pairs.ToDictionary(p => (p.EntryIndex, p.SampleId));
        var claims = new List<CoverageSelectedClaim>();
        var holes = new List<CoverageHole>();
        foreach (var group in input.Samples.GroupBy(s => s.StateKey.Value).OrderBy(g => g.Key))
        {
            if (!table.TrySelect(new(group.Key), out int entry))
            {
                holes.Add(new(group.Key, Array.AsReadOnly(group.Select(s => s.Id).ToArray())));
                continue;
            }
            foreach (var sample in group)
            {
                var pair = byPair[(entry, sample.Id)];
                claims.Add(new(group.Key, entry, sample.Id, pair.Valid, pair.Failure));
            }
        }
        return new(input, table, pairs, claims, holes);
    }

    internal static CoveragePairResult[] Pairs(CoverageBuildInput input, CoverageRecessionTable table)
    {
        var topology = input.Topology;
        var uniforms = new CoverageCarryUniforms(topology, input.InverseBind.Length);
        var carry = new CoverageCarryScratch(topology, input.InverseBind.Length);
        var certification = new CoverageScratch(topology);
        var before = new ModelVertex[topology.VertexCount];
        var after = new ModelVertex[topology.VertexCount];
        var pairs = new List<CoveragePairResult>();
        foreach (var sample in input.Samples)
        {
            bool prepared = uniforms.TryPrepare(sample.Palette, input.InverseBind, sample.ActorModel, sample.Commands, out var preparationFailure);
            if (prepared) prepared = CoverageCarryReference.TryCarry(topology, uniforms.ComposedPalette, table, -1, uniforms.Commands, carry, before, out preparationFailure);
            for (int entry = 0; entry < table.EntryCount; entry++)
            {
                CoverageFailure failure = preparationFailure;
                bool valid = prepared
                    && CoverageCarryReference.TryCarry(topology, uniforms.ComposedPalette, table, entry, uniforms.Commands, carry, after, out failure)
                    && CoverageCertification.ValidateGeometry(topology, before, after, sample.ActorModel, sample.Commands, certification, out failure);
                pairs.Add(new(entry, sample.Id, valid, failure));
            }
        }
        return pairs.OrderBy(p => p.EntryIndex).ThenBy(p => p.SampleId, StringComparer.Ordinal).ToArray();
    }

    internal static int[] Representatives(CoverageRefinement topology, CoverageSample sample)
    {
        int[] regions = Enumerable.Repeat(-1, topology.Map.Regions.Count).ToArray();
        foreach (var command in sample.Commands)
            if ((uint)command.RegionIndex < (uint)regions.Length) regions[command.RegionIndex] = 0;
        int[] representatives = new int[topology.PositionGroupCount];
        CoverageReferenceTopology.FillActiveRepresentatives(topology, regions, representatives, new CoverageReferenceArithmetic());
        return representatives;
    }
    internal static void CheckRepresentatives(ReadOnlySpan<int> expected, ReadOnlySpan<int> actual, string? sample = null)
    {
        if (expected.Length != actual.Length)
            throw new CoverageBuildValidationException(CoverageBuildValidationRefusal.RepresentativeMismatch, "Representative vector length differs.") { SampleId = sample };
        for (int group = 0; group < expected.Length; group++)
            if (expected[group] != actual[group])
                throw new CoverageBuildValidationException(CoverageBuildValidationRefusal.RepresentativeMismatch, "Representative does not own the canonical active topology record.")
                { GroupIndex = group, SampleId = sample };
    }
}
