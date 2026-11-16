using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

internal sealed record CoverageDenseRowInventory(int DenseIndex, int PrecedingPhaseTick, ulong ResolvedStateKey,
    CoverageSampleInventory Sample, bool Looped, bool Clamped);
internal sealed record CoverageDomainInventory(string Id, CoverageDomainKind Kind,
    IReadOnlyList<string> AuthoredFrameIds, IReadOnlyList<string> BlendPathIds, IReadOnlyList<CoverageDenseRowInventory> DenseRows);

/// <summary>Immutable metadata with ordinal pose access. A session view borrows that session's lifetime.</summary>
internal sealed class CoverageCampaign
{
    readonly Matrix4x4[] inverseBind;
    readonly Func<int, CoverageSample> readSample;
    readonly Func<int, int, CoverageDenseRow> readDenseRow;
    readonly Action ensureAlive;
    readonly IReadOnlyList<string> materializedSources;

    internal CoverageCampaign(CoverageRefinement topology, ReadOnlySpan<Matrix4x4> inverseBind,
        IReadOnlyList<CoverageDomainInventory> domains, IReadOnlyList<CoverageSampleInventory> samples,
        IReadOnlyList<string> candidates, CoverageRecessionIdentity identity, string inputSha256,
        IReadOnlyList<string> sourcePaths, Func<int, CoverageSample> readSample, Func<int, int, CoverageDenseRow> readDenseRow,
        Action ensureAlive, IReadOnlyList<string>? materializedSources = null)
    {
        Topology = topology; this.inverseBind = inverseBind.ToArray(); Identity = identity; InputSha256 = inputSha256;
        Domains = Array.AsReadOnly(domains.ToArray()); Samples = Array.AsReadOnly(samples.ToArray());
        if (candidates.Count > CoverageBuildInput.MaxCandidates)
            throw new CoverageBuildDataException(CoverageBuildInputRefusal.CapacityExceeded, "Candidate inventory exceeds capacity.")
            { Count = candidates.Count, Limit = CoverageBuildInput.MaxCandidates };
        CandidateSampleIds = Array.AsReadOnly(candidates.ToArray()); SourcePaths = Array.AsReadOnly(sourcePaths.ToArray());
        this.materializedSources = Array.AsReadOnly((materializedSources ?? sourcePaths).ToArray());
        this.readSample = readSample; this.readDenseRow = readDenseRow; this.ensureAlive = ensureAlive;
        ValidateInventories();
    }

    internal CoverageRefinement Topology { get; }
    internal ReadOnlySpan<Matrix4x4> InverseBind => inverseBind;
    internal IReadOnlyList<CoverageDomainInventory> Domains { get; }
    internal IReadOnlyList<CoverageSampleInventory> Samples { get; }
    internal IReadOnlyList<string> CandidateSampleIds { get; }
    internal CoverageRecessionIdentity Identity { get; }
    internal string InputSha256 { get; }
    internal IReadOnlyList<string> SourcePaths { get; }
    internal CoverageSample ReadSample(int sampleIndex) { ensureAlive(); return readSample(sampleIndex); }
    internal CoverageDenseRow ReadDenseRow(int domainIndex, int rowIndex) { ensureAlive(); return readDenseRow(domainIndex, rowIndex); }

    internal CoverageCampaign WithCandidates(IReadOnlyList<string> candidateSampleIds)
    {
        ensureAlive();
        return new(Topology, inverseBind, Domains, Samples, candidateSampleIds, Identity, InputSha256,
            SourcePaths, readSample, readDenseRow, ensureAlive, materializedSources);
    }

    internal CoverageBuildInput Materialize()
    {
        ensureAlive();
        var domains = Domains.Select((d, i) => new CoverageDomain(d.Id, d.Kind, d.AuthoredFrameIds, d.BlendPathIds,
            Enumerable.Range(0, d.DenseRows.Count).Select(r => ReadDenseRow(i, r))));
        return new(Topology, inverseBind, domains, Enumerable.Range(0, Samples.Count).Select(ReadSample),
            CandidateSampleIds, Identity, InputSha256, materializedSources);
    }

