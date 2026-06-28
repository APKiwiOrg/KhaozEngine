using System.Numerics;

namespace KhaozEngine.Netcode;

/// <summary>
/// A predicted local state whose position participates in reconciliation error smoothing.
/// </summary>
/// <typeparam name="TSelf">The implementing state type (CRTP), so WithPosition stays strongly typed.</typeparam>
public interface IPredictedState<TSelf>
{
    /// <summary>Planar (ground-plane) world position used to measure and gate reconciliation error.</summary>
    Vector2 Position { get; }

    /// <summary>
    /// Vertical axis (height) carried through render smoothing alongside the planar <see cref="Position"/>, so a
    /// jump/fall eases instead of stair-stepping or popping. Defaults to 0 for purely planar states that have no
    /// vertical axis - those keep their old behaviour with no change required.
    /// </summary>
    float Vertical => 0f;

    /// <summary>Returns a copy of this state with the planar <paramref name="position"/> applied (vertical unchanged).</summary>
    TSelf WithPosition(Vector2 position);

    /// <summary>
    /// Returns a copy with both the smoothed planar <paramref name="position"/> and the <paramref name="vertical"/>
    /// axis applied, used to build the rendered (presentation) state. Defaults to <see cref="WithPosition"/> (the
    /// vertical is ignored) so a purely planar state needs no extra implementation.
    /// </summary>
    TSelf WithRenderState(Vector2 position, float vertical) => WithPosition(position);
}
