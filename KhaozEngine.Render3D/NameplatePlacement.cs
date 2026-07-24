using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Caller-held state for one plate/entity's <see cref="NameplateEdgeBehavior.Deflect"/> hysteresis, carried
    /// across frames by <see cref="NameplateRenderer"/>'s stateful <c>Draw</c> overload. A default instance means
    /// not deflected, so a freshly introduced plate starts in the normal above-anchor placement. Mutable: pass by
    /// <c>ref</c> so <see cref="NameplatePlacement.Place"/> can update it in place without a per-frame allocation.
    /// </summary>
    /// <remarks>
    /// One instance belongs to exactly one plate. Sharing an instance across plates would let one plate's
    /// deflection state leak into another's placement (e.g. a plate that never overflows would inherit a stuck
    /// "deflected" flag from a different entity), so the renderer's contract is one <see
    /// cref="NameplatePlacementState"/> per tracked entity, held by the caller alongside whatever else it already
    /// keeps per-entity (health, position, etc).
    /// </remarks>
    public struct NameplatePlacementState
    {
        // 0 = not deflected, 1 = deflected left of the anchor, 2 = deflected right of the anchor.
        // Internal (not private) so NameplatePlacement, a different type in this assembly, can read and update it
        // directly without a public setter leaking the side encoding to consumers.
        internal byte Side;

        /// <summary>True while the plate is placed beside the anchor rather than in its normal above-anchor spot.
        /// Exposed for tests and for a consumer that wants to react to a plate's edge state (e.g. skip a
        /// look-at-target animation while a plate is deflected).</summary>
        public readonly bool IsDeflected => Side != 0;
    }

    /// <summary>
    /// Pure, GPU-free placement math for a <see cref="Nameplate"/> panel: the baseline centred-above-anchor rect
    /// <see cref="NameplateRenderer"/> has always used, plus the two opt-in <see cref="NameplateEdgeBehavior"/>
    /// modes that keep it inside the viewport. No camera, no device, so it is headless-testable like <see
    /// cref="NameplateLayout"/>. The renderer projects the world point and calls <see cref="Place"/> with the
    /// resulting pixel to get the final panel rect.
    /// </summary>
    /// <remarks>
    /// <see cref="NameplateEdgeBehavior.Deflect"/> exists instead of always clamping because a downward clamp
    /// covers the creature's face in exactly the case that triggers it: a close-up look-up at a tall or raised
    /// target, where the plate's natural position is above the top edge. Moving the plate beside the anchor keeps
    /// it readable without occluding the thing it labels. The hysteresis band exists because a plate flipping
    /// between "above" and "beside" on alternate frames as the camera jiggles at the threshold is a worse look
    /// than one that briefly overflows, so leaving deflection needs more headroom than entering it required.
    /// </remarks>
    public static class NameplatePlacement
    {
        /// <summary>Horizontal gap in pixels between the anchor pixel and the near edge of a deflected plate.</summary>
        internal const float DeflectGap = 8f;

        /// <summary>
        /// The panel rect (x, y, width, height), matching <see cref="NameplateRenderer"/>'s panel-rect convention,
        /// for a plate of <paramref name="size"/> centred above <paramref name="anchor"/> under <paramref
        /// name="style"/>'s <see cref="NameplateStyle.EdgeBehavior"/>. <paramref name="state"/> is this plate's
        /// carried-over deflection state. Pass a fresh <see cref="NameplatePlacementState"/> when <see
        /// cref="NameplateStyle.EdgeBehavior"/> is not <see cref="NameplateEdgeBehavior.Deflect"/>, since only
        /// Deflect reads or writes it.
        /// </summary>
        public static Vector4 Place(
            Vector2 anchor, Vector2 size, int viewportWidth, int viewportHeight,
            in NameplateStyle style, ref NameplatePlacementState state)
        {
            float baselineLeft = anchor.X - size.X * 0.5f;
            float baselineTop = anchor.Y - size.Y;

            if (style.EdgeBehavior == NameplateEdgeBehavior.None)
            {
                state = default;
                return new Vector4(baselineLeft, baselineTop, size.X, size.Y);
            }

            float m = style.EdgeMargin;
            float w = size.X;
            float h = size.Y;
            float maxLeft = viewportWidth - m - w;
            float maxTop = viewportHeight - m - h;

            if (style.EdgeBehavior == NameplateEdgeBehavior.Clamp)
            {
                state = default;
                return new Vector4(ClampAxis(baselineLeft, m, maxLeft), ClampAxis(baselineTop, m, maxTop), w, h);
            }

            // Deflect: clamp horizontally like Clamp, but move beside the anchor instead of clamping down over
            // it when the normal above-anchor placement overflows the top edge.
            float band = style.EdgeHysteresis > 0f ? style.EdgeHysteresis : h * 0.5f;

            if (!state.IsDeflected)
            {
                if (baselineTop >= m)
                    return new Vector4(ClampAxis(baselineLeft, m, maxLeft), ClampAxis(baselineTop, m, maxTop), w, h);

                // Entering deflection: pick whichever side of the anchor has more room, tie favouring the right.
                float roomRightEnter = (viewportWidth - m) - (anchor.X + DeflectGap);
                float roomLeftEnter = (anchor.X - DeflectGap) - m;
                state.Side = roomRightEnter >= roomLeftEnter ? SideRight : SideLeft;
            }
            else if (baselineTop >= m + band)
            {
                // Cleared the hysteresis band: leave deflection and return to the normal placement.
                state = default;
                return new Vector4(ClampAxis(baselineLeft, m, maxLeft), ClampAxis(baselineTop, m, maxTop), w, h);
            }
            else
            {
                // Still deflected: the side is sticky, and only switches when it no longer fits AND the other
                // side does, so a plate does not flip back and forth as the anchor drifts a few pixels.
                float roomRight = (viewportWidth - m) - (anchor.X + DeflectGap);
                float roomLeft = (anchor.X - DeflectGap) - m;
                if (state.Side == SideRight && roomRight < w && roomLeft >= w)
                    state.Side = SideLeft;
                else if (state.Side == SideLeft && roomLeft < w && roomRight >= w)
                    state.Side = SideRight;
            }

            float deflectedLeft = state.Side == SideRight ? anchor.X + DeflectGap : anchor.X - DeflectGap - w;
            float deflectedTop = anchor.Y - h * 0.5f;
            return new Vector4(ClampAxis(deflectedLeft, m, maxLeft), ClampAxis(deflectedTop, m, maxTop), w, h);
        }

        // value clamped to [min, max]: Min first then Max, so an inverted range (max < min, i.e. the plate is
        // bigger than the viewport minus margins) resolves to min, the near/top edge, instead of throwing like
        // Math.Clamp would.
        static float ClampAxis(float value, float min, float max)
        {
            value = MathF.Min(value, max);
            value = MathF.Max(value, min);
            return value;
        }

        const byte SideLeft = 1;
        const byte SideRight = 2;
    }
}
