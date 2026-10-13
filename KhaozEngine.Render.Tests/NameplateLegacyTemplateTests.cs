using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests;

[Collection("AllocSensitive")]
public sealed class NameplateLegacyTemplateTests
{
    [Fact]
    public void StyleEqualityCoversEveryProperty()
    {
        NameplateStyle original = NameplateStyle.Default;
        var equality = Assert.IsAssignableFrom<IEquatable<NameplateStyle>>(original);
        foreach (PropertyInfo property in typeof(NameplateStyle).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object copy = original;
            property.SetValue(copy, ChangedValue(property.PropertyType, property.GetValue(copy)));
            var changed = (NameplateStyle)copy;

            Assert.False(equality.Equals(changed), $"Equality omitted {property.Name}.");
            Assert.False(changed.Equals(original), $"Object equality omitted {property.Name}.");
        }
    }

    [Fact]
    public void EqualStylesHaveEqualHashesAndOperators()
    {
        NameplateStyle original = NameplateStyle.Default;
        NameplateStyle copy = original with { };
        var equality = Assert.IsAssignableFrom<IEquatable<NameplateStyle>>(original);

        Assert.True(equality.Equals(copy));
        Assert.True(original.Equals((object)copy));
        Assert.False(original.Equals(null));
        Assert.Equal(original.GetHashCode(), copy.GetHashCode());
        Assert.True(original == copy);
        Assert.False(original != copy);
        Assert.False(original == NameplateStyle.TextOnly);
        Assert.True(original != NameplateStyle.TextOnly);
    }

    [Fact]
    public void AWarmLegacyStyleReusesItsTemplate()
    {
        NameplateTemplate first = LegacyFor(NameplateStyle.Default, 1);

        Assert.Same(first, LegacyFor(NameplateStyle.Default, 1));
        Assert.Same(first, LegacyFor(NameplateStyle.Default, 0));
        Assert.Same(first, LegacyFor(NameplateStyle.Default, 4));
        Assert.Equal(5, first.Components.Count);
    }

