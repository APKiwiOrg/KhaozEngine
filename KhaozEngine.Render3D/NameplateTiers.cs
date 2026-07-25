using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// What to draw for one entity's nameplate this frame: nothing, the name only, or the full plate. Resolved by
    /// <see cref="NameplateTiers.Resolve"/> from distance and gaze so a crowd of nearby, unfocused entities does
    /// not draw a stack of full plates the player never reads.
    /// </summary>
    public enum NameplateTier : byte
    {
        /// <summary>Draw nothing.</summary>
        Hidden = 0,
        /// <summary>The panel-less name-only look: pair with <see cref="NameplateStyle.TextOnly"/> and a <see
        /// cref="Nameplate"/> with no bars.</summary>
        Text = 1,
        /// <summary>The whole plate: panel, title, and bars.</summary>
        Full = 2,
    }

    /// <summary>
    /// Caller-held state for one entity's tier hysteresis, carried across frames by whichever loop calls <see
    /// cref="NameplateTiers.Resolve"/>, alongside whatever else it already keeps per-entity (the same contract
    /// <see cref="NameplatePlacementState"/> already uses). A default instance resolves to <see
    /// cref="NameplateTier.Hidden"/>, so a first-seen entity resolves upward on its first call: entering a tier is
    /// always at the raw edge, so starting Hidden costs nothing.
    /// </summary>
    public struct NameplateTierState
    {
        // Stored as the enum's own byte value rather than a separate set of flags, so the whole hysteresis state
        // (which tier, and therefore which edge and band apply next) reads off one field.
        internal byte Value;

        /// <summary>The tier this entity resolved to on its last <see cref="NameplateTiers.Resolve"/> call.</summary>
        public readonly NameplateTier Tier => (NameplateTier)Value;
    }

    /// <summary>
    /// Tuning for <see cref="NameplateTiers.Resolve"/>: the distance ladder and the look-at gate, each with its own
    /// hysteresis band.
    /// </summary>
    public readonly struct NameplateTierConfig
    {
        /// <summary>The plate is <see cref="NameplateTier.Full"/> at or under this distance.</summary>
        public float FullDistance { get; init; }
        /// <summary>The plate is at least <see cref="NameplateTier.Text"/> out to this distance. 0 means unbounded:
        /// text stays visible at any distance the caller's own cull ring still lets through.</summary>
        public float TextDistance { get; init; }
        /// <summary>Extra distance beyond a ladder edge required to leave the nearer tier. Values &lt;= 0 derive
        /// <see cref="FullDistance"/> * 0.1, so the band scales with the tier's own range instead of needing a
        /// per-config tune.</summary>
        public float DistanceHysteresis { get; init; }
        /// <summary>The look-at gate: a normalized centre-ellipse radius (see <see cref="NameplateTiers.Resolve"/>)
        /// the projected focus point must fall inside for the plate to be eligible to show at all. Values &lt;= 0
        /// disable the gate entirely, so distance alone decides the tier.</summary>
        public float FocusRadius { get; init; }
        /// <summary>Extra normalized radius beyond <see cref="FocusRadius"/> required to hide an already-visible
        /// plate. Values &lt;= 0 derive 0.15.</summary>
        public float FocusHysteresis { get; init; }

        /// <summary>A readable default: full plates within 15 units, a permissive 0.6-radius look-at gate, both
        /// bands derived rather than tuned.</summary>
        public static NameplateTierConfig Default => new NameplateTierConfig
        {
            FullDistance = 15f,
            TextDistance = 0f,
            DistanceHysteresis = 0f,
            FocusRadius = 0.6f,
            FocusHysteresis = 0f,
        };
    }

    /// <summary>
    /// Pure tier resolution: no camera, no device, no draw calls, so it is headless-testable like <see
    /// cref="NameplatePlacement"/>. A caller resolves once per entity per frame and only draws the plate when the
    /// result is not <see cref="NameplateTier.Hidden"/> (and only the name, panel-less, when it is <see
    /// cref="NameplateTier.Text"/>).
    /// </summary>
    /// <remarks>
    /// Every boundary here is asymmetric, entering at the raw edge and exiting only past edge-plus-band, the same
    /// stability contract as <see cref="NameplatePlacement"/>'s deflect hysteresis: a value jittering at a
    /// threshold (a player standing still at exactly the tier boundary, or looking near the edge of the focus
    /// ellipse) must not flip the presentation frame to frame.
    ///
    /// <see cref="Resolve"/>'s <c>focusPixel</c> should be the projected BODY of the entity, not the plate
    /// anchor. A close-up look-up at a tall creature puts the head anchor (the plate's usual anchor point) near
    /// the screen edge in exactly the case where the player IS looking at it, which would fail the gate for the
    /// wrong reason.
    /// </remarks>
    public static class NameplateTiers
    {
        /// <summary>
        /// Resolves the tier to draw this frame for one entity, in this order: the <paramref name="pinned"/>
        /// override, the <paramref name="onScreen"/> cull, the look-at gate, then the distance ladder. <paramref
        /// name="state"/> is this entity's carried-over hysteresis state (see <see cref="NameplateTierState"/>).
        /// Pass one instance per tracked entity across frames.
        /// </summary>
        /// <param name="focusPixel">The projected screen-space point the gaze check measures against (the
        /// entity's body, not its plate anchor, see the remarks above).</param>
        /// <param name="onScreen">Whether the entity's anchor projects onto the viewport at all.</param>
        /// <param name="distance">Player-to-entity distance in the caller's world units.</param>
        /// <param name="viewportWidth">Viewport width in pixels, for normalizing the focus ellipse.</param>
        /// <param name="viewportHeight">Viewport height in pixels, for normalizing the focus ellipse.</param>
        /// <param name="config">Tuning for the distance ladder and the focus gate.</param>
        /// <param name="pinned">True forces <see cref="NameplateTier.Full"/> regardless of everything else (the
        /// caller's own override, e.g. a hostile target the player is fighting, whose health bar must stay
        /// trackable no matter where the player is looking).</param>
        /// <param name="state">This entity's carried hysteresis state, updated in place.</param>
        public static NameplateTier Resolve(
            Vector2 focusPixel, bool onScreen, float distance, int viewportWidth, int viewportHeight,
            in NameplateTierConfig config, bool pinned, ref NameplateTierState state)
        {
            if (pinned)
            {
                state.Value = (byte)NameplateTier.Full;
                return NameplateTier.Full;
            }

            if (!onScreen)
            {
                state.Value = (byte)NameplateTier.Hidden;
                return NameplateTier.Hidden;
            }

            if (config.FocusRadius > 0f)
            {
                float halfW = viewportWidth * 0.5f;
                float halfH = viewportHeight * 0.5f;
                float dx = (focusPixel.X - halfW) / halfW;
                float dy = (focusPixel.Y - halfH) / halfH;
                float r = MathF.Sqrt(dx * dx + dy * dy);
                float focusBand = config.FocusHysteresis > 0f ? config.FocusHysteresis : 0.15f;

                if (state.Tier == NameplateTier.Hidden)
                {
                    // Not yet visible: only the raw edge lets it in, so a plate does not appear the instant the
                    // gaze grazes the outer edge of the hysteresis band.
                    if (r > config.FocusRadius)
                        return NameplateTier.Hidden;
                }
                else if (r > config.FocusRadius + focusBand)
                {
                    // Already visible: only clearing the band hides it, so a gaze wobbling right at the raw
                    // edge does not flicker the plate off and on.
                    state.Value = (byte)NameplateTier.Hidden;
                    return NameplateTier.Hidden;
                }
            }

            float distanceBand = config.DistanceHysteresis > 0f ? config.DistanceHysteresis : config.FullDistance * 0.1f;
            float textDistance = config.TextDistance;

            if (textDistance > 0f)
            {
                if (state.Tier == NameplateTier.Hidden)
                {
                    if (distance > textDistance)
                        return NameplateTier.Hidden;
                }
                else if (distance > textDistance + distanceBand)
                {
                    state.Value = (byte)NameplateTier.Hidden;
                    return NameplateTier.Hidden;
                }
            }

            NameplateTier resolved = state.Tier == NameplateTier.Full
                ? (distance > config.FullDistance + distanceBand ? NameplateTier.Text : NameplateTier.Full)
                : (distance <= config.FullDistance ? NameplateTier.Full : NameplateTier.Text);

            state.Value = (byte)resolved;
            return resolved;
        }
    }
}
