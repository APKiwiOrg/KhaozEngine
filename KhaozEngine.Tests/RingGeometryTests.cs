using KhaozEngine.Graphics;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Headless tests for <see cref="PrimitiveRenderer.RingSegments"/>, the radius-adaptive segment
/// count behind <see cref="PrimitiveRenderer.DrawRing(Microsoft.Xna.Framework.Graphics.SpriteBatch,
/// Microsoft.Xna.Framework.Graphics.Texture2D, Microsoft.Xna.Framework.Vector2, float, float,
/// Microsoft.Xna.Framework.Color, int?)"/>.
/// </summary>
public class RingGeometryTests
{
    [Theory]
    [InlineData(0f, 18)]      // degenerate radius still clamps up to the floor
    [InlineData(10f, 18)]     // (int)(3.5) = 3, clamped to 18
    [InlineData(100f, 35)]    // (int)(35.0) = 35, inside the band
    [InlineData(300f, 64)]    // (int)(105) = 105, clamped to 64
    [InlineData(5000f, 64)]   // far past the ceiling
    public void AdaptiveSegments_ClampToBand(float radius, int expected)
    {
        Assert.Equal(expected, PrimitiveRenderer.RingSegments(radius, segmentsOverride: null));
    }

    [Theory]
    [InlineData(48, 48)]   // explicit override is honored as-is
    [InlineData(3, 3)]     // exactly the floor
    [InlineData(2, 3)]     // below the floor is raised to 3
    [InlineData(0, 3)]
    public void Override_FlooredAtThree(int requested, int expected)
    {
        // Override wins regardless of radius (use a large radius to prove it is ignored).
        Assert.Equal(expected, PrimitiveRenderer.RingSegments(radius: 999f, segmentsOverride: requested));
    }
}
