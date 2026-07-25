using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests;

// Headless coverage for NameplateTiers.Resolve, the pure tier-resolution state machine that decides what to draw
// per entity (NameplateTier.Hidden/Text/Full) from distance and gaze, plus the caller pin and the
// NameplateStyle.TextOnly preset it pairs with. Companion to NameplatePlacementTests (which covers WHERE the
// plate goes, not WHETHER it draws) - both share the enter-at-the-edge, exit-past-the-band hysteresis contract.
public sealed class NameplateTierTests
{
    const int ViewportWidth = 800;
    const int ViewportHeight = 600;
    static readonly Vector2 Center = new Vector2(400f, 300f);

    // Builds a focus pixel on the horizontal axis (dy = 0) at the given normalized ellipse radius, so tests can
    // target the focus gate's exact edge and band without recomputing the ellipse math inline.
    static Vector2 FocusAtRadius(float r) => new Vector2(400f + r * 400f, 300f);

    [Fact]
    public void Default_hasExpectedValues_andStateDefaultsToHidden()
    {
        NameplateTierConfig config = NameplateTierConfig.Default;

        Assert.Equal(15f, config.FullDistance, 3);
        Assert.Equal(0f, config.TextDistance, 3);
        Assert.Equal(0f, config.DistanceHysteresis, 3);
        Assert.Equal(0.6f, config.FocusRadius, 3);
        Assert.Equal(0f, config.FocusHysteresis, 3);
        Assert.Equal(NameplateTier.Hidden, default(NameplateTierState).Tier);
    }

    [Fact]
    public void Resolve_pinned_farAndUnfocused_forcesFull_thenUnpinned_hidesViaFocusGate_inOneTransition()
    {
        NameplateTierConfig config = NameplateTierConfig.Default;
        var state = new NameplateTierState();
        Vector2 corner = new Vector2(10f, 10f);

        NameplateTier pinnedTier = NameplateTiers.Resolve(
            corner, onScreen: true, distance: 80f, ViewportWidth, ViewportHeight, config, pinned: true, ref state);
        Assert.Equal(NameplateTier.Full, pinnedTier);
        Assert.Equal(NameplateTier.Full, state.Tier);

        // Same inputs, unpinned: the pin was the only thing keeping this visible, and it drops straight to
        // Hidden via the focus gate (unfocused far corner) rather than pausing at Text on the way down.
        NameplateTier unpinnedTier = NameplateTiers.Resolve(
            corner, onScreen: true, distance: 80f, ViewportWidth, ViewportHeight, config, pinned: false, ref state);
        Assert.Equal(NameplateTier.Hidden, unpinnedTier);
        Assert.Equal(NameplateTier.Hidden, state.Tier);
    }

    [Fact]
    public void Resolve_offscreen_returnsHidden_evenAtCloseDistance()
    {
        NameplateTierConfig config = NameplateTierConfig.Default;
        var state = new NameplateTierState();

        NameplateTier tier = NameplateTiers.Resolve(
            Center, onScreen: false, distance: 5f, ViewportWidth, ViewportHeight, config, pinned: false, ref state);

        Assert.Equal(NameplateTier.Hidden, tier);
    }

    [Fact]
    public void Resolve_offscreenAndPinned_stillForcesFull()
    {
        NameplateTierConfig config = NameplateTierConfig.Default;
        var state = new NameplateTierState();

        NameplateTier tier = NameplateTiers.Resolve(
            Center, onScreen: false, distance: 5f, ViewportWidth, ViewportHeight, config, pinned: true, ref state);

        Assert.Equal(NameplateTier.Full, tier);
    }

    [Fact]
    public void Resolve_focusGate_hiddenState_entersOnlyAtOrBelowRawEdge()
    {
        NameplateTierConfig config = NameplateTierConfig.Default;

        // r = 0.61, just above FocusRadius 0.6: stays Hidden no matter how close the entity is.
        var stateAbove = new NameplateTierState();
        NameplateTier above = NameplateTiers.Resolve(
            FocusAtRadius(0.61f), onScreen: true, distance: 5f, ViewportWidth, ViewportHeight, config, pinned: false, ref stateAbove);
        Assert.Equal(NameplateTier.Hidden, above);

        // r = 0.6, exactly at the raw edge: enters, and the close distance resolves the ladder to Full.
        var stateAt = new NameplateTierState();
        NameplateTier at = NameplateTiers.Resolve(
            FocusAtRadius(0.6f), onScreen: true, distance: 5f, ViewportWidth, ViewportHeight, config, pinned: false, ref stateAt);
        Assert.Equal(NameplateTier.Full, at);
    }

