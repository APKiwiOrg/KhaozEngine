using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

public enum CoverageDomainKind { AuthoredClip, BlendPath }

/// <summary>Inventories are owned by this domain. A blend path domain always declares blend paths.</summary>
public sealed class CoverageDomain
{
    public CoverageDomain(string id, CoverageDomainKind kind, IEnumerable<string> authoredFrameIds, IEnumerable<string> blendPathIds,
        IEnumerable<CoverageDenseRow>? denseRows = null)
    {
        Id = id;
        Kind = kind;
        DenseRows = Array.AsReadOnly(BoundedCollections.Snapshot(denseRows ?? [], CoverageBuildInput.MaxSamples, CoverageBuildInput.Capacity));
        AuthoredFrameIds = Array.AsReadOnly(BoundedCollections.Snapshot(authoredFrameIds, CoverageBuildInput.MaxSamples, CoverageBuildInput.Capacity));
        BlendPathIds = Array.AsReadOnly(BoundedCollections.Snapshot(blendPathIds, CoverageBuildInput.MaxSamples, CoverageBuildInput.Capacity));
    }
    public IReadOnlyList<CoverageDenseRow> DenseRows { get; }
    public string Id { get; }
    public CoverageDomainKind Kind { get; }
    public IReadOnlyList<string> AuthoredFrameIds { get; }
    public IReadOnlyList<string> BlendPathIds { get; }
}

/// <summary>One 240 Hz current pose. The consumer resolves its key using the preceding 60 Hz phase and current blend.
/// Looped or Clamped explicitly permits consumer remapping of the phase tick. Neither flag selects geometry.</summary>
public sealed record CoverageDenseRow(int DenseIndex, int PrecedingPhaseTick, ulong ResolvedStateKey,
    CoverageSample Sample, bool Looped, bool Clamped);

/// <summary>One consumer-sampled pose, with no engine animation or key sampler.</summary>
public sealed class CoverageSample
{
    readonly Matrix4x4[] palette;
    readonly CoverageCommandPose[] commands;
    public CoverageSample(string id, string domainId, string? authoredFrameId, string? blendPathId,
        CoverageStateKey stateKey, Matrix4x4 actorModel, ReadOnlySpan<Matrix4x4> palette, ReadOnlySpan<CoverageCommandPose> commands)
    {
        Id = id; DomainId = domainId; AuthoredFrameId = authoredFrameId; BlendPathId = blendPathId;
        StateKey = stateKey; ActorModel = actorModel; this.palette = palette.ToArray(); this.commands = commands.ToArray();
    }
    public string Id { get; }
    public string DomainId { get; }
    public string? AuthoredFrameId { get; }
    public string? BlendPathId { get; }
    public CoverageStateKey StateKey { get; }
    public Matrix4x4 ActorModel { get; }
    public ReadOnlySpan<Matrix4x4> Palette => palette;
    public ReadOnlySpan<CoverageCommandPose> Commands => commands;
}

/// <summary>Immutable, exact-input snapshot. Use Read for file-backed production inputs.</summary>
public sealed class CoverageBuildInput
{
    public const int MaxSamples = 65_536;
    public const int MaxCandidates = 4_096;
    public const int MaxPairs = 4_194_304;
    readonly Matrix4x4[] inverseBind;
    internal CoverageBuildInput(CoverageRefinement topology, ReadOnlySpan<Matrix4x4> inverseBind,
        IEnumerable<CoverageDomain> domains, IEnumerable<CoverageSample> samples, IEnumerable<string> candidates,
        CoverageRecessionIdentity identity, string inputSha256, IEnumerable<string>? sourcePaths = null)
    {
        Topology = topology; this.inverseBind = inverseBind.ToArray(); Identity = identity; InputSha256 = inputSha256;
        Domains = Array.AsReadOnly(BoundedCollections.Snapshot(domains, MaxSamples, Capacity));
        Samples = Array.AsReadOnly(BoundedCollections.Snapshot(samples, MaxSamples, Capacity));
        SourcePaths = Array.AsReadOnly((sourcePaths ?? []).ToArray());
        CandidateSampleIds = Array.AsReadOnly(BoundedCollections.Snapshot(candidates, MaxCandidates, count => new CoverageBuildDataException(CoverageBuildInputRefusal.CapacityExceeded, "Candidate inventory exceeds capacity.") { Count = count, Limit = MaxCandidates }));
        long denseCount = Domains.Sum(d => (long)d.DenseRows.Count);
        if (denseCount > MaxSamples) throw Capacity(denseCount);
        ValidateInventories();
    }
    public static CoverageBuildInput Read(string manifestPath) => CoverageBuildInputJson.Read(manifestPath);
    public CoverageRefinement Topology { get; }
    public ReadOnlySpan<Matrix4x4> InverseBind => inverseBind;
    public IReadOnlyList<CoverageDomain> Domains { get; }
    public IReadOnlyList<CoverageSample> Samples { get; }
    public IReadOnlyList<string> CandidateSampleIds { get; }
    public CoverageRecessionIdentity Identity { get; }
    public string InputSha256 { get; }
    internal IReadOnlyList<string> SourcePaths { get; }
    internal static Exception Capacity(long count) => new CoverageBuildDataException(CoverageBuildInputRefusal.CapacityExceeded, "Input inventory exceeds capacity.")
    { Count = count, Limit = MaxSamples };

