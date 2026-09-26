using System;

namespace KhaozEngine.Render3D.Internal;

/// <summary>
/// Which reset reason is reported when several triggers fire in one frame
/// (docs/design/TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24.md, section 2). One reason is reported per frame, the root cause
/// over the effects it causes: a colour-format rebuild, then the anti-aliasing selection, then the render scale, then
/// the internal size those settings may also have changed, then an explicit cut over a detected one. The rank is a
/// reason's place in <see cref="HighestFirst"/>, never its declared value, which follows a different order.
/// </summary>
internal static class TemporalResetPrecedence
{
    static readonly TemporalResetReason[] Order =
    [
        TemporalResetReason.FirstFrame,
        TemporalResetReason.DeviceReset,
        TemporalResetReason.AntiAliasing,
        TemporalResetReason.RenderScale,
        TemporalResetReason.Resize,
        TemporalResetReason.CameraCutRequested,
        TemporalResetReason.CameraCutDetected,
    ];

    /// <summary>Every reason other than <see cref="TemporalResetReason.None"/>, highest ranked first.</summary>
    internal static ReadOnlySpan<TemporalResetReason> HighestFirst => Order;

    /// <summary>The reason to report when both <paramref name="a"/> and <paramref name="b"/> fired this frame.
    /// <see cref="TemporalResetReason.None"/> means no trigger and yields to any reason, so a detector folds each
    /// trigger it finds into a running result that starts at <see cref="TemporalResetReason.None"/>, in any order.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A reason has no place in the ranking.</exception>
    internal static TemporalResetReason Higher(TemporalResetReason a, TemporalResetReason b) => Rank(a) <= Rank(b) ? a : b;

    static int Rank(TemporalResetReason reason)
    {
        if (reason == TemporalResetReason.None) return int.MaxValue;
        for (int rank = 0; rank < Order.Length; rank++)
            if (Order[rank] == reason) return rank;
        throw new ArgumentOutOfRangeException(nameof(reason), reason, "The reason has no place in the reset precedence.");
    }
}