    [Fact]
    public void Resolve_focusGate_visibleState_staysVisibleInsideBand_hidesJustPastIt()
    {
        NameplateTierConfig config = NameplateTierConfig.Default;
        var state = new NameplateTierState();

        // Establish a visible (Full) state, dead centre, close distance.
        NameplateTiers.Resolve(Center, onScreen: true, distance: 5f, ViewportWidth, ViewportHeight, config, pinned: false, ref state);
        Assert.Equal(NameplateTier.Full, state.Tier);

        // r = 0.7, inside the derived 0.6..0.75 exit band: stays visible.
        NameplateTier stillVisible = NameplateTiers.Resolve(
            FocusAtRadius(0.7f), onScreen: true, distance: 5f, ViewportWidth, ViewportHeight, config, pinned: false, ref state);
        Assert.Equal(NameplateTier.Full, stillVisible);

        // r = 0.76, just past the band: hides.
        NameplateTier hidden = NameplateTiers.Resolve(
            FocusAtRadius(0.76f), onScreen: true, distance: 5f, ViewportWidth, ViewportHeight, config, pinned: false, ref state);
        Assert.Equal(NameplateTier.Hidden, hidden);
    }

    [Fact]
    public void Resolve_focusGate_jitterSweep_entersOnceAndNeverFlickersAfter()
    {
        // A persistent state, with the focus radius oscillating between 0.58 and 0.72 (straddling the 0.6 raw
        // edge, never reaching the derived 0.75 exit band), must enter exactly once and never hide again.
        NameplateTierConfig config = NameplateTierConfig.Default;
        var state = new NameplateTierState();
        int transitions = 0;
        bool prevHidden = state.Tier == NameplateTier.Hidden;

        for (int i = 0; i < 40; i++)
        {
            float r = (i % 2 == 0) ? 0.58f : 0.72f;
            NameplateTiers.Resolve(FocusAtRadius(r), onScreen: true, distance: 5f, ViewportWidth, ViewportHeight, config, pinned: false, ref state);
            bool nowHidden = state.Tier == NameplateTier.Hidden;
            if (nowHidden != prevHidden) transitions++;
            prevHidden = nowHidden;
        }

        Assert.Equal(1, transitions);
        Assert.NotEqual(NameplateTier.Hidden, state.Tier);
    }

    [Fact]
    public void Resolve_distanceLadder_fullEdge_entersAt14_staysInsideBand_dropsPast16_5_reentersAt15()
    {
        // FullDistance 15, derived band 15 * 0.1 = 1.5: the exit threshold is 16.5.
        NameplateTierConfig config = NameplateTierConfig.Default;
        var state = new NameplateTierState();

        NameplateTier at14 = NameplateTiers.Resolve(Center, onScreen: true, distance: 14f, ViewportWidth, ViewportHeight, config, pinned: false, ref state);
        Assert.Equal(NameplateTier.Full, at14);

        foreach (float d in new[] { 15.5f, 16.0f, 16.4f })
        {
            NameplateTier tier = NameplateTiers.Resolve(Center, onScreen: true, distance: d, ViewportWidth, ViewportHeight, config, pinned: false, ref state);
            Assert.Equal(NameplateTier.Full, tier);
        }

        NameplateTier at166 = NameplateTiers.Resolve(Center, onScreen: true, distance: 16.6f, ViewportWidth, ViewportHeight, config, pinned: false, ref state);
        Assert.Equal(NameplateTier.Text, at166);

        // Back inside the raw Full distance but not yet re-entering: the Text/Hidden re-entry rule for the
        // Full/Text edge is the raw FullDistance itself (15), not a band.
        NameplateTier stillTextAt155 = NameplateTiers.Resolve(Center, onScreen: true, distance: 15.5f, ViewportWidth, ViewportHeight, config, pinned: false, ref state);
        Assert.Equal(NameplateTier.Text, stillTextAt155);

        NameplateTier reenterAt15 = NameplateTiers.Resolve(Center, onScreen: true, distance: 15f, ViewportWidth, ViewportHeight, config, pinned: false, ref state);
        Assert.Equal(NameplateTier.Full, reenterAt15);
    }

