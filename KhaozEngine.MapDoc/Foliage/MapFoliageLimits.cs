using System;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>The typed outcome shared by foliage context capture, preparation and generation. Only Complete publishes
/// usable output. MissingSupport, Unavailable and Ambiguous name support refusals. PartialResidency names a document
/// whose tiles, surfaces or foliage are not all loaded. A refusal never truncates or inflates a budget to pass.</summary>
public enum MapFoliageStatus { Complete, MissingSupport, Unavailable, PartialResidency, Ambiguous, CapacityExceeded, NotRepresentable, Invalid }

/// <summary>Per-call foliage work ceilings. Chunk preparation and evaluation charge candidates, samples, prepared scopes,
/// faces and owner candidates. Query charges its totals across every chunk before it retains a sample. Generate is a
/// one-chunk Query for retention, so MaxQuerySamples and MaxQueryRetainedBytes bound it beside the per-chunk MaxSamples.
/// Comparison charges candidates, change rows and reconciliation bytes per lattice block. A prepared face is charged
/// 1280 bytes once: that covers the support grid's record and the membership context's reused hold of the same face.
/// Context construction has its own <see cref="MapFoliageContextLimits"/>.</summary>
/// <param name="MaxCandidates">Demand sites: MapFoliageDistribution, MapFoliageGenerationAdmission, MapFoliageInstanceDistribution. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxSamples">Demand sites: MapFoliageDistribution. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxPrepareScopes">Demand sites: MapFoliageOwners, MapFoliageSupport. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxFaces">Demand sites: MapFoliageCandidateSupport (via bound context), MapFoliageSupportBuilder. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxRuleCandidates">Demand sites: MapFoliageClearance. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxMaskCandidates">Demand sites: MapFoliageClearance. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxQueryChunks">Demand sites: MapFoliageDistribution. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxQuerySamples">Demand sites: MapFoliageInstanceBuilder, MapFoliageRetentionBudget, NativeMapFoliage. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxQueryRetainedBytes">Demand sites: MapFoliageColumnBudget, MapFoliageGenerationAdmission, MapFoliageRenderCache, MapFoliageRetentionBudget. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxDifferentialCandidates">Demand sites: MapFoliageDifferential. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxChangeRows">Demand sites: MapFoliageDifferential. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxComparisonPreparedChunks">Demand sites: MapFoliageDistribution. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxComparisonRetainedBytes">Demand sites: MapFoliageDistribution. See docs/CAPACITY-DEMAND.md.</param>
public sealed record MapFoliageLimits(int MaxCandidates = 4_000_000, int MaxSamples = 262_144, int MaxPrepareScopes = 256,
    int MaxFaces = 262_144, int MaxRuleCandidates = 4096, int MaxMaskCandidates = 4096, int MaxQueryChunks = 64,
    long MaxQuerySamples = 262_144, long MaxQueryRetainedBytes = 67_108_864, long MaxDifferentialCandidates = 64_000_000,
    long MaxChangeRows = 4_000_000, int MaxComparisonPreparedChunks = 64, long MaxComparisonRetainedBytes = 134_217_728)
{
    // Reconciliation admits actual preparation allocations before emission. Ordinary chunk preparation owns no
    // aggregate cache, and leaves this callback unset.
    internal Action<long>? ChargePreparation { get; init; }
    internal Action<long>? AllocateInstances { get; init; }
    internal Action<long>? ReleaseInstances { get; init; }
}

/// <summary>Charged foliage work. Byte charges are explicit conservative accounting, never a measured allocation.</summary>
public sealed record MapFoliageWork(long Candidates, long Samples, long PrepareScopes, long Faces, long RuleCandidates,
    long MaskCandidates, long SurfaceQueries, long RetainedBytes);
