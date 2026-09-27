using System;

namespace KhaozEngine.Render3D.Internal;

/// <summary>
/// The owner of everything a temporal technique carries from one frame to the next, and of whether that state can be
/// trusted this frame (docs/design/TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24.md, section 2). It holds the validity, and
/// the temporal resolve's history targets (TemporalHistory.Targets.cs). The targets are created while the resolve runs
/// and kept across <see cref="Invalidate"/>, which only stops them being read for a frame. They are released when the
/// resolve stops and recreated when a size changes.
/// <para>
/// It lives with <c>Scene3D</c>'s per-frame state rather than in <c>RenderResources</c>, so the resource rebuilds that
/// change nothing temporal (a bloom toggle, the distortion field coming and going) do not discard it. The scene decides
/// when to reset and hands over one reason per frame. This type only records it.
/// </para>
/// </summary>
internal sealed partial class TemporalHistory
{
    /// <summary>Whether the previous frame's state can be read this frame. False until a frame has completed, and for
    /// the one frame after every reset.</summary>
    public bool IsValid { get; private set; }

    /// <summary>Why the history was last reset. <see cref="TemporalResetReason.None"/> until the first reset, then the
    /// latest reason, kept after the history recovers so diagnostics can say what the last reset was.</summary>
    public TemporalResetReason LastReset { get; private set; }

    /// <summary>Drop the history for this frame: the frame renders as if nothing came before it.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is <see cref="TemporalResetReason.None"/>
    /// or not a defined reason.</exception>
    public void Invalidate(TemporalResetReason reason)
    {
        if (reason <= TemporalResetReason.None || reason > TemporalResetReason.DeviceReset)
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "A reset needs a defined reason other than None.");
        IsValid = false;
        LastReset = reason;
    }

    /// <summary>The frame rendered, so its state is what the next frame reprojects from.</summary>
    public void MarkValidAfterFrame() => IsValid = true;
}
