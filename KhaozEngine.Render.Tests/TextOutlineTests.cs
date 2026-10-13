using System;
using System.Numerics;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests;

public sealed class TextOutlineTests
{
    [Fact]
    public void Offsets_ofTwo_areTheEightNeighboursScaled()
    {
        Span<Vector2> offsets = stackalloc Vector2[8];

        TextOutline.Offsets(2f, offsets);

        Assert.Equal(new[]
        {
            new Vector2(-2f, -2f),
            new Vector2(0f, -2f),
            new Vector2(2f, -2f),
            new Vector2(-2f, 0f),
            new Vector2(2f, 0f),
            new Vector2(-2f, 2f),
            new Vector2(0f, 2f),
            new Vector2(2f, 2f),
        }, offsets.ToArray());
    }

    [Fact]
    public void Offsets_rejectsAShortSpan()
    {
        Assert.Throws<ArgumentException>(() => TextOutline.Offsets(1f, new Vector2[7]));
    }

    [Fact]
    public void Measure_addsTheOutlineOnBothSides()
    {
        Vector2 size = TextOutline.Measure(new FixedGlyphFont(), "Zombie", 1f, 1f);

        Assert.Equal(new Vector2(38f, 12f), size);
    }

    [Fact]
    public void Measure_treatsANegativeThicknessAsNone()
    {
        Vector2 size = TextOutline.Measure(new FixedGlyphFont(), "Zombie", 1f, -2f);

        Assert.Equal(new Vector2(36f, 10f), size);
    }
}

internal sealed class FixedGlyphFont(float charWidth = 6f, float lineHeight = 10f) : ITextMeasurer
{
    public float LineHeight { get; } = lineHeight;

    public Vector2 Measure(string text) => new(text.Length * charWidth, LineHeight);
}
