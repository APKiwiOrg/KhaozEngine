using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests;

// Headless coverage for issue #294: HUD nameplates draw into the DESIGN-space SpriteBatch pass (a fixed design
// size mapped onto the window via IDesignViewport, with letterbox bars under ScaleMode.Fit), but
// IIsoCamera3D.WorldToScreen / NameplateAnchorProjection.Project / NameplateTiers.Resolve took raw framebuffer
// pixel dims, and the camera's projection matrix uses the REAL framebuffer aspect. Calling the int overload with
// the design dims only lines up with the 3D scene when the window happens to be exactly the design aspect. On any
// other window shape the anchor drifts by ndc * letterboxOffset on the loose axis. The fix remaps NDC onto
// IDesignViewport.WindowBounds (the whole window expressed in design space) instead of onto (0,0,Width,Height),
// which is exact for Fit, Fill, and Stretch (Stretch has zero offsets, so WindowBounds == DesignBounds and the new
// path degenerates to the old one).
public sealed class NameplateDesignProjectionTests
{
    const int DesignWidth = 1280;
    const int DesignHeight = 720;

    // Off-centre orbit (non-zero yaw) and a laterally-offset entity, so the entity is well away from the camera's
    // central vertical plane and from the window centre - the case the old int-with-design-dims path gets wrong.
    // Typed as IIsoCamera3D (not the concrete FollowCamera3D) so the design-aware WorldToScreen overload - a
    // default interface member - resolves: a default interface member is only visible through an interface-typed
    // reference, never through the implementing concrete type directly.
    static IIsoCamera3D MakeCamera(float aspect) => new FollowCamera3D
    {
        Target = Vector3.Zero,
        Yaw = 0.6f,
        Pitch = MathF.PI / 6f,
        Distance = 9f,
        AspectRatio = aspect,
    };

    static readonly Vector3 OffCentreEntity = new Vector3(5f, 0f, 3f);

    [Fact]
    public void WorldToScreen_wideWindow_designAwareRoundTripsToSameWindowPixelAsIntOverload()
    {
        // THE regression: camera driven from a wide window's real aspect, design viewport updated to that same
        // window size. The design-aware pixel, mapped back onto the window via DesignToScreen, must land exactly
        // where the int overload (called with the REAL window dims) puts the entity - the round-trip law.
        var designViewport = new DesignViewport(DesignWidth, DesignHeight, ScaleMode.Fit);
        designViewport.Update(2560, 1080);
        IIsoCamera3D camera = MakeCamera(2560f / 1080f);

        Assert.True(camera.WorldToScreen(OffCentreEntity, designViewport, out Vector2 designPixel));
        Assert.True(camera.WorldToScreen(OffCentreEntity, 2560, 1080, out Vector2 windowPixel));

        Vector2 roundTripped = designViewport.DesignToScreen(designPixel);
        Assert.Equal(windowPixel.X, roundTripped.X, 2);
        Assert.Equal(windowPixel.Y, roundTripped.Y, 2);
    }

    [Fact]
    public void WorldToScreen_wideWindow_oldIntWithDesignDimsPathDriftsFromCorrectPixel()
    {
        // Premise guard: the OLD path (int overload called with the design dims, ignoring the letterbox) is wrong
        // on a non-design-aspect window. Documents the bug this fix corrects.
        var designViewport = new DesignViewport(DesignWidth, DesignHeight, ScaleMode.Fit);
        designViewport.Update(2560, 1080);
        IIsoCamera3D camera = MakeCamera(2560f / 1080f);

        Assert.True(camera.WorldToScreen(OffCentreEntity, designViewport, out Vector2 designPixel));
        Assert.True(camera.WorldToScreen(OffCentreEntity, DesignWidth, DesignHeight, out Vector2 oldPixel));

        float driftX = MathF.Abs(designPixel.X - oldPixel.X);
        Assert.True(driftX > 5f, $"expected the old int-with-design-dims path to drift by more than 5px, drift={driftX}");
    }