    [Fact]
    public void AStyleFadingEveryFrameKeepsTheCacheBounded()
    {
        var canvas = Canvas();
        var plate = Plate(1);
        NameplateStyle style = default;
        for (int frame = 0; frame < 100; frame++)
        {
            style = NameplateStyle.Default with { PanelFill = NameplateStyle.Default.PanelFill.WithAlpha(frame / 100f) };
            canvas.Calls.Clear();
            Assert.True(Draw(canvas, plate, style));
            Assert.InRange(CacheCount(), 1, 8);
        }

        NameplateTemplate cached = LegacyFor(style, 1);
        NameplateTemplate? fresh = null;
        OnFreshThread(() => fresh = LegacyFor(style, 1));
        Assert.NotNull(fresh);
        Assert.NotSame(cached, fresh);
        var content = new NameplateContent();
        content.SetText(NameplateSlot.Name, "Goblin", Color.White);
        content.SetBar(new NameplateSlot("legacy.bar.0"), 0.5f, Color.White, Color.Black);
        var freshCanvas = Canvas();
        NameplatePlacementState state = default;

        Assert.True(NameplateRenderer.DrawTemplateAt(ref freshCanvas, new Vector2(500f, 500f), fresh,
            content, 1000, 1000, ref state));
        Assert.Equal(freshCanvas.Calls, canvas.Calls);
        Assert.Equal("fill 447.000 470.000 106.000 30.000 0.080 0.090 0.110 0.990 6.000", canvas.Calls[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void AWarmLegacyDrawAllocatesNothing(int barCount)
    {
        var canvas = new CountingCanvas(new FixedGlyphFont(6f, 10f));
        Nameplate plate = Plate(barCount);
        NameplateStyle style = NameplateStyle.Default;
        NameplatePlacementState state = default;
        var anchor = new Vector2(500f, 500f);
        Assert.True(NameplateRenderer.DrawAt(ref canvas, anchor, plate, style, 1000, 1000, ref state));
        Assert.True(NameplateRenderer.DrawAt(ref canvas, anchor, plate, style, 1000, 1000, ref state));
        int warmCalls = canvas.Calls;
        GC.GetAllocatedBytesForCurrentThread();

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool drawn = NameplateRenderer.DrawAt(ref canvas, anchor, plate, style, 1000, 1000, ref state);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.True(drawn);
        Assert.Equal(warmCalls + warmCalls / 2, canvas.Calls);
        Assert.Equal(before, after);
    }

    [Fact]
    public void FiveBarsStillDraw()
    {
        NameplateTemplate first = LegacyFor(NameplateStyle.Default, 5);
        int cachedBefore = CacheCount();
        Assert.NotSame(first, LegacyFor(NameplateStyle.Default, 5));
        Assert.Equal(cachedBefore, CacheCount());
        Assert.Equal(6, first.Components.Count);
        var canvas = Canvas();

        Assert.True(Draw(canvas, Plate(5), NameplateStyle.Default));

        Assert.Equal(13, canvas.Calls.Count);
        Assert.Equal("fill 447.000 430.000 106.000 70.000 0.080 0.090 0.110 0.920 6.000", canvas.Calls[0]);
        Assert.Equal("text \"Goblin\" 482.000 435.000 1.000 1.000 1.000 1.000 1.000", canvas.Calls[2]);
        for (int bar = 0; bar < 5; bar++)
        {
            int y = 448 + 10 * bar;
            Assert.Equal($"fill 455.000 {y}.000 90.000 7.000 0.000 0.000 0.000 1.000 2.000", canvas.Calls[3 + bar * 2]);
            Assert.Equal($"fill 455.000 {y}.000 45.000 7.000 1.000 1.000 1.000 1.000 2.000", canvas.Calls[4 + bar * 2]);
        }
    }

    [Fact]
    public void FillingContentClearsOldBarsAndRestoresOpacity()
    {
        var content = new NameplateContent();
        LegacyFill(content, Plate(5));
        content.Opacity = 0.3f;

        LegacyFill(content, new Nameplate { Title = "Ox", TitleColor = Color.Black });

        Assert.Equal(1f, content.Opacity);
        Assert.True(content.TryGetText(NameplateSlot.Name, out var title));
        Assert.Equal("Ox", title.Text);
        Assert.Equal(Color.Black, title.Color);
        for (int bar = 0; bar < 5; bar++)
            Assert.False(content.TryGetBar(new NameplateSlot($"legacy.bar.{bar}"), out _));
    }

    [Theory]
    [InlineData(16f)]
    [InlineData(8f)]
    public void ACapNoWiderThanPaddingMapsToUnboundedContent(float maxWidth)
    {
        var style = NameplateStyle.Default with { MaxWidth = maxWidth };

        Assert.Equal(0f, LegacyFor(style, 1).MaxContentWidth);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void AnEmptyLegacyBarDrawsOnlyItsTrack(float fraction)
    {
        var canvas = Canvas();
        var plate = new Nameplate { Title = "", Bars = new[] { new NameplateBar(fraction, Color.White, Color.Transparent) } };
        var style = NameplateStyle.Default with { PanelFill = Color.Transparent, PanelBorder = Color.Transparent };
        LegacyFor(style, 1);

        Assert.True(Draw(canvas, plate, style));

        Assert.Equal(new[] { "fill 455.000 488.000 90.000 7.000 0.000 0.000 0.000 0.000 2.000" }, canvas.Calls);
    }

    [Fact]
    public void ThreadContentDoesNotKeepThePreviousPlatesBars()
    {
        var canvas = Canvas();
        LegacyFor(NameplateStyle.Default, 4);
        Assert.True(Draw(canvas, Plate(4), NameplateStyle.Default));
        canvas.Calls.Clear();

        Assert.True(Draw(canvas, new Nameplate { Title = "Ox", TitleColor = Color.White }, NameplateStyle.Default));

        Assert.Equal(3, canvas.Calls.Count);
        Assert.Equal("fill 447.000 480.000 106.000 20.000 0.080 0.090 0.110 0.920 6.000", canvas.Calls[0]);
        Assert.Equal("text \"Ox\" 494.000 485.000 1.000 1.000 1.000 1.000 1.000", canvas.Calls[2]);
    }

    private struct CountingCanvas(ITextMeasurer font) : INameplateCanvas
    {
        public ITextMeasurer Font => font;
        public int Calls { get; private set; }

        public void FillRounded(Vector4 rect, Color color, float cornerRadius) => Calls++;
        public void StrokeRounded(Vector4 rect, Color color, float cornerRadius, float thickness) => Calls++;
        public void Text(string text, Vector2 position, Color color, float scale) => Calls++;
        public void OutlinedText(string text, Vector2 position, Color color, Color outline, float thickness, float scale) =>
            Calls++;
    }

    private static object? ChangedValue(Type type, object? value)
    {
        if (type == typeof(float)) return (float)value! + 1f;
        if (type == typeof(Color))
        {
            var color = (Color)value!;
            return new Color(color.R + 0.125f, color.G, color.B, color.A);
        }
        if (type == typeof(Color?)) return value is null ? Color.White : null;
        if (type == typeof(Vector2)) return (Vector2)value! + Vector2.One;
        if (type.IsEnum) return Enum.GetValues(type).Cast<object>().First(candidate => !candidate.Equals(value));
        throw new InvalidOperationException($"Unsupported NameplateStyle property type {type.FullName}. Add a changed value to the equality test.");
    }

    private static NameplateTemplate LegacyFor(NameplateStyle style, int barCount) =>
        NameplateLegacyTemplate.For(style, barCount);

    private static void LegacyFill(NameplateContent content, Nameplate plate) =>
        NameplateLegacyTemplate.Fill(content, plate);

    private static int CacheCount()
    {
        FieldInfo? field = typeof(NameplateLegacyTemplate).GetField("_count", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<int>(field.GetValue(null));
    }

    private static void OnFreshThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        });
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    private static RecordingNameplateCanvas Canvas() => new(new FixedGlyphFont(6f, 10f));

    private static Nameplate Plate(int barCount) => new()
    {
        Title = "Goblin",
        TitleColor = Color.White,
        Bars = Enumerable.Repeat(new NameplateBar(0.5f, Color.White, Color.Black), barCount).ToArray(),
    };

    private static bool Draw(RecordingNameplateCanvas canvas, in Nameplate plate, in NameplateStyle style)
    {
        NameplatePlacementState state = default;
        return NameplateRenderer.DrawAt(ref canvas, new Vector2(500f, 500f), plate, style, 1000, 1000, ref state);
    }
}
