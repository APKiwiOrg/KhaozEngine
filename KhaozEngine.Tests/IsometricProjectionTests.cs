using System;
using KhaozEngine.Graphics;
using Microsoft.Xna.Framework;
using Xunit;

namespace KhaozEngine.Tests;

public class IsometricProjectionTests
{
    [Fact]
    public void Defaults_to_2to1_footprint_with_height_scale_equal_to_tile_height()
    {
        var proj = new IsometricProjection();
        Assert.Equal(64f, proj.TileWidth);
        Assert.Equal(32f, proj.TileHeight);
        Assert.Equal(32f, proj.HeightScale); // defaults to tile height
    }

    [Fact]
    public void WorldToScreen_matches_the_2to1_formula()
    {
        var proj = new IsometricProjection(64f, 32f);
        // (wx - wy) * 32, (wx + wy) * 16
        Assert.Equal(new Vector2(0f, 0f), proj.WorldToScreen(0f, 0f));
        Assert.Equal(new Vector2(32f, 16f), proj.WorldToScreen(1f, 0f));
        Assert.Equal(new Vector2(-32f, 16f), proj.WorldToScreen(0f, 1f));
        Assert.Equal(new Vector2(0f, 32f), proj.WorldToScreen(1f, 1f));
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(1f, 0f)]
    [InlineData(0f, 1f)]
    [InlineData(3.5f, -2.25f)]
    [InlineData(-7f, 12f)]
    public void WorldToScreen_then_ScreenToGround_round_trips_on_the_ground_plane(float wx, float wy)
    {
        var proj = new IsometricProjection(48f, 24f);
        Vector2 screen = proj.WorldToScreen(wx, wy);
        Vector2 ground = proj.ScreenToGround(screen);
        Assert.Equal(wx, ground.X, 4);
        Assert.Equal(wy, ground.Y, 4);
    }

    [Fact]
    public void RoundTrip_holds_for_a_non_square_footprint_and_default_projection()
    {
        var proj = new IsometricProjection(); // 64x32
        Vector2 ground = proj.ScreenToGround(proj.WorldToScreen(5f, -3f));
        Assert.Equal(5f, ground.X, 4);
        Assert.Equal(-3f, ground.Y, 4);
    }

    [Fact]
    public void Z_lifts_the_point_up_the_screen_by_z_times_heightScale()
    {
        var proj = new IsometricProjection(64f, 32f, heightScale: 20f);
        Vector2 ground = proj.WorldToScreen(2f, 1f, z: 0f);
        Vector2 raised = proj.WorldToScreen(2f, 1f, z: 3f);
        Assert.Equal(ground.X, raised.X);                 // z does not affect x
        Assert.Equal(ground.Y - 3f * 20f, raised.Y, 4);   // up = smaller y
    }

    [Fact]
    public void ScreenToGround_ignores_z_and_returns_the_ground_point()
    {
        // ScreenToGround is the z=0 inverse, so a raised projection maps back to a different
        // ground point (the one that would sit at that screen y on the floor) -- documented.
        var proj = new IsometricProjection();
        Vector2 flat = proj.ScreenToGround(proj.WorldToScreen(4f, 4f, z: 0f));
        Assert.Equal(new Vector2(4f, 4f).X, flat.X, 4);
        Assert.Equal(new Vector2(4f, 4f).Y, flat.Y, 4);
    }

    [Theory]
    [InlineData(0f, 32f, null)]
    [InlineData(64f, 0f, null)]
    [InlineData(-1f, 32f, null)]
    [InlineData(64f, 32f, 0f)]
    [InlineData(64f, 32f, -5f)]
    public void Constructor_rejects_non_positive_dimensions(float w, float h, float? heightScale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IsometricProjection(w, h, heightScale));
    }

    [Theory]
    [InlineData(2f, 1f, 3f)]
    [InlineData(-4f, 6f, 0.5f)]
    [InlineData(0f, 0f, 5f)]
    public void WorldToScreen_then_ScreenToWorld_round_trips_at_an_arbitrary_height_plane(float wx, float wy, float z)
    {
        var proj = new IsometricProjection(64f, 32f, heightScale: 18f);
        Vector2 screen = proj.WorldToScreen(wx, wy, z);
        Vector2 world = proj.ScreenToWorld(screen, z);
        Assert.Equal(wx, world.X, 4);
        Assert.Equal(wy, world.Y, 4);
    }

    [Fact]
    public void ScreenToWorld_at_z_zero_equals_ScreenToGround()
    {
        var proj = new IsometricProjection();
        var screen = new Vector2(120f, -40f);
        Assert.Equal(proj.ScreenToGround(screen), proj.ScreenToWorld(screen, 0f));
    }

    [Fact]
    public void Projection_is_usable_through_the_IIsometricProjection_seam()
    {
        // Consumers depend on the interface so they can swap or fake it in headless tests.
        IIsometricProjection proj = new IsometricProjection(48f, 24f, heightScale: 12f);
        Vector2 screen = proj.WorldToScreen(3f, 2f, z: 1f);
        Vector2 world = proj.ScreenToWorld(screen, z: 1f);
        Assert.Equal(3f, world.X, 4);
        Assert.Equal(2f, world.Y, 4);
        Assert.Equal(12f, proj.HeightScale);
    }
}
