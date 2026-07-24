using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests;

// Headless coverage for NameplatePlacement.Place, the pure edge-aware placement math NameplateRenderer.Draw
// delegates to: the unclamped None baseline, the Clamp axis math, and the Deflect side-choice/stickiness/
// hysteresis state machine (NameplatePlacementState.IsDeflected). NameplateRenderer.Draw itself still needs a
// GPU SpriteBatch so it is not unit-tested here, mirroring NameplateTests/WorldLabel.
public sealed class NameplatePlacementTests
{
    const int ViewportWidth = 800;
    const int ViewportHeight = 600;
    const float Margin = 10f;
    static readonly Vector2 PlateSize = new Vector2(100f, 40f);

    static NameplateStyle StyleWith(NameplateEdgeBehavior behavior, float hysteresis = 0f) =>
        NameplateStyle.Default with { EdgeBehavior = behavior, EdgeMargin = Margin, EdgeHysteresis = hysteresis };

    // The plate's rect must sit entirely inside the margin-inset viewport, on both axes.
    static void AssertWithinMargin(Vector4 rect)
    {
        Assert.True(rect.X >= Margin);
        Assert.True(rect.X + rect.Z <= ViewportWidth - Margin);
        Assert.True(rect.Y >= Margin);
        Assert.True(rect.Y + rect.W <= ViewportHeight - Margin);
    }

    [Fact]
    public void Default_edgeBehaviorIsNone_andPlacementStateDefaultsToNotDeflected()
    {
        Assert.Equal(NameplateEdgeBehavior.None, NameplateStyle.Default.EdgeBehavior);
        Assert.Equal(4f, NameplateStyle.Default.EdgeMargin, 3);
        Assert.False(default(NameplatePlacementState).IsDeflected);
    }

    [Fact]
    public void Place_none_returnsUnclampedBaseline_andLeavesStateNotDeflected()
    {
        // Today's placement: centred horizontally, bottom-anchored above the pixel, no clamping even though
        // this anchor overflows the top edge.
        NameplateStyle style = StyleWith(NameplateEdgeBehavior.None);
        var state = new NameplatePlacementState();

        Vector4 rect = NameplatePlacement.Place(new Vector2(400f, 20f), PlateSize, ViewportWidth, ViewportHeight, style, ref state);

        Assert.Equal(350f, rect.X, 3);
        Assert.Equal(-20f, rect.Y, 3);
        Assert.False(state.IsDeflected);
    }

    [Fact]
    public void Place_clamp_topOverflow_clampsTopToMargin()
    {
        NameplateStyle style = StyleWith(NameplateEdgeBehavior.Clamp);
        var state = new NameplatePlacementState();

        Vector4 rect = NameplatePlacement.Place(new Vector2(400f, 20f), PlateSize, ViewportWidth, ViewportHeight, style, ref state);

        Assert.Equal(350f, rect.X, 3);
        Assert.Equal(10f, rect.Y, 3);
        Assert.False(state.IsDeflected);
    }

    [Fact]
    public void Place_clamp_leftOverflow_clampsLeftToMargin()
    {
        NameplateStyle style = StyleWith(NameplateEdgeBehavior.Clamp);
        var state = new NameplatePlacementState();

        Vector4 rect = NameplatePlacement.Place(new Vector2(30f, 300f), PlateSize, ViewportWidth, ViewportHeight, style, ref state);

        Assert.Equal(10f, rect.X, 3);
    }

    [Fact]
    public void Place_clamp_rightOverflow_clampsLeftToViewportMinusMarginMinusWidth()
    {
        NameplateStyle style = StyleWith(NameplateEdgeBehavior.Clamp);
        var state = new NameplatePlacementState();

        Vector4 rect = NameplatePlacement.Place(new Vector2(770f, 300f), PlateSize, ViewportWidth, ViewportHeight, style, ref state);

        Assert.Equal(690f, rect.X, 3); // 800 - 10 - 100
    }

    [Fact]
    public void Place_clamp_bottomOverflow_clampsTopToViewportMinusMarginMinusHeight()
    {
        NameplateStyle style = StyleWith(NameplateEdgeBehavior.Clamp);
        var state = new NameplatePlacementState();

        Vector4 rect = NameplatePlacement.Place(new Vector2(400f, 599f), PlateSize, ViewportWidth, ViewportHeight, style, ref state);

        Assert.Equal(550f, rect.Y, 3); // 600 - 10 - 40
    }

