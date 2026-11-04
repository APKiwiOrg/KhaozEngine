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
        Assert.Equal(0.541f, c.Y, Tolerance);
        Assert.Equal(0.227f, c.Z, Tolerance);
        Assert.Equal(128f / 255f, c.W, Tolerance);
    }

    [Theory]
    [InlineData("04d8a3a", "#4d8a3a", TileWorldRefusal.ColourShapeInvalid, null, null)]
    [InlineData("#4d8a3", "#4d8a3a", TileWorldRefusal.ColourShapeInvalid, null, null)]
    [InlineData("#4d8a3ag0", "#4d8a3af0", TileWorldRefusal.ColourDigitInvalid, 7, 'g')]
    [InlineData("#zd8a3a", "#4d8a3a", TileWorldRefusal.ColourDigitInvalid, 1, 'z')]
    [InlineData("#4z8a3a", "#4d8a3a", TileWorldRefusal.ColourDigitInvalid, 2, 'z')]
    [InlineData("", "#4d8a3a", TileWorldRefusal.ColourShapeInvalid, null, null)]
    public void Parse_rejects_malformed_hex(string hex, string control, TileWorldRefusal reason,
                                           int? characterIndex, char? character)
    {
        var ex = Assert.Throws<TileWorldException>(() => TileColors.Parse(hex));
        Assert.Equal(reason, ex.Reason);
        Assert.Equal(hex, ex.ColourText);
        Assert.Equal(characterIndex, ex.CharacterIndex);
        Assert.Equal(character, ex.Character);

        TileColors.Parse(control);
    }

    [Fact]
    public void Parse_of_null_throws_the_same_content_error_as_any_other_bad_colour()
    {
        // A catalog material whose colour was written as JSON null is bad content, not a caller bug, so it fails
        // with the tile-world error every other malformed colour raises rather than as an argument error.
        var ex = Assert.Throws<TileWorldException>(() => TileColors.Parse((string?)null));
        Assert.Equal(TileWorldRefusal.ColourNull, ex.Reason);
        Assert.Null(ex.ColourText);

        var blank = new GroundMaterial { Id = 9, Name = "blank", Color = null! };
        var materialEx = Assert.Throws<TileWorldException>(() => TileColors.Parse(blank));
        Assert.Equal(TileWorldRefusal.ColourNull, materialEx.Reason);
        Assert.Null(materialEx.ColourText);

        Assert.Equal(Vector4.One, TileColors.Parse(new GroundMaterial { Id = 9, Name = "blank", Color = "#ffffff" }));
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
        float baseline = TileColors.Jitter(7, 9, 1);
        float custom = TileColors.Jitter(7, 9, 1, 0.5f);
        Assert.InRange(custom, 0.5f, 1.5f);
        Assert.Equal(1f + (baseline - 1f) * (0.5f / TileColors.DefaultJitterAmplitude), custom, 1e-5f);
        Assert.Equal(1f, TileColors.Jitter(7, 9, 1, 0f));
    }

    [Theory]
    [InlineData(1, 2, 0, 2, 1, 0)] // swapped world coordinates
    [InlineData(3, 4, 0, 3, 4, 1)] // different planes
    public void Jitter_distinguishes_world_coordinates_and_planes(
        int firstX, int firstZ, int firstPlane, int secondX, int secondZ, int secondPlane)
    {
        Assert.NotEqual(TileColors.Jitter(firstX, firstZ, firstPlane), TileColors.Jitter(secondX, secondZ, secondPlane));
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
