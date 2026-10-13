using System;
using System.Numerics;
using System.Threading;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests;

[Collection("AllocSensitive")]
public sealed class NameplateEllipsisCacheTests
{
    [Fact]
    public void ACappedNameDrawnTwiceAllocatesNothingTheSecondTime()
    {
        var template = new NameplateTemplate(new NameplateComponent[]
        {
            new NameplateTextComponent("name", NameplateSlot.Name)
            {
                MaxWidth = 60f,
                OutlineThickness = 1f,
                OutlineColor = Color.Black,
            },
        });
        var content = new NameplateContent();
        content.SetText(NameplateSlot.Name, "Abcdefghijkl", Color.White);
        var canvas = new TextCapturingCanvas(new FixedGlyphFont(6f, 10f));
        NameplatePlacementState state = default;
        var anchor = new Vector2(500f, 500f);

        Assert.True(NameplateRenderer.DrawTemplateAt(ref canvas, anchor, template, content, 1000, 1000, ref state));
        Assert.Equal("Abcdef...", canvas.LastText);
        GC.GetAllocatedBytesForCurrentThread();

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool drawn = NameplateRenderer.DrawTemplateAt(ref canvas, anchor, template, content, 1000, 1000, ref state);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.True(drawn);
        Assert.Equal("Abcdef...", canvas.LastText);
        Assert.Equal(before, after);
    }

    [Fact]
    public void EqualSourceTextReusesTheTrimmedString()
    {
        var font = new FixedGlyphFont();
        const string source = "ABCDEFGHIJKL";
        string equalSource = new(source.ToCharArray());

        string first = NameplateLayout.Ellipsize(font, source, 60f, 1f);
        string second = NameplateLayout.Ellipsize(font, equalSource, 60f, 1f);

        Assert.Equal("ABCDEFG...", first);
        Assert.Same(first, second);
    }

    [Fact]
    public void EachMeasurementInputSelectsItsOwnEllipsis()
    {
        var font = new FixedGlyphFont();
        const string source = "ABCDEFGHIJKL";

        Assert.Equal("ABCDEFG...", NameplateLayout.Ellipsize(font, source, 60f, 1f));
        Assert.Equal("MNOPQRS...", NameplateLayout.Ellipsize(font, "MNOPQRSTUVWX", 60f, 1f));
        Assert.Equal("ABCDE...", NameplateLayout.Ellipsize(font, source, 48f, 1f));
        Assert.Equal("AB...", NameplateLayout.Ellipsize(font, source, 60f, 2f));
        Assert.Equal("ABC...", NameplateLayout.Ellipsize(new FixedGlyphFont(10f), source, 60f, 1f));
        Assert.Equal("ABCDEFG...", NameplateLayout.Ellipsize(font, source, 60f, 1f));
    }

    [Fact]
    public void ManyDifferentNamesEvictTheOldestEllipsis()
    {
        var font = new FixedGlyphFont();
        const string source = "ABCDEFGHIJKL";
        string first = NameplateLayout.Ellipsize(font, source, 60f, 1f);
        Assert.Same(first, NameplateLayout.Ellipsize(font, source, 60f, 1f));

        for (int i = 0; i < 32; i++)
            NameplateLayout.Ellipsize(font, new string((char)('A' + i), 12), 60f, 1f);

        string afterEviction = NameplateLayout.Ellipsize(font, source, 60f, 1f);

        Assert.Equal("ABCDEFG...", afterEviction);
        Assert.NotSame(first, afterEviction);
        Assert.Same(afterEviction, NameplateLayout.Ellipsize(font, source, 60f, 1f));
    }

    [Fact]
    public void TextThatFitsDoesNotEvictCachedNames()
    {
        var font = new FixedGlyphFont();
        var sources = new string[32];
        var trimmed = new string[32];
        for (int i = 0; i < sources.Length; i++)
        {
            sources[i] = new string((char)('A' + i), 12);
            trimmed[i] = NameplateLayout.Ellipsize(font, sources[i], 60f, 1f);
        }

        for (int i = 0; i < 32; i++)
        {
            string fitting = new((char)('A' + i), 10);
            Assert.Same(fitting, NameplateLayout.Ellipsize(font, fitting, 60f, 1f));
        }

        for (int i = 0; i < sources.Length; i++)
            Assert.Same(trimmed[i], NameplateLayout.Ellipsize(font, sources[i], 60f, 1f));
    }

    [Fact]
    public void EachThreadKeepsItsOwnTrimmedStrings()
    {
        var font = new FixedGlyphFont();
        const string source = "ABCDEFGHIJKL";
        string first = NameplateLayout.Ellipsize(font, source, 60f, 1f);
        string? otherFirst = null;
        string? otherSecond = null;
        var thread = new Thread(() =>
        {
            otherFirst = NameplateLayout.Ellipsize(font, source, 60f, 1f);
            otherSecond = NameplateLayout.Ellipsize(font, source, 60f, 1f);
        });
        thread.Start();
        thread.Join();

        Assert.Equal("ABCDEFG...", otherFirst);
        Assert.Same(otherFirst, otherSecond);
        Assert.NotSame(first, otherFirst);
        Assert.Same(first, NameplateLayout.Ellipsize(font, source, 60f, 1f));
    }

    private struct TextCapturingCanvas(ITextMeasurer font) : INameplateCanvas
    {
        public ITextMeasurer Font => font;
        public string? LastText { get; private set; }

        public void FillRounded(Vector4 rect, Color color, float cornerRadius) { }
        public void StrokeRounded(Vector4 rect, Color color, float cornerRadius, float thickness) { }
        public void Text(string text, Vector2 position, Color color, float scale) => LastText = text;
        public void OutlinedText(string text, Vector2 position, Color color, Color outline, float thickness, float scale) =>
            LastText = text;
    }
}
