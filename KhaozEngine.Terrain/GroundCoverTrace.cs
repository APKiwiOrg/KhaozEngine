namespace KhaozEngine.Terrain;

/// <summary>One ground-cover lattice candidate. Every channel is a pure function of the lattice address and seed.
/// The jitter channels are computed with the candidate because its world point needs them. The other five are
/// hashed only when read, so the generator pays for exactly the channels its decision uses and reading any of
/// them never changes another candidate or the surface call order. Channel values lie in the range 0 up to but
/// excluding 1.</summary>
public readonly record struct GroundCoverCandidate
{
    readonly int _seed;

    internal GroundCoverCandidate(int x, int z, int seed, float spacing)
    {
        X = x;
        Z = z;
        _seed = seed;
        JitterX = GroundCoverCandidateEvaluator.Channel(x, z, seed, GroundCoverCandidateEvaluator.JitterXChannel);
        JitterZ = GroundCoverCandidateEvaluator.Channel(x, z, seed, GroundCoverCandidateEvaluator.JitterZChannel);
        WorldX = (x + JitterX - 0.5f) * spacing;
        WorldZ = (z + JitterZ - 0.5f) * spacing;
    }

    /// <summary>Lattice address column.</summary>
    public int X { get; }
    /// <summary>Lattice address row.</summary>
    public int Z { get; }
    /// <summary>Jittered world X, which may lie outside the cell's generation area.</summary>
    public float WorldX { get; }
    /// <summary>Jittered world Z.</summary>
    public float WorldZ { get; }
    /// <summary>X jitter channel.</summary>
    public float JitterX { get; }
    /// <summary>Z jitter channel.</summary>
    public float JitterZ { get; }
    /// <summary>Density channel. A sample keeps the candidate only when this is below its density.</summary>
    public float DensityPick => Read(GroundCoverCandidateEvaluator.DensityChannel);
    /// <summary>Weighted model channel.</summary>
    public float ModelPick => Read(GroundCoverCandidateEvaluator.ModelChannel);
    /// <summary>Uniform scale channel.</summary>
    public float ScalePick => Read(GroundCoverCandidateEvaluator.ScaleChannel);
    /// <summary>Yaw channel, a fraction of one full turn.</summary>
    public float YawPick => Read(GroundCoverCandidateEvaluator.YawChannel);
    /// <summary>Stable thinning rank channel.</summary>
    public float Rank => Read(GroundCoverCandidateEvaluator.RankChannel);

    float Read(int channel) => GroundCoverCandidateEvaluator.Channel(X, Z, _seed, channel);
}

/// <summary>What a traced surface reports for one candidate. A null sample is a known outside-domain rejection,
/// never a zero-height floor. A surface that cannot answer throws instead.</summary>
/// <param name="Sample">The sampled surface point, or null when the candidate lies outside the known domain.</param>
public readonly record struct GroundCoverSurfaceInput(GroundCoverSample? Sample);

/// <summary>Why a candidate did or did not produce an instance.</summary>
public enum GroundCoverDecisionKind
{
    /// <summary>The jittered point lies outside the half-open generation area. The surface was not consulted.</summary>
    OutsideArea,
    /// <summary>The surface reported the point outside its known domain.</summary>
    OutsideSurface,
    /// <summary>The sampled density is exactly zero.</summary>
    ZeroDensity,
    /// <summary>The density channel is at or above the sampled density.</summary>
    DensityRejected,
    /// <summary>The candidate produced an instance.</summary>
    Accepted,
}

/// <summary>One traced candidate decision. Rows are provisional until the enclosing generation or query result is
/// complete.</summary>
/// <param name="Candidate">The candidate and its channels.</param>
/// <param name="Kind">The decision.</param>
/// <param name="Sample">The surface sample as reported, null for outside-area and outside-surface rows.</param>
/// <param name="Instance">The produced instance, only for accepted rows.</param>
/// <param name="Scale">Uniform scale from the scale channel for accepted rows, zero otherwise.</param>
/// <param name="YawRadians">Yaw in radians from the yaw channel for accepted rows, zero otherwise.</param>
public readonly record struct GroundCoverDecision(
    GroundCoverCandidate Candidate,
    GroundCoverDecisionKind Kind,
    GroundCoverSample? Sample,
    GroundCoverInstance? Instance,
    float Scale,
    float YawRadians);
