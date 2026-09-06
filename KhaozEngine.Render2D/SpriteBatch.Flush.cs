using System;

namespace KhaozEngine.Render2D;

public sealed partial class SpriteBatch
{
    private bool _batchActive;

    /// <summary>
    /// Submits and clears all queued draws while leaving the current batch open and reusable. The active
    /// transform, sampler, blend mode, texture-grouping mode, and scissor remain in effect. This is an ordering
    /// boundary for scoping <see cref="GroupByTexture"/> to part of a Begin/End pass.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// There is no active batch. Call a <c>Begin</c> overload before flushing, and do not flush after
    /// <see cref="End"/> until the next <c>Begin</c>.
    /// </exception>
    public void Flush()
    {
        if (!_batchActive)
            throw new InvalidOperationException("SpriteBatch.Flush requires an active Begin/End batch.");
        FlushCore();
    }
}
