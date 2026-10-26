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