    [Fact]
    public void WorldToScreen_16by9Window_designAwareMatchesOldPath_noLetterbox()
    {
        // No letterbox at the design aspect: WindowBounds == DesignBounds, so the two paths agree exactly - the
        // common case must not regress.
        var designViewport = new DesignViewport(DesignWidth, DesignHeight, ScaleMode.Fit);
        designViewport.Update(1600, 900);
        IIsoCamera3D camera = MakeCamera(16f / 9f);

        Assert.True(camera.WorldToScreen(OffCentreEntity, designViewport, out Vector2 designPixel));
        Assert.True(camera.WorldToScreen(OffCentreEntity, DesignWidth, DesignHeight, out Vector2 oldPixel));

        Assert.Equal(oldPixel.X, designPixel.X, 2);
        Assert.Equal(oldPixel.Y, designPixel.Y, 2);
    }

    [Fact]
    public void WorldToScreen_stretchMode_designAwareMatchesOldPath_zeroOffsets()
    {
        // Stretch offsets are always zero, so WindowBounds == DesignBounds regardless of window aspect, and the
        // new path degenerates to the old one even on a non-design-aspect window.
        var designViewport = new DesignViewport(DesignWidth, DesignHeight, ScaleMode.Stretch);
        designViewport.Update(2000, 1000);
        IIsoCamera3D camera = MakeCamera(2000f / 1000f);

        Assert.True(camera.WorldToScreen(OffCentreEntity, designViewport, out Vector2 designPixel));
        Assert.True(camera.WorldToScreen(OffCentreEntity, DesignWidth, DesignHeight, out Vector2 oldPixel));

        Assert.Equal(oldPixel.X, designPixel.X, 2);
        Assert.Equal(oldPixel.Y, designPixel.Y, 2);
    }

    [Fact]
    public void Project_wideWindow_composesBodyColumnXWithHeadY_inDesignSpace()
    {
        var designViewport = new DesignViewport(DesignWidth, DesignHeight, ScaleMode.Fit);
        designViewport.Update(2560, 1080);
        IIsoCamera3D camera = MakeCamera(2560f / 1080f);
        var offset = new Vector3(0f, 2.15f, 0f);

        Assert.True(camera.WorldToScreen(OffCentreEntity, designViewport, out Vector2 bodyPixel));
        Assert.True(camera.WorldToScreen(OffCentreEntity + offset, designViewport, out Vector2 headPixel));

        Assert.True(NameplateAnchorProjection.Project(camera, OffCentreEntity, offset, designViewport, out Vector2 pixel));

        Assert.Equal(bodyPixel.X, pixel.X, 2);
        Assert.Equal(headPixel.Y, pixel.Y, 2);
    }

    [Fact]
    public void Tiers_wideWindow_windowBoundsCentreIsFocused_outsideWindowBoundsRadiusIsHidden()
    {
        var designViewport = new DesignViewport(DesignWidth, DesignHeight, ScaleMode.Fit);
        designViewport.Update(2560, 1080);
        NameplateTierConfig config = NameplateTierConfig.Default;
        Rect window = designViewport.WindowBounds;
        var centre = new Vector2(window.X + window.Width * 0.5f, window.Y + window.Height * 0.5f);
        // Comfortably inside the window's top-left corner: well outside the 0.6 focus radius measured against
        // WindowBounds (the window the player actually sees), not DesignBounds.
        var farCorner = new Vector2(window.X + 1f, window.Y + 1f);

        var centreState = new NameplateTierState();
        NameplateTier centreTier = NameplateTiers.Resolve(
            centre, onScreen: true, distance: 5f, designViewport, config, pinned: false, ref centreState);
        Assert.Equal(NameplateTier.Full, centreTier);

        var cornerState = new NameplateTierState();
        NameplateTier cornerTier = NameplateTiers.Resolve(
            farCorner, onScreen: true, distance: 5f, designViewport, config, pinned: false, ref cornerState);
        Assert.Equal(NameplateTier.Hidden, cornerTier);
    }
}