    void ValidateInventories()
    {
        if (Domains.Count == 0 || Samples.Count == 0 || CandidateSampleIds.Count == 0 || inverseBind.Length == 0)
            throw new CoverageBuildDataException(CoverageBuildInputRefusal.EmptyInventory, "Coverage work cannot be vacuous.");
        if ((long)Samples.Count * CandidateSampleIds.Count > MaxPairs)
            throw new CoverageBuildDataException(CoverageBuildInputRefusal.CapacityExceeded, "Pair matrix exceeds capacity.")
            { Count = (long)Samples.Count * CandidateSampleIds.Count, Limit = MaxPairs };
        Unique(Domains.Select(d => d.Id), "$.domains");
        Unique(Samples.Select(s => s.Id), "$.samples");
        Unique(CandidateSampleIds, "$.candidateSampleIds");
        foreach (var domain in Domains)
        {
            Unique(domain.AuthoredFrameIds, "$.domains.authoredFrameIds");
            Unique(domain.BlendPathIds, "$.domains.blendPathIds");
            if (!Enum.IsDefined(domain.Kind) || domain.AuthoredFrameIds.Count == 0
                || domain.Kind != CoverageDomainKind.AuthoredClip && domain.BlendPathIds.Count == 0)
                throw new CoverageBuildDataException(CoverageBuildInputRefusal.EmptyInventory, "Domain requires its declared inventories.") { DomainId = domain.Id };

        }
        foreach (var sample in Samples)
        {
            var domain = Domains.SingleOrDefault(d => d.Id == sample.DomainId);
            if (domain == null || sample.AuthoredFrameId == null && sample.BlendPathId == null
                || sample.AuthoredFrameId != null && !domain.AuthoredFrameIds.Contains(sample.AuthoredFrameId, StringComparer.Ordinal)
                || sample.BlendPathId != null && !domain.BlendPathIds.Contains(sample.BlendPathId, StringComparer.Ordinal))
                throw new CoverageBuildDataException(CoverageBuildInputRefusal.DomainMembership, "Sample is outside its domain inventories.")
                { SampleId = sample.Id, DomainId = sample.DomainId };
        }
        foreach (var domain in Domains)
        {
            foreach (string frame in domain.AuthoredFrameIds)
                if (!Samples.Any(s => s.DomainId == domain.Id && s.AuthoredFrameId == frame)) Missing(domain.Id, frame);
            foreach (string path in domain.BlendPathIds)
                if (!Samples.Any(s => s.DomainId == domain.Id && s.BlendPathId == path)) Missing(domain.Id, path);
        }
        foreach (string id in CandidateSampleIds)
            if (!Samples.Any(s => s.Id == id))
                throw new CoverageBuildDataException(CoverageBuildInputRefusal.UnknownCandidate, "Candidate sample does not exist.") { SampleId = id };
    }
    static void Unique(IEnumerable<string> ids, string path)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in ids)
            if (string.IsNullOrWhiteSpace(id) || !found.Add(id))
                throw new CoverageBuildDataException(CoverageBuildInputRefusal.InvalidOrDuplicateId, "Inventory IDs must be nonblank and unique.") { Path = path, ItemId = id };
    }
    static void Missing(string domain, string item) => throw new CoverageBuildDataException(CoverageBuildInputRefusal.MissingInventorySample,
        "A declared inventory item has no samples.")
    { DomainId = domain, ItemId = item };
}

public enum CoverageBuildInputRefusal
{
    InvalidJson, DuplicateMember, UnknownMember, MissingMember, InvalidValue, NonFiniteNumber,
    UnsupportedVersion, MalformedDigest, HashMismatch, CapacityExceeded, EmptyInventory,
    InvalidOrDuplicateId, DomainMembership, MissingInventorySample, UnknownCandidate, AssetBinding, InputIo,
}

/// <summary>Malformed input with rooted JSON Path and optional source, sample, domain, item and capacity context.
/// Wrapped sidecar refusals retain their original typed inner exception.</summary>
public sealed class CoverageBuildDataException : MalformedDataException
{
    public CoverageBuildDataException(CoverageBuildInputRefusal reason, string message, Exception? inner = null) : base(message, inner) => Reason = reason;
    public CoverageBuildInputRefusal Reason { get; }
    public string Path { get; init; } = "$";
    public string? Identity { get; init; }
    public string? MemberName { get; init; }
    public string? SampleId { get; init; }
    public string? DomainId { get; init; }
    public string? ItemId { get; init; }
    public long? Count { get; init; }
    public long? Limit { get; init; }
}

public enum CoverageBuildValidationRefusal { ProvenanceMismatch, RepresentativeMismatch, CandidateConstruction, StateInventoryMismatch, OutputIo, OutputAlias }

public sealed class CoverageBuildValidationException : InvalidOperationException
{
    public CoverageBuildValidationException(CoverageBuildValidationRefusal reason, string message, Exception? inner = null) : base(message, inner) => Reason = reason;
    public CoverageBuildValidationRefusal Reason { get; }
    public string? SampleId { get; init; }
    public int? GroupIndex { get; init; }
    public CoverageFailure? Failure { get; init; }
}