    [Fact]
    public void Resolve_distanceLadder_jitterSweep_aroundFullEdge_noTransitionsAfterInitialResolve()
    {
        // Warm up to Full at a comfortably close distance (not part of the counted sweep), then jitter the
        // distance between 15.2 and 16.4: all above the raw FullDistance edge (15) but all below the derived
        // exit threshold (16.5), so a persistent state must never drop out of Full.
        NameplateTierConfig config = NameplateTierConfig.Default;
        var state = new NameplateTierState();

        NameplateTiers.Resolve(Center, onScreen: true, distance: 14f, ViewportWidth, ViewportHeight, config, pinned: false, ref state);
        Assert.Equal(NameplateTier.Full, state.Tier);

        float[] jitter = { 15.2f, 16.4f, 15.6f, 16.0f, 15.3f, 16.4f, 15.9f, 16.1f, 15.2f, 16.3f };
        int transitions = 0;
        NameplateTier prev = state.Tier;
        foreach (float d in jitter)
        {
            NameplateTier tier = NameplateTiers.Resolve(Center, onScreen: true, distance: d, ViewportWidth, ViewportHeight, config, pinned: false, ref state);
            if (tier != prev) transitions++;
            prev = tier;
        }

        Assert.Equal(0, transitions);
        Assert.Equal(NameplateTier.Full, state.Tier);
    }

    [Fact]
    public void Resolve_textDistance50_entersAt49_staysAt51_hidesAt52_reentersOnlyAt50()
    {
        // TextDistance 50, derived band still FullDistance * 0.1 = 1.5, so the Text exit threshold is 51.5.
        NameplateTierConfig config = NameplateTierConfig.Default with { TextDistance = 50f };
        var state = new NameplateTierState();

        NameplateTier at49 = NameplateTiers.Resolve(Center, onScreen: true, distance: 49f, ViewportWidth, ViewportHeight, config, pinned: false, ref state);
        Assert.Equal(NameplateTier.Text, at49);

        NameplateTier at51 = NameplateTiers.Resolve(Center, onScreen: true, distance: 51f, ViewportWidth, ViewportHeight, config, pinned: false, ref state);
        Assert.Equal(NameplateTier.Text, at51);

        NameplateTier at52 = NameplateTiers.Resolve(Center, onScreen: true, distance: 52f, ViewportWidth, ViewportHeight, config, pinned: false, ref state);
        Assert.Equal(NameplateTier.Hidden, at52);

        // Past TextDistance but still Hidden: does not yet re-enter.
        NameplateTier at505 = NameplateTiers.Resolve(Center, onScreen: true, distance: 50.5f, ViewportWidth, ViewportHeight, config, pinned: false, ref state);
        Assert.Equal(NameplateTier.Hidden, at505);

        NameplateTier at50 = NameplateTiers.Resolve(Center, onScreen: true, distance: 50f, ViewportWidth, ViewportHeight, config, pinned: false, ref state);
        Assert.Equal(NameplateTier.Text, at50);
    }

    [Fact]
    public void Resolve_textDistanceZero_isUnbounded_farDistanceStillResolvesText()
    {
        NameplateTierConfig config = NameplateTierConfig.Default; // TextDistance = 0

        var state = new NameplateTierState();
        NameplateTier tier = NameplateTiers.Resolve(
            Center, onScreen: true, distance: 500f, ViewportWidth, ViewportHeight, config, pinned: false, ref state);

        Assert.Equal(NameplateTier.Text, tier);
    }

    [Fact]
    public void Resolve_focusRadiusDisabled_ignoresGaze_resolvesByDistanceOnly()
    {
        NameplateTierConfig config = NameplateTierConfig.Default with { FocusRadius = 0f };
        var state = new NameplateTierState();

        NameplateTier tier = NameplateTiers.Resolve(
            new Vector2(10f, 10f), onScreen: true, distance: 5f, ViewportWidth, ViewportHeight, config, pinned: false, ref state);

        Assert.Equal(NameplateTier.Full, tier);
    }

    [Fact]
    public void TextOnly_isPanelLessNameOnlyLook_withBlackTitleShadow_andNoEdgeBehavior()
    {
        NameplateStyle style = NameplateStyle.TextOnly;

        Assert.Equal(0f, style.PanelFill.A, 3);
        Assert.Equal(0f, style.PanelBorderThickness, 3);
        Assert.Equal(0f, style.MinBarWidth, 3);
        Assert.Equal(Color.Black, style.TitleShadow);
        Assert.Equal(NameplateEdgeBehavior.None, style.EdgeBehavior);
    }
}
