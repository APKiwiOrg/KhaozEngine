using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.WorldMap;
using Xunit;

namespace KhaozEngine.Tests.WorldMap;

/// <summary>Normalized symbol geometry rejects unusable outlines and owns immutable snapshots.</summary>
public sealed class SymbolGeometryValidationTests
{
    /// <summary>Callers cannot replace vertices or segments after construction.</summary>
    [Fact]
    public void SymbolGeometry_SnapshotsInputsAndExposesReadOnlyCollections()
    {
        Vector2[] outline = [new(-1f), new(1f, -1f), new(1f), new(-1f, 1f)];
        var strokes = new List<WorldMapSymbolStroke> { new(new(-0.5f, 0f), new(0.5f, 0f)) };
        var geometry = new WorldMapSymbolGeometry(outline, strokes);
        outline[0] = Vector2.Zero;
        strokes[0] = new(Vector2.Zero, Vector2.One);
        strokes.Clear();

        Assert.Equal(new Vector2(-1f), geometry.Outline[0]);
        Assert.Equal(new WorldMapSymbolStroke(new(-0.5f, 0f), new(0.5f, 0f)), Assert.Single(geometry.Strokes));
        if (geometry.Outline is IList<Vector2> mutableOutline)
            Assert.Throws<NotSupportedException>(() => mutableOutline[0] = Vector2.Zero);
        if (geometry.Strokes is IList<WorldMapSymbolStroke> mutableStrokes)
            Assert.Throws<NotSupportedException>(() => mutableStrokes.Clear());
        Assert.Equal(4, geometry.Outline.Count);
        Assert.Single(geometry.Strokes);
    }

    /// <summary>Optional segments default to an empty snapshot and either winding is accepted.</summary>
    [Fact]
    public void SymbolGeometry_ConcaveOutlineAcceptsBothWindings()
    {
        Vector2[] outline = SymbolGeometryRasterTests.Concave;
        Assert.Equal(outline, new WorldMapSymbolGeometry(outline).Outline);
        Assert.Equal(outline.Reverse(), new WorldMapSymbolGeometry(outline.Reverse()).Outline);
        Assert.Empty(new WorldMapSymbolGeometry(outline).Strokes);
    }

    /// <summary>A missing outline is rejected immediately.</summary>
    [Fact]
    public void SymbolGeometry_NullOutlineIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => new WorldMapSymbolGeometry(null!));

    /// <summary>A straight continuation is simple and does not overlap the preceding edge.</summary>
    [Fact]
    public void SymbolGeometry_CollinearContinuationIsAccepted()
    {
        var geometry = new WorldMapSymbolGeometry([new(-1f), new(0f, -1f), new(1f, -1f), new(1f), new(-1f, 1f)]);
        Assert.Equal(5, geometry.Outline.Count);
    }

    /// <summary>Small nonzero areas are valid without an arbitrary normalized-size threshold.</summary>
    [Fact]
    public void SymbolGeometry_SmallNondegenerateOutlineIsAccepted()
    {
        var geometry = new WorldMapSymbolGeometry([Vector2.Zero, new(1e-25f, 0f), new(0f, 1e-25f)]);
        Assert.Equal(3, geometry.Outline.Count);
    }

    /// <summary>All coordinates must be finite and inside the normalized footprint.</summary>
    [Theory]
    [InlineData(float.NaN, 0f)]
    [InlineData(0f, float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity, 0f)]
    [InlineData(-1.001f, 0f)]
    [InlineData(0f, 1.001f)]
    public void SymbolGeometry_InvalidOutlinePointIsRejected(float x, float y) =>
        Assert.Throws<ArgumentException>(() => new WorldMapSymbolGeometry([new(x, y), new(1f, -1f), new(1f, 1f)]));

    /// <summary>Both segment endpoints use the same normalized coordinate contract.</summary>
    [Theory]
    [InlineData(float.NaN, 0f)]
    [InlineData(0f, float.PositiveInfinity)]
    [InlineData(-1.001f, 0f)]
    [InlineData(0f, 1.001f)]
    public void SymbolGeometry_InvalidStrokePointIsRejected(float x, float y)
    {
        Assert.Throws<ArgumentException>(() => new WorldMapSymbolGeometry(SymbolGeometryRasterTests.Rectangle,
            [new(new(x, y), Vector2.Zero)]));
        Assert.Throws<ArgumentException>(() => new WorldMapSymbolGeometry(SymbolGeometryRasterTests.Rectangle,
            [new(Vector2.Zero, new(x, y))]));
    }

    /// <summary>Outlines must enclose an area without repeated vertices, crossings or touching edges.</summary>
    [Theory]
    [MemberData(nameof(InvalidOutlines))]
    public void SymbolGeometry_DegenerateOrNonSimpleOutlineIsRejected(Vector2[] outline) =>
        Assert.Throws<ArgumentException>(() => new WorldMapSymbolGeometry(outline));

    /// <summary>Malformed outline fixtures cover each independent topology failure.</summary>
    public static IEnumerable<object[]> InvalidOutlines()
    {
        yield return [Array.Empty<Vector2>()];
        yield return [new Vector2[] { new(-1f), new(1f) }];
        yield return [new Vector2[] { new(-1f, 0f), new(0f, 0f), new(1f, 0f) }];
        yield return [new Vector2[] { new(-1f), new(1f, -1f), new(1f), new(-1f), new(-1f, 1f) }];
        yield return [new Vector2[] { new(-1f), new(1f, -1f), new(1f), new(-1f, 1f), new(-1f) }];
        yield return [new Vector2[] { new(-1f), new(1f), new(-1f, 1f), new(1f, -0.5f) }];
        yield return [new Vector2[] { new(-1f), new(1f, -1f), new(0f, -1f), new(1f), new(-1f, 1f) }];
        yield return [new Vector2[] { new(-1f), new(1f, -1f), new(1f), new(0f, -1f), new(-1f, 1f) }];
    }
}
