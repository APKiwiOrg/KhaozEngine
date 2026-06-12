using KhaozEngine.Sprites;
using Microsoft.Xna.Framework;
using Xunit;

namespace KhaozEngine.Tests;

public class Direction8Tests
{
    // Screen space: +X is east (right), +Y is south (down), matching MonoGame.
    [Theory]
    [InlineData(1f, 0f, Direction8.E)]
    [InlineData(1f, 1f, Direction8.SE)]
    [InlineData(0f, 1f, Direction8.S)]
    [InlineData(-1f, 1f, Direction8.SW)]
    [InlineData(-1f, 0f, Direction8.W)]
    [InlineData(-1f, -1f, Direction8.NW)]
    [InlineData(0f, -1f, Direction8.N)]
    [InlineData(1f, -1f, Direction8.NE)]
    public void FromVector_maps_each_cardinal_and_diagonal(float x, float y, Direction8 expected)
    {
        Assert.Equal(expected, Direction8Extensions.FromVector(new Vector2(x, y)));
    }

    [Fact]
    public void FromVector_magnitude_does_not_matter()
    {
        Assert.Equal(Direction8.E, Direction8Extensions.FromVector(new Vector2(500f, 0f)));
        Assert.Equal(Direction8.N, Direction8Extensions.FromVector(new Vector2(0f, -0.001f)));
    }

    [Fact]
    public void FromVector_zero_returns_fallback()
    {
        Assert.Equal(Direction8.S, Direction8Extensions.FromVector(Vector2.Zero));
        Assert.Equal(Direction8.N, Direction8Extensions.FromVector(Vector2.Zero, fallback: Direction8.N));
    }

    // Each sector spans 45 degrees, centred on a cardinal: a cardinal +/- 22 degrees stays put.
    [Fact]
    public void FromVector_stays_within_22_degree_half_sector()
    {
        // 22 degrees clockwise from East (toward south) is still East.
        var justBelowBoundary = AngleVector(22.0);
        Assert.Equal(Direction8.E, Direction8Extensions.FromVector(justBelowBoundary));

        // 23 degrees has crossed into the SE sector.
        var justAboveBoundary = AngleVector(23.0);
        Assert.Equal(Direction8.SE, Direction8Extensions.FromVector(justAboveBoundary));
    }

    [Fact]
    public void FromVector_exact_boundary_rounds_to_higher_clockwise_direction()
    {
        // Exactly 22.5 degrees (the E/SE seam) is documented to round toward SE.
        Assert.Equal(Direction8.SE, Direction8Extensions.FromVector(AngleVector(22.5)));
    }

    [Fact]
    public void FromVector_handles_wrap_around_north_to_east_seam()
    {
        // 350 degrees clockwise from East sits between NE and E, closer to E.
        Assert.Equal(Direction8.E, Direction8Extensions.FromVector(AngleVector(350.0)));
    }

    // Helper: build a unit vector at `degrees` clockwise from +X (east), in y-down screen space.
    private static Vector2 AngleVector(double degrees)
    {
        double rad = degrees * System.Math.PI / 180.0;
        return new Vector2((float)System.Math.Cos(rad), (float)System.Math.Sin(rad));
    }
}
