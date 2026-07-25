using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests;

// Headless coverage for NameplateAnchorProjection.Project, the perspective-lean fix for nameplate/world-label
// anchor placement, driven through a real FollowCamera3D (pure math, no GPU). A world-vertical offset
// (worldPos + offset) only projects screen-vertically when the entity sits on the camera's central vertical
// plane. Off that plane, a perspective camera leans the projected point horizontally (zero lean at screen
// centre, the sign flipping as the entity crosses that plane), so a single head-point projection puts the
// plate beside the entity instead of above it, and swaps side as the camera orbits. Project composes the
// anchor from two projections instead: the body column (worldPos plus only the lateral part of offset)
// drives screen X, the head point (worldPos + the full offset) drives screen Y.
public sealed class NameplateAnchorProjectionTests
{
    const int ViewportWidth = 1280;
    const int ViewportHeight = 720;

    static FollowCamera3D MakeCamera() => new FollowCamera3D
    {
        Target = Vector3.Zero,
        Yaw = 0f,
        Pitch = MathF.PI / 6f,   // 30 deg, an ordinary look-down pitch
        Distance = 9f,
    };

    [Fact]
    public void Project_offCentreEntity_rawHeadProjectionLeansHorizontally()
    {
        // Guards the premise this fix exists for: projecting worldPos + offset alone, for an entity off the
        // camera's central vertical plane, leans horizontally away from the entity's own body column.
        FollowCamera3D camera = MakeCamera();
        var feet = new Vector3(3f, 0f, 0f);
        var offset = new Vector3(0f, 2.15f, 0f);

        Assert.True(camera.WorldToScreen(feet, ViewportWidth, ViewportHeight, out Vector2 feetPixel));
        Assert.True(camera.WorldToScreen(feet + offset, ViewportWidth, ViewportHeight, out Vector2 headPixel));

        Assert.True(MathF.Abs(headPixel.X - feetPixel.X) > 1f,
            $"expected a measurable lean, feet.X={feetPixel.X} head.X={headPixel.X}");
    }

    [Fact]
    public void Project_offCentreEntity_composesBodyColumnXWithHeadY()
    {
        FollowCamera3D camera = MakeCamera();
        var feet = new Vector3(3f, 0f, 0f);
        var offset = new Vector3(0f, 2.15f, 0f);

        Assert.True(camera.WorldToScreen(feet, ViewportWidth, ViewportHeight, out Vector2 bodyPixel));
        Assert.True(camera.WorldToScreen(feet + offset, ViewportWidth, ViewportHeight, out Vector2 headPixel));

        Assert.True(NameplateAnchorProjection.Project(camera, feet, offset, ViewportWidth, ViewportHeight, out Vector2 pixel));

        Assert.Equal(bodyPixel.X, pixel.X, 2);
        Assert.Equal(headPixel.Y, pixel.Y, 2);
    }

    [Fact]
    public void Project_centredEntity_matchesRawHeadProjectionOnBothAxes()
    {
        // An entity ON the camera's central vertical plane (X = 0 here, Yaw = 0) has no lean to correct: the
        // body-column and head-point projections already share the same X, so composing them changes nothing.
        FollowCamera3D camera = MakeCamera();
        var feet = new Vector3(0f, 0f, 0f);
        var offset = new Vector3(0f, 2.15f, 0f);

        Assert.True(camera.WorldToScreen(feet + offset, ViewportWidth, ViewportHeight, out Vector2 headPixel));
        Assert.True(NameplateAnchorProjection.Project(camera, feet, offset, ViewportWidth, ViewportHeight, out Vector2 pixel));

        Assert.Equal(headPixel.X, pixel.X, 2);
        Assert.Equal(headPixel.Y, pixel.Y, 2);
    }

    [Fact]
    public void Project_lateralOffsetComponent_staysHonouredInX()
    {
        // A caller-supplied lateral world offset (offset.X/.Z) is a deliberate world-space nudge, not the
        // vertical float height, so it must still move the anchor rather than being dropped.
        FollowCamera3D camera = MakeCamera();
        var feet = new Vector3(3f, 0f, 0f);
        var offset = new Vector3(0.5f, 2.15f, 0f);

        Assert.True(camera.WorldToScreen(feet + new Vector3(offset.X, 0f, offset.Z), ViewportWidth, ViewportHeight,
            out Vector2 lateralBodyPixel));
        Assert.True(NameplateAnchorProjection.Project(camera, feet, offset, ViewportWidth, ViewportHeight, out Vector2 pixel));

        Assert.Equal(lateralBodyPixel.X, pixel.X, 2);
    }

    [Fact]
    public void Project_entityBehindCamera_returnsFalse()
    {
        FollowCamera3D camera = MakeCamera();
        Vector3 behind = camera.Eye - camera.Forward * 50f;

        Assert.False(NameplateAnchorProjection.Project(camera, behind, new Vector3(0f, 2.15f, 0f),
            ViewportWidth, ViewportHeight, out Vector2 pixel));
        Assert.Equal(default, pixel);
    }

    [Fact]
    public void Project_mirroredEntity_leanSignFlipsButComposedXStillTracksBody()
    {
        FollowCamera3D camera = MakeCamera();
        var offset = new Vector3(0f, 2.15f, 0f);
        var feetRight = new Vector3(3f, 0f, 0f);
        var feetLeft = new Vector3(-3f, 0f, 0f);

        Assert.True(camera.WorldToScreen(feetRight, ViewportWidth, ViewportHeight, out Vector2 bodyRight));
        Assert.True(camera.WorldToScreen(feetRight + offset, ViewportWidth, ViewportHeight, out Vector2 headRight));
        Assert.True(camera.WorldToScreen(feetLeft, ViewportWidth, ViewportHeight, out Vector2 bodyLeft));
        Assert.True(camera.WorldToScreen(feetLeft + offset, ViewportWidth, ViewportHeight, out Vector2 headLeft));

        float diffRight = bodyRight.X - headRight.X;
        float diffLeft = bodyLeft.X - headLeft.X;
        Assert.True(MathF.Abs(diffRight) > 1f && MathF.Abs(diffLeft) > 1f,
            $"expected a measurable lean on both sides, diffRight={diffRight} diffLeft={diffLeft}");
        Assert.True(diffRight * diffLeft < 0f,
            $"expected the lean to flip sign across the centre plane, diffRight={diffRight} diffLeft={diffLeft}");

        Assert.True(NameplateAnchorProjection.Project(camera, feetRight, offset, ViewportWidth, ViewportHeight, out Vector2 pixelRight));
        Assert.True(NameplateAnchorProjection.Project(camera, feetLeft, offset, ViewportWidth, ViewportHeight, out Vector2 pixelLeft));

        Assert.Equal(bodyRight.X, pixelRight.X, 2);
        Assert.Equal(bodyLeft.X, pixelLeft.X, 2);
    }
}
