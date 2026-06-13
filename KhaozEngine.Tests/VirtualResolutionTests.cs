using KhaozEngine.Input;
using Microsoft.Xna.Framework;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Headless coverage for <see cref="VirtualResolution"/>'s scaling math. The constructor accepts a
/// null <c>GraphicsDeviceManager</c> (only <see cref="VirtualResolution.Initialize"/> reads the
/// back-buffer); tests drive the pure <see cref="VirtualResolution.Configure"/> with explicit sizes.
/// </summary>
public class VirtualResolutionTests
{
    private const float Tol = 1e-3f;

    [Fact]
    public void DesignScaledDesktop_ScalesBaselineToFillWindow()
    {
        // A 932x430 design space presented into a 2x (Retina) 1864x860 window scales by 2.
        var vr = VirtualResolution.DesignScaled(null, baseWidth: 932, referenceHeight: 430);
        vr.Configure(1864, 860);

        Assert.Equal(2f, vr.Scale, Tol);
        Assert.Equal(932, vr.Width);                 // width locked to the design baseline
        Assert.Equal(430, vr.Height);                // height happens to match at a 2x square scale
        Assert.Equal(Matrix.CreateScale(2f, 2f, 1f), vr.ScaleMatrix);
    }

    [Fact]
    public void DesignScaled_ScreenToVirtualRoundTripsThroughScaleMatrix()
    {
        var vr = VirtualResolution.DesignScaled(null, baseWidth: 932, referenceHeight: 430);
        vr.Configure(1864, 860);   // scale 2

        // ScreenToVirtual divides by scale; ScaleMatrix multiplies back to screen pixels.
        var screen = new Vector2(1200, 600);
        var virt = vr.ScreenToVirtual(screen);
        Assert.Equal(new Vector2(600, 300), virt);
        Assert.Equal(screen, Vector2.Transform(virt, vr.ScaleMatrix));
    }

    [Fact]
    public void DesignScaled_HeightAdaptsOnWideWindow()
    {
        // Wide window: scale is locked by width, so the visible design height shrinks below the
        // reference (fill-the-width, adaptive-height; no letterbox bars, no offset).
        var vr = VirtualResolution.DesignScaled(null, baseWidth: 932, referenceHeight: 430);
        vr.Configure(2796, 430);   // 3x width

        Assert.Equal(3f, vr.Scale, Tol);
        Assert.Equal(932, vr.Width);
        Assert.True(vr.Height < vr.ReferenceHeight);   // 430/3 -> 143
        Assert.Equal(143, vr.Height);
    }

    [Fact]
    public void DefaultDesktop_StaysIdentityAndOneToOne()
    {
        // Regression: the existing desktop default (isMobile:false) is unchanged.
        var vr = new VirtualResolution(null, isMobile: false, baseWidth: 932, referenceHeight: 430);
        vr.Configure(1864, 860);

        Assert.Equal(1f, vr.Scale, Tol);
        Assert.Equal(1864, vr.Width);
        Assert.Equal(860, vr.Height);
        Assert.Equal(Matrix.Identity, vr.ScaleMatrix);
        Assert.Equal(new Rectangle(0, 0, 1864, 860), vr.VirtualBounds);
        Assert.Equal(new Vector2(20, 40), vr.ScreenToVirtual(new Vector2(20, 40)));   // 1:1
    }

    [Fact]
    public void Mobile_BehaviorUnchanged()
    {
        // Regression: the mobile path (the original design-scale) is byte-identical to before.
        var vr = new VirtualResolution(null, isMobile: true, baseWidth: 440, referenceHeight: 956);
        vr.Configure(880, 1900);

        Assert.Equal(2f, vr.Scale, Tol);
        Assert.Equal(440, vr.Width);
        Assert.Equal(950, vr.Height);
        Assert.Equal(Matrix.CreateScale(2f, 2f, 1f), vr.ScaleMatrix);
    }

    [Fact]
    public void ImplementsDesignViewport_ExposingLiveSizeScaleAndMatrix()
    {
        // VirtualResolution is usable through the IDesignViewport seam; the seam reports the live
        // values (it reads the same backing members, so reconfiguring is visible through the interface).
        var vr = VirtualResolution.DesignScaled(null, baseWidth: 932, referenceHeight: 430);
        vr.Configure(1864, 860);

        IDesignViewport viewport = vr;
        Assert.Equal(vr.Width, viewport.Width);
        Assert.Equal(vr.Height, viewport.Height);
        Assert.Equal(vr.Scale, viewport.Scale, Tol);
        Assert.Equal(vr.ScaleMatrix, viewport.ScaleMatrix);

        vr.Configure(2796, 430);   // 3x width; seam tracks the change
        Assert.Equal(3f, viewport.Scale, Tol);
        Assert.Equal(932, viewport.Width);
        Assert.Equal(143, viewport.Height);
        Assert.Equal(Matrix.CreateScale(3f, 3f, 1f), viewport.ScaleMatrix);
    }

    [Fact]
    public void DesignViewport_IsFakeableWithoutVirtualResolution()
    {
        // The whole point of the seam: a headless screen can run against a fixed fake with no
        // GraphicsDeviceManager and no VirtualResolution at all.
        IDesignViewport fake = new FakeViewport(320, 180, 2f, Matrix.CreateScale(2f, 2f, 1f));

        Assert.Equal(320, fake.Width);
        Assert.Equal(180, fake.Height);
        Assert.Equal(2f, fake.Scale, Tol);
        Assert.Equal(Matrix.CreateScale(2f, 2f, 1f), fake.ScaleMatrix);
    }

    private sealed class FakeViewport(int width, int height, float scale, Matrix scaleMatrix) : IDesignViewport
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public float Scale { get; } = scale;
        public Matrix ScaleMatrix { get; } = scaleMatrix;
    }
}
