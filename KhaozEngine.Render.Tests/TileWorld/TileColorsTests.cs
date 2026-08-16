using System;
using System.Numerics;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>Hex parsing, the deterministic per-tile jitter hash and the corner blend the ground mesher
/// builds its vertex colours from.</summary>
public class TileColorsTests
{
    const float Tolerance = 1e-3f;

    [Fact]
    public void Parse_reads_a_six_digit_hex_as_opaque_rgba()
    {
        Vector4 c = TileColors.Parse("#4d8a3a");
        Assert.Equal(0.302f, c.X, Tolerance);
        Assert.Equal(0.541f, c.Y, Tolerance);
        Assert.Equal(0.227f, c.Z, Tolerance);
        Assert.Equal(1f, c.W, Tolerance);
    }

    [Fact]
    public void Parse_reads_an_eight_digit_hex_alpha()
    {
        Vector4 c = TileColors.Parse("#4D8A3A80");
        Assert.Equal(0.302f, c.X, Tolerance);
        Assert.Equal(128f / 255f, c.W, Tolerance);
    }

    [Theory]
    [InlineData("4d8a3a")]
    [InlineData("#4d8a3")]
    [InlineData("#4d8a3ag0")]
    [InlineData("#zzzzzz")]
    [InlineData("")]
    public void Parse_throws_naming_the_bad_string(string hex)
    {
        var ex = Assert.Throws<TileWorldException>(() => TileColors.Parse(hex));
        Assert.Contains(hex, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_of_a_material_reads_its_color()
    {
        GroundMaterial grass = TileRenderTestData.Catalogs.Material(TileRenderTestData.Grass)!;
        Assert.Equal(TileColors.Parse(grass.Color), TileColors.Parse(grass));
    }

    [Fact]
    public void Jitter_is_deterministic_and_inside_the_amplitude_band()
    {
        for (int z = 0; z < 40; z++)
            for (int x = 0; x < 25; x++)
            {
                float j = TileColors.Jitter(x - 12, z - 20, 0);
                Assert.Equal(j, TileColors.Jitter(x - 12, z - 20, 0));
                Assert.InRange(j, 1f - 0.04f, 1f + 0.04f);
            }
    }

    [Fact]
    public void Jitter_honours_a_custom_amplitude()
    {
        Assert.InRange(TileColors.Jitter(7, 9, 1, 0.5f), 0.5f, 1.5f);
        Assert.Equal(1f, TileColors.Jitter(7, 9, 1, 0f));
    }

    [Fact]
    public void Jitter_differs_between_swapped_coordinates()
    {
        Assert.NotEqual(TileColors.Jitter(1, 2, 0), TileColors.Jitter(2, 1, 0));
    }

    [Fact]
    public void Jitter_differs_between_planes()
    {
        Assert.NotEqual(TileColors.Jitter(3, 4, 0), TileColors.Jitter(3, 4, 1));
    }

    [Fact]
    public void Blend_of_two_colors_is_their_midpoint_at_full_alpha()
    {
        Vector4[] colors = [new Vector4(0f, 0.2f, 1f, 1f), new Vector4(1f, 0.6f, 0f, 1f)];
        Vector4 blended = TileColors.Blend(colors);
        Assert.Equal(0.5f, blended.X, Tolerance);
        Assert.Equal(0.4f, blended.Y, Tolerance);
        Assert.Equal(0.5f, blended.Z, Tolerance);
        Assert.Equal(1f, blended.W, Tolerance);
    }

    [Fact]
    public void Blend_of_an_empty_span_is_void()
    {
        Assert.Equal(TileColors.Void, TileColors.Blend(ReadOnlySpan<Vector4>.Empty));
        Assert.Equal(Vector4.Zero, TileColors.Void);
    }
}