    [Fact]
    public void Place_deflect_noOverflow_returnsBaseline_andNotDeflected()
    {
        NameplateStyle style = StyleWith(NameplateEdgeBehavior.Deflect);
        var state = new NameplatePlacementState();

        Vector4 rect = NameplatePlacement.Place(new Vector2(400f, 300f), PlateSize, ViewportWidth, ViewportHeight, style, ref state);

        Assert.Equal(350f, rect.X, 3);
        Assert.Equal(260f, rect.Y, 3);
        Assert.False(state.IsDeflected);
    }

    [Fact]
    public void Place_deflect_topOverflowAtCentre_entersDeflection_sideRightAndWithinMargin()
    {
        // baselineTop = 30 - 40 = -10, below the margin, so this enters deflection immediately. Dead centre
        // makes roomRight == roomLeft, and the tie-break favours the right.
        NameplateStyle style = StyleWith(NameplateEdgeBehavior.Deflect);
        var state = new NameplatePlacementState();

        Vector4 rect = NameplatePlacement.Place(new Vector2(400f, 30f), PlateSize, ViewportWidth, ViewportHeight, style, ref state);

        Assert.True(state.IsDeflected);
        Assert.Equal(408f, rect.X, 3); // anchor.X + DeflectGap
        Assert.Equal(10f, rect.Y, 3);  // anchor.Y - height*0.5 = 10, exactly at the margin after clamping
        AssertWithinMargin(rect);
    }

    [Fact]
    public void Place_deflect_topLeftCorner_deflectsRight_andStaysWithinViewport()
    {
        // Near the left edge there is far more room to the right than to the left, so it deflects right.
        NameplateStyle style = StyleWith(NameplateEdgeBehavior.Deflect);
        var state = new NameplatePlacementState();

        Vector4 rect = NameplatePlacement.Place(new Vector2(30f, 20f), PlateSize, ViewportWidth, ViewportHeight, style, ref state);

        Assert.True(state.IsDeflected);
        Assert.Equal(38f, rect.X, 3); // anchor.X + DeflectGap
        AssertWithinMargin(rect);
    }

    [Fact]
    public void Place_deflect_topRightCorner_deflectsLeft_andStaysWithinViewport()
    {
        // Near the right edge there is far more room to the left than to the right, so it deflects left.
        NameplateStyle style = StyleWith(NameplateEdgeBehavior.Deflect);
        var state = new NameplatePlacementState();

        Vector4 rect = NameplatePlacement.Place(new Vector2(770f, 20f), PlateSize, ViewportWidth, ViewportHeight, style, ref state);

        Assert.True(state.IsDeflected);
        Assert.Equal(662f, rect.X, 3); // anchor.X - DeflectGap - width
        AssertWithinMargin(rect);
    }

    [Fact]
    public void Place_deflect_hysteresis_defaultBand_exitsAtDerivedThreshold()
    {
        // EdgeHysteresis <= 0 derives the exit band as half the plate height: 40 * 0.5 = 20, so the exit
        // threshold is margin(10) + band(20) = 30.
        NameplateStyle style = StyleWith(NameplateEdgeBehavior.Deflect);
        var state = new NameplatePlacementState();

        NameplatePlacement.Place(new Vector2(400f, 30f), PlateSize, ViewportWidth, ViewportHeight, style, ref state);
        Assert.True(state.IsDeflected);

        // baselineTop = 69 - 40 = 29, still short of the 30 threshold: stays deflected.
        NameplatePlacement.Place(new Vector2(400f, 69f), PlateSize, ViewportWidth, ViewportHeight, style, ref state);
        Assert.True(state.IsDeflected);

        // baselineTop = 70 - 40 = 30, clears the threshold: un-deflects back to the normal clamped placement.
        Vector4 rect = NameplatePlacement.Place(new Vector2(400f, 70f), PlateSize, ViewportWidth, ViewportHeight, style, ref state);
        Assert.False(state.IsDeflected);
        Assert.Equal(30f, rect.Y, 3);
    }

