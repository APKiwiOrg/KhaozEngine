using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render2D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render2D;

public sealed class DpiFontBindingTests
{
    [Fact]
    public void ARetainedFontUsesTheNewDensityForMetricsAtlasAndBothTextPaths()
    {
        float density = 1f;
        int densityReads = 0;
        var textures = new List<FakeTexture>();
        using var source = new DpiFont(16f, value => CreateFont(value, textures));
        SpriteFont retained = source.Bind(() => { densityReads++; return density; });
        Assert.Equal(0, source.BakeCount);

        Assert.Equal(new Vector2(12f, 16f), retained.Measure("AA"));
        Assert.Equal(1f, retained.RenderScale);

        density = 2f;
        Assert.Equal(new Vector2(14f, 18f), retained.Measure("AA"));
        Assert.Equal(new Vector2(14f, 18f), retained.Measure("AA".AsSpan()));
        Assert.Equal(18f, retained.LineHeight);
        Assert.Equal(0.5f, retained.RenderScale);
        Assert.True(textures[0].Disposed);
        Assert.Same(textures[1], retained.Atlas.Handle);

        var batch = (SpriteBatch)RuntimeHelpers.GetUninitializedObject(typeof(SpriteBatch));
        densityReads = 0;
        Assert.Equal(new[] { new Vector4(10f, 30f, 6f, 9f) },
            batch.DebugGlyphDests(retained, "A", new Vector2(10f, 20f), 1f));
        Assert.Equal(1, densityReads);

        densityReads = 0;
        Assert.Equal(new[] { new Vector4(10f, 30f, 6f, 9f) },
            batch.DebugGlyphDestsRuns(retained, [new ColoredTextRun("A", Vector4.One)],
                new Vector2(10f, 20f), 1f));
        Assert.Equal(1, densityReads);
        Assert.Equal(2, source.BakeCount);
    }

    [Fact]
    public void TheDpiFontOwnsTheAtlasAndItsBoundViewCannotResurrectItAfterDisposal()
    {
        var textures = new List<FakeTexture>();
        using var source = new DpiFont(16f, value => CreateFont(value, textures));
        SpriteFont retained = source.Bind(() => 1f);
        Assert.Equal(new Vector2(6f, 16f), retained.Measure("A"));

        retained.Dispose();
        Assert.False(textures[0].Disposed);
        Assert.Equal(new Vector2(6f, 16f), retained.Measure("A"));

        source.Dispose();
        Assert.True(textures[0].Disposed);
        Assert.Throws<ObjectDisposedException>(() => retained.Measure("A"));
        Assert.Throws<ObjectDisposedException>(() => source.For(2f));
        Assert.Throws<ObjectDisposedException>(() => source.Bind(() => 1f));
        Assert.Single(textures);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidBoundDensityIsRejectedBeforeBaking(float invalid)
    {
        var textures = new List<FakeTexture>();
        using var source = new DpiFont(16f, value => CreateFont(value, textures));
        SpriteFont retained = source.Bind(() => invalid);

        Assert.Throws<ArgumentOutOfRangeException>(() => retained.Measure("A"));
        Assert.Empty(textures);
    }

    [Fact]
    public void ABindingBelowNativeDensityUsesTheExistingMinimumFontBakeDensity()
    {
        var textures = new List<FakeTexture>();
        using var source = new DpiFont(16f, value => CreateFont(value, textures));
        SpriteFont retained = source.Bind(() => 0.5f);

        Assert.Equal(new Vector2(6f, 16f), retained.Measure("A"));
        Assert.Equal(1f, source.BakedDpiScale);
        Assert.Throws<ArgumentNullException>(() => source.Bind(null!));
    }

    static SpriteFont CreateFont(float density, List<FakeTexture> textures)
    {
        var texture = new FakeTexture(64, 64, 1, 1, GpuPixelFormat.R8G8B8A8UNorm);
        textures.Add(texture);
        // A small literal glyph table isolates the binding contract from rasterizer output and GPU setup.
        ConstructorInfo constructor = typeof(SpriteFont).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, [typeof(Texture2D), typeof(int), typeof(int), typeof(float), typeof(float), typeof(float)],
            modifiers: null)!;
        var font = (SpriteFont)constructor.Invoke([Texture2D.Wrap(texture, 64, 64), 64, 64,
            density == 1f ? 9f : 10f, density == 1f ? 16f : 18f, 1f / density]);
        font.Glyphs['A'] = new GlyphInfo
        {
            W = density == 1f ? 5 : 12,
            H = density == 1f ? 8 : 18,
            Advance = density == 1f ? 6f : 7f,
        };
        return font;
    }
}
