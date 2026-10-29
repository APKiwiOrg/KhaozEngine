namespace KhaozEngine.Physics;

/// <summary>Support-query and outer movement work recorded by an opt-in capture. New counters append stable numeric identities.</summary>
public enum SupportQueryCounter
{
    Find = 0,
    Proposals = 1,
    Volumes = 2,
    SelectedStatics = 3,
    MeshLeaves = 4,
    Polygons = 5,
    Members = 6,
    Extrema = 7,
    IntervalFallbacks = 8,
    ExactFallbacks = 9,
    JoinPairs = 10,
    JoinsProved = 11,
    JoinsPruned = 12,
    Unsupported = 13,
    Ambiguous = 14,
    CapacityExceeded = 15,
    AnalyticSamples = 16,
    Bindings = 17,
    PhysicsLeases = 18,
    MovementLeases = 19,
    ProducerPins = 20,
    GroundSubsteps = 21,
    MediumRetries = 22,
    ModeSegments = 23,
    /// <summary>Installed support world forms built, published or not.</summary>
    WorldFormBuilds = 24,
    /// <summary>Footprint polygons that passed the bounds and column reject and began per-query operands.</summary>
    PolygonOperands = 25,
    /// <summary>Contact members the certified separation prefilter decided negative without the contact kernel.</summary>
    PrefilterRejects = 26,
    /// <summary>Joins of two faces of one installed convex leaf whose meeting along a captured shared edge came
    /// from the world form's topology. <see cref="JoinsProved"/> counts the geometric joins only.</summary>
    TopologyJoins = 27,
    /// <summary>Interval samples of the curved tangent contact kernel, each one closest point evaluation over a
    /// height span or at one height of a probe axis.</summary>
    TangentSamples = 28,
    /// <summary>Member pairs a foot support footprint query left unjoined because support certification never reads
    /// their join. <see cref="JoinPairs"/> counts the pairs it evaluated.</summary>
    JoinsSkipped = 29,
    /// <summary>Mesh triangles materialized, including uncached reference captures.</summary>
    MeshTriangleCaptures = 30,
    /// <summary>Finds an exact repeat request served from its interval's memo. <see cref="Find"/> counts them too.</summary>
    FindMemoHits = 31,
    /// <summary>Vertical footprint polygons whose admission and column verdict the certified segment test decided
    /// without the extrema kernel.</summary>
    AdmissionCertificates = 32,
    /// <summary>Extrema evaluations served by the exact horizontal rectangle stratum.</summary>
    StairStratum = 33,
    /// <summary>Contact member verdicts and contact heights the exact axis-aligned rectangle certificates decided
    /// without the contact kernel or the complete lowest-height evaluator.</summary>
    RectangleContact = 34,
    /// <summary>Face-only arithmetic values read from installed world faces. Each projected edge length, normalized
    /// normal, normal publication, plane publication and exhaustive proposal normal length counts once per read.</summary>
    SharedMemberFacts = 35,
    FootprintReuseHits = 36,
    FootprintReuseMisses = 37,
    FootprintReuseStores = 38,
    FootprintReuseBudgetSkips = 39,
    EdgeSteps = 40,
    InitialHoldReuseHits = 41,
    /// <summary>Calls to NpcGroundMovement.Hold with requested Legacy tuning, after the context null guard.</summary>
    NpcLegacyHolds = 42,
    /// <summary>Calls to NpcGroundMovement.Hold with requested Contact tuning, after the context null guard.</summary>
    NpcContactHolds = 43,
    /// <summary>Calls to NpcGroundMovement.Step with requested Legacy tuning, including steering that requests no travel.</summary>
    NpcLegacySteps = 44,
    /// <summary>Calls to NpcGroundMovement.Step with requested Contact tuning, including steering that requests no travel.</summary>
    NpcContactSteps = 45,
    /// <summary>Authoritative PlayerMovementSystem per-entity Steps with Legacy tuning, including idle and rejected Steps.
    /// Direct PlayerMoveSimulator consumers, including prediction, do not count.</summary>
    PlayerLegacySteps = 46,
    /// <summary>Authoritative PlayerMovementSystem per-entity Steps with Contact tuning, including idle and rejected Steps.
    /// Direct PlayerMoveSimulator consumers, including prediction, do not count.</summary>
    PlayerContactSteps = 47,
    /// <summary>Directed Drop candidates reaching the dedicated fall proof phase.</summary>
    DropCandidates = 48,
    /// <summary>Usable walking modes attempted by full-tuning Drop proofs.</summary>
    DropWalkProofs = 49,
    /// <summary>Usable running modes attempted by full-tuning Drop proofs.</summary>
    DropRunProofs = 50,
    /// <summary>Real Contact fall-proof movement slices, including initial and post-landing Holds.</summary>
    DropSteps = 51,
}
