using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests;

// Headless coverage for WorldLabel.ShouldCull, the distance predicate factored out of Draw (Draw itself needs a
// GPU SpriteBatch). The cull anchor (cullFrom) is the gameplay-meaningful viewer-player position for third-person
// games, distinct from the camera eye.
public sealed class WorldLabelTests
{
    [Fact]
    public void ShouldCull_disabledWhenMaxDistanceZeroOrLess()
    {
        var far = new Vector3(1000f, 0f, 0f);
        Assert.False(WorldLabel.ShouldCull(far, Vector3.Zero, 0f));
        Assert.False(WorldLabel.ShouldCull(far, Vector3.Zero, -5f));
    }

    [Fact]
    public void ShouldCull_insideRingDraws_outsideRingCulls()
    {
        var anchor = Vector3.Zero;
        // 90m ring: a target 80m out survives, 100m out is culled.
        Assert.False(WorldLabel.ShouldCull(new Vector3(80f, 0f, 0f), anchor, 90f));
        Assert.True(WorldLabel.ShouldCull(new Vector3(100f, 0f, 0f), anchor, 90f));
    }

    [Fact]
    public void ShouldCull_measuresFromCullFromNotCamera()
    {
        // The target sits exactly on the ring around the player but far from a camera offset behind them: culling
        // keys off the supplied anchor (cullFrom), so moving the anchor flips the result for the SAME target.
        var target = new Vector3(50f, 0f, 0f);
        var player = new Vector3(0f, 0f, 0f);
        var cameraBehindPlayer = new Vector3(-30f, 0f, 0f); // orbit eye 30m back

        Assert.False(WorldLabel.ShouldCull(target, player, 60f));            // 50m from player: in range
        Assert.True(WorldLabel.ShouldCull(target, cameraBehindPlayer, 60f)); // 80m from camera eye: out of range
    }

    [Fact]
    public void ShouldCull_boundaryIsExclusive_exactlyAtRingDraws()
    {
        // Distance == maxDistance is NOT culled (strict >), so a target exactly on the ring still draws.
        Assert.False(WorldLabel.ShouldCull(new Vector3(90f, 0f, 0f), Vector3.Zero, 90f));
    }
}