    internal static CoverageCampaign FromSnapshot(CoverageBuildInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        static CoverageSampleInventory Inventory(CoverageSample s) => new(s.Id, s.DomainId, s.AuthoredFrameId, s.BlendPathId, s.StateKey.Value);
        var domains = input.Domains.Select(d => new CoverageDomainInventory(d.Id, d.Kind, d.AuthoredFrameIds, d.BlendPathIds,
            Array.AsReadOnly(d.DenseRows.Select(r => new CoverageDenseRowInventory(r.DenseIndex, r.PrecedingPhaseTick,
                r.ResolvedStateKey, Inventory(r.Sample), r.Looped, r.Clamped)).ToArray()))).ToArray();
        return new(input.Topology, input.InverseBind, domains, input.Samples.Select(Inventory).ToArray(), input.CandidateSampleIds,
            input.Identity, input.InputSha256, input.SourcePaths, i => input.Samples[i], (d, r) => input.Domains[d].DenseRows[r], () => { });
    }

    void ValidateInventories()
    {
        if (Domains.Count == 0 || Samples.Count == 0 || CandidateSampleIds.Count == 0 || inverseBind.Length == 0)
            throw new CoverageBuildDataException(CoverageBuildInputRefusal.EmptyInventory, "Coverage work cannot be vacuous.");
        if (Samples.Count > CoverageBuildInput.MaxSamples || Domains.Count > CoverageBuildInput.MaxSamples
            || Domains.Sum(d => (long)d.DenseRows.Count) > CoverageBuildInput.MaxSamples) throw CoverageBuildInput.Capacity(Samples.Count);
        long pairs = (long)Samples.Count * CandidateSampleIds.Count;
        if (pairs > CoverageBuildInput.MaxPairs)
            throw new CoverageBuildDataException(CoverageBuildInputRefusal.CapacityExceeded, "Pair matrix exceeds capacity.") { Count = pairs, Limit = CoverageBuildInput.MaxPairs };
        Unique(Domains.Select(d => d.Id), "$.domains"); Unique(Samples.Select(s => s.Id), "$.samples");
        Unique(CandidateSampleIds, "$.candidateSampleIds");
        var domains = Domains.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var frames = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var paths = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var domain in Domains)
        {
            Unique(domain.AuthoredFrameIds, "$.domains.authoredFrameIds"); Unique(domain.BlendPathIds, "$.domains.blendPathIds");
            if (!Enum.IsDefined(domain.Kind) || domain.AuthoredFrameIds.Count == 0
                || domain.Kind != CoverageDomainKind.AuthoredClip && domain.BlendPathIds.Count == 0)
                throw new CoverageBuildDataException(CoverageBuildInputRefusal.EmptyInventory, "Domain requires its declared inventories.") { DomainId = domain.Id };
            frames.Add(domain.Id, domain.AuthoredFrameIds.ToHashSet(StringComparer.Ordinal));
            paths.Add(domain.Id, domain.BlendPathIds.ToHashSet(StringComparer.Ordinal));
        }
        var coveredFrames = Domains.ToDictionary(d => d.Id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var coveredPaths = Domains.ToDictionary(d => d.Id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var sample in Samples)
        {
            if (!domains.ContainsKey(sample.DomainId) || sample.AuthoredFrameId == null && sample.BlendPathId == null
                || sample.AuthoredFrameId != null && !frames[sample.DomainId].Contains(sample.AuthoredFrameId)
                || sample.BlendPathId != null && !paths[sample.DomainId].Contains(sample.BlendPathId))
                throw new CoverageBuildDataException(CoverageBuildInputRefusal.DomainMembership, "Sample is outside its domain inventories.")
                { SampleId = sample.Id, DomainId = sample.DomainId };
            if (sample.AuthoredFrameId != null) coveredFrames[sample.DomainId].Add(sample.AuthoredFrameId);
            if (sample.BlendPathId != null) coveredPaths[sample.DomainId].Add(sample.BlendPathId);
        }
        foreach (var domain in Domains)
            foreach (string item in domain.AuthoredFrameIds.Except(coveredFrames[domain.Id], StringComparer.Ordinal)
                .Concat(domain.BlendPathIds.Except(coveredPaths[domain.Id], StringComparer.Ordinal)))
                throw new CoverageBuildDataException(CoverageBuildInputRefusal.MissingInventorySample, "A declared inventory item has no samples.") { DomainId = domain.Id, ItemId = item };
        var ids = Samples.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (string id in CandidateSampleIds)
            if (!ids.Contains(id)) throw new CoverageBuildDataException(CoverageBuildInputRefusal.UnknownCandidate, "Candidate sample does not exist.") { SampleId = id };
    }

    static void Unique(IEnumerable<string> ids, string path)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in ids)
            if (string.IsNullOrWhiteSpace(id) || !found.Add(id))
                throw new CoverageBuildDataException(CoverageBuildInputRefusal.InvalidOrDuplicateId, "Inventory IDs must be nonblank and unique.") { Path = path, ItemId = id };
    }
}
