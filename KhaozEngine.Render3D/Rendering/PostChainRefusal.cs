namespace KhaozEngine.Render3D;

/// <summary>The stable reasons PostChain input or state is refused.</summary>
public enum PostChainRefusal
{
    /// <summary>Refuses targets reporting a source-slot count the chain cannot build.</summary>
    SourceSlotCountUnsupported,

    /// <summary>Refuses running targets not bound at their current generation.</summary>
    TargetsNotBoundAtCurrentGeneration,

    /// <summary>Refuses running a source slot without built resource sets.</summary>
    SourceSlotNotBuilt,

    /// <summary>Refuses the pre-temporal outline pass without its internal ping pair.</summary>
    TemporalPingPairMissing,
}