    [Fact]
    public void Place_deflect_hysteresis_explicitBand_exitsAtExplicitThreshold()
    {
        // EdgeHysteresis = 5 sets the exit threshold to margin(10) + band(5) = 15.
        NameplateStyle style = StyleWith(NameplateEdgeBehavior.Deflect, hysteresis: 5f);
        var state = new NameplatePlacementState();

        NameplatePlacement.Place(new Vector2(400f, 30f), PlateSize, ViewportWidth, ViewportHeight, style, ref state);
        Assert.True(state.IsDeflected);

        // baselineTop = 54 - 40 = 14, short of 15: stays deflected.
        NameplatePlacement.Place(new Vector2(400f, 54f), PlateSize, ViewportWidth, ViewportHeight, style, ref state);
        Assert.True(state.IsDeflected);

        // baselineTop = 55 - 40 = 15, clears it: un-deflects.
        NameplatePlacement.Place(new Vector2(400f, 55f), PlateSize, ViewportWidth, ViewportHeight, style, ref state);
        Assert.False(state.IsDeflected);
    }

    [Fact]
    public void Place_deflect_hysteresis_noOscillation_acrossSweep()
    {
        // A single persistent state must not flicker in and out of deflection as the anchor drifts back and
        // forth inside the hysteresis band: exactly one enter transition on the way down through the margin,
        // zero transitions while the retreat stays short of the exit threshold, and exactly one exit
        // transition once it clears that threshold.
        NameplateStyle style = StyleWith(NameplateEdgeBehavior.Deflect);
        var state = new NameplatePlacementState();
        int transitions = 0;
        bool prev = state.IsDeflected;

        void Step(float anchorY)
        {
            NameplatePlacement.Place(new Vector2(400f, anchorY), PlateSize, ViewportWidth, ViewportHeight, style, ref state);
            if (state.IsDeflected != prev) transitions++;
            prev = state.IsDeflected;
        }

        for (float y = 60f; y >= 30f; y -= 1f) Step(y);
        Assert.Equal(1, transitions); // enters once, at y = 49 (baselineTop = 9 < margin 10)

        int afterDown = transitions;
        for (float y = 31f; y <= 60f; y += 1f) Step(y);
        Assert.Equal(afterDown, transitions); // no exit: baselineTop stays below the 30 threshold through y = 60

        for (float y = 61f; y <= 75f; y += 1f) Step(y);
        Assert.Equal(afterDown + 1, transitions); // exits once, at y = 70 (baselineTop = 30 >= 30)

        Assert.Equal(2, transitions);
    }

    [Fact]
    public void Place_deflect_stickySide_doesNotFlip_whenRoomShiftsButStillFits()
    {
        // Enter deflection left of centre (anchor.X = 390), where the room split favours the right
        // (roomRight 392 >= roomLeft 372): deflects right, left edge = anchor.X + DeflectGap.
        NameplateStyle style = StyleWith(NameplateEdgeBehavior.Deflect);
        var state = new NameplatePlacementState();

        Vector4 first = NameplatePlacement.Place(new Vector2(390f, 30f), PlateSize, ViewportWidth, ViewportHeight, style, ref state);
        Assert.True(state.IsDeflected);
        Assert.Equal(398f, first.X, 3);

        // Move the anchor to 410 (right of centre): the split now nominally favours the left (roomLeft 392 >
        // roomRight 372), but the current (right) side's room is still 372, comfortably >= the 100-wide
        // plate, so stickiness keeps it on the right instead of flipping every frame the anchor drifts.
        Vector4 second = NameplatePlacement.Place(new Vector2(410f, 30f), PlateSize, ViewportWidth, ViewportHeight, style, ref state);
        Assert.True(state.IsDeflected);
        Assert.Equal(418f, second.X, 3); // 410 + DeflectGap, side unchanged
    }

    [Fact]
    public void Place_deflect_sideSwitches_whenCurrentSideNoLongerFits()
    {
        // Same entry as the stickiness test: deflects right at anchor.X = 390.
        NameplateStyle style = StyleWith(NameplateEdgeBehavior.Deflect);
        var state = new NameplatePlacementState();
        NameplatePlacement.Place(new Vector2(390f, 30f), PlateSize, ViewportWidth, ViewportHeight, style, ref state);
        Assert.True(state.IsDeflected);

        // Move the anchor to 750: roomRight = (790) - (750 + 8) = 32, below the 100-wide plate, while
        // roomLeft = (750 - 8) - 10 = 732 comfortably fits it. The current side no longer fits, so it switches.
        Vector4 rect = NameplatePlacement.Place(new Vector2(750f, 30f), PlateSize, ViewportWidth, ViewportHeight, style, ref state);
        Assert.True(state.IsDeflected);
        Assert.Equal(642f, rect.X, 3); // 750 - DeflectGap - width, now on the left
    }
}
