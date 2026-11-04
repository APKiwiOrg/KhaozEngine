using System;

namespace KhaozEngine.Windowing;

/// <summary>Private archive draw cuts. Disabled is the normal renderer, None is a sampled baseline.</summary>
public enum GBufferGate
{
    Disabled = -3,
    None = 0,
    Rigid = 1,
    Splat = 2,
    TileGround = 3,
    Foliage = 4,
    Skinned = 5,
}

/// <summary>Private recorder seam, registered before Run. Both operations run on the callback thread and must not
/// allocate or do I/O. Only the main opaque G-buffer draws may consult the selected gate.</summary>
public interface IFrameDiagnosticsGBufferGates
{
    /// <summary>Select one gate for subsequent renders. Disabled restores ordinary rendering.</summary>
    void Apply(GBufferGate gate);
    /// <summary>Consume rendered state since the last call, resetting it to no frame. Returns the gate id,
    /// -1 for no rendered frame, or -2 for disagreeing renders. Fill all five skipped-draw counts, in gate-id order.</summary>
    int ConsumeApplied(Span<long> skippedDraws);
}
