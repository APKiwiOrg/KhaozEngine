using System.Numerics;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>Headless coverage for parallax scroll math (ParallaxLayer + Parallax.Wrap).</summary>
public class Render2DParallaxTests
{
    private const float Tol = 1e-4f;

    private static void AssertClose(Vector2 expected, Vector2 actual) =>
        Assert.True(Vector2.Distance(expected, actual) <= Tol, $"expected {expected}, got {actual}");

    // ---- ParallaxLayer ----

    [Fact]
    public void Layer_ViewPositionScalesByFactorPerAxis()
    {
        var layer = new ParallaxLayer(new Vector2(0.5f, 1f));
        AssertClose(new Vector2(100f, 50f), layer.ViewPosition(new Vector2(200f, 50f)));
    }

    [Fact]
    public void Layer_ZeroFactorIsStaticBackdrop()
    {
        var layer = new ParallaxLayer(Vector2.Zero);
        AssertClose(Vector2.Zero, layer.ViewPosition(new Vector2(200f, 50f)));
    }

    [Fact]
    public void Layer_UnitFactorLocksToCamera()
    {
        var layer = new ParallaxLayer(new Vector2(1f, 1f));
        var cam = new Vector2(123f, -45f);
        AssertClose(cam, layer.ViewPosition(cam));
    }

    [Fact]
    public void Layer_UniformCtorSetsBothAxes()
    {
        var layer = new ParallaxLayer(0.25f);
        Assert.Equal(0.25f, layer.Factor.X, Tol);
        Assert.Equal(0.25f, layer.Factor.Y, Tol);
        AssertClose(new Vector2(25f, 50f), layer.ViewPosition(new Vector2(100f, 200f)));
    }

    // ---- Parallax.Wrap ----

    [Fact]
    public void Wrap_ReturnsRemainderInRange()
    {
        Assert.Equal(50f, Parallax.Wrap(250f, 100f), Tol);
        Assert.Equal(0f, Parallax.Wrap(100f, 100f), Tol);
        Assert.Equal(0f, Parallax.Wrap(0f, 100f), Tol);
    }

    [Fact]
    public void Wrap_NegativeValueGivesPositiveRemainder()
    {
        Assert.Equal(70f, Parallax.Wrap(-30f, 100f), Tol);
    }

    [Fact]
    public void Wrap_NonPositiveSizeReturnsZero()
    {
        Assert.Equal(0f, Parallax.Wrap(5f, 0f), Tol);
        Assert.Equal(0f, Parallax.Wrap(5f, -2f), Tol);
    }
}
