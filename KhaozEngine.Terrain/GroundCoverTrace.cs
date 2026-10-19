namespace KhaozEngine.Terrain;

/// <summary>One ground-cover lattice candidate and its seven stateless channel values. The channels depend only on
/// the lattice address and seed, so reading them never changes another candidate or the surface call order.</summary>
/// <param name="X">Lattice address column.</param>
/// <param name="Z">Lattice address row.</param>
/// <param name="WorldX">Jittered world X, which may lie outside the cell's generation area.</param>
/// <param name="WorldZ">Jittered world Z.</param>
/// <param name="JitterX">X jitter channel in the range 0 up to but excluding 1.</param>
/// <param name="JitterZ">Z jitter channel.</param>
/// <param name="DensityPick">Density channel. A sample keeps the candidate only when this is below its density.</param>
/// <param name="ModelPick">Weighted model channel.</param>
/// <param name="ScalePick">Uniform scale channel.</param>
/// <param name="YawPick">Yaw channel, a fraction of one full turn.</param>
/// <param name="Rank">Stable thinning rank channel.</param>
public readonly record struct GroundCoverCandidate(
    int X,
    int Z,
    float WorldX,
    float WorldZ,
    float JitterX,
    float JitterZ,
    float DensityPick,
    float ModelPick,
    float ScalePick,
    float YawPick,
    float Rank);

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

/// <summary>One traced candidate decision. Scale and yaw are the candidate's channel-derived values whatever the
/// kind. Rows are provisional until the enclosing generation or query result is complete.</summary>
/// <param name="Candidate">The candidate and its channels.</param>
/// <param name="Kind">The decision.</param>
/// <param name="Sample">The surface sample as reported, null for outside-area and outside-surface rows.</param>
/// <param name="Instance">The produced instance, only for accepted rows.</param>
/// <param name="Scale">Uniform scale from the scale channel.</param>
/// <param name="YawRadians">Yaw in radians from the yaw channel.</param>
public readonly record struct GroundCoverDecision(
    GroundCoverCandidate Candidate,
    GroundCoverDecisionKind Kind,
    GroundCoverSample? Sample,
    GroundCoverInstance? Instance,
    float Scale,
    float YawRadians);
