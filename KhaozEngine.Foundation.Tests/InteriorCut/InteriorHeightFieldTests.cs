using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.InteriorCut;

public class InteriorHeightFieldTests
{
    [Fact]
    public void RectFillsOnlyWholeCells()
    {
        var window = new InteriorWindow(Vector2.Zero, 0.25f, 8);
        var field = Build([Rect(0f, 0f, 1f, 1f)], window);
        int filledCellCount = CountFilled(field);
        Assert.Equal(16, filledCellCount);
        Assert.True(field.Contains(new Vector3(0.5f, 0f, 0.5f)));
        Assert.False(field.Contains(new Vector3(0.5f, 3f, 0.5f)));
        Assert.False(field.Contains(new Vector3(1f, 1f, 0.5f)));

        var shifted = Build([Rect(0.125f, 0f, 1.125f, 1f)], window);
        bool straddlingCellFilled = shifted.TryGetSpan(0, 0, out _, out _);
        Assert.False(straddlingCellFilled);
        Assert.Equal(12, CountFilled(shifted));
    }

    [Fact]
    public void RotatedPolygonNeverCutsOutside()
    {
        Vector2[] ring = [new(-1f, -1f), new(1f, -1f), new(1f, 1f), new(-1f, 1f)];
        float angle = MathF.PI / 6f;
        ring = ring.Select(p => Vector2.Transform(p, Matrix3x2.CreateRotation(angle))).ToArray();
        var window = new InteriorWindow(new Vector2(-2f), 0.25f, 16);
        var field = Build([new InteriorVolume { Outline = ring, FloorY = 0f, CeilingY = 3f }], window);
        Assert.True(CountFilled(field) > 20);
        for (int z = 0; z < window.Cells; z++)
            for (int x = 0; x < window.Cells; x++)
            {
                Vector2 min = window.Min + new Vector2(x, z) * window.CellSize;
                if (field.TryGetSpan(x, z, out _, out _))
                    foreach (Vector2 corner in new[] { min, min + new Vector2(0.25f, 0f), min + new Vector2(0f, 0.25f), min + new Vector2(0.25f) })
                        Assert.True(InPolygon(ring, corner));
                if (!InPolygon(ring, min + new Vector2(0.125f)))
                    Assert.False(field.Contains(new Vector3(min.X + 0.125f, 1f, min.Y + 0.125f)));
            }
        var reverse = Build([new InteriorVolume { Outline = ring.Reverse().ToArray(), FloorY = 0f, CeilingY = 3f }], window);
        Assert.Equal(field.Texels.ToArray(), reverse.Texels.ToArray());

        // All four cell corners are inside, but a narrow notch crosses its top edge.
        Vector2[] notch = [new(-1f, -1f), new(2f, -1f), new(2f, 2f), new(0.6f, 2f), new(0.6f, 0.5f), new(0.4f, 0.5f), new(0.4f, 2f), new(-1f, 2f)];
        Assert.False(InteriorFieldRasterizer.Covers(notch, Vector2.Zero, 1f));
        Assert.False(InteriorFieldRasterizer.Covers(notch.Reverse().ToArray(), Vector2.Zero, 1f));
    }

    [Fact]
    public void StackedAndDisjointStoreysMergeOnce()
    {
        var merged = Build([Rect(0f, 0f, 1f, 1f, 0f, 3f), Rect(0f, 0f, 1f, 1f, 6f, 9f), Rect(0f, 0f, 1f, 1f, 1f, 2f)], new InteriorWindow(Vector2.Zero, 1f, 1));
        Assert.Equal(1, merged.MergedCells);
        Assert.True(merged.TryGetSpan(0, 0, out float mergedFloor, out float mergedTop));
        Assert.Equal(0f, mergedFloor);
        Assert.Equal(9f, mergedTop);
        Assert.True(merged.Contains(new Vector3(0.5f, 5f, 0.5f)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuantizedEmptyStoreyStillContributesToMergedSpan(bool thinFirst)
    {
        var lower = Rect(0f, 0f, 1f, 1f, 0f, 3f);
        var thin = Rect(0f, 0f, 1f, 1f, 6.001f, 6.009f);
        var merged = Build(thinFirst ? [thin, lower] : [lower, thin], new InteriorWindow(Vector2.Zero, 1f, 1));

        Assert.True(merged.TryGetSpan(0, 0, out float floor, out float top));
        Assert.Equal(0f, floor);
        Assert.Equal(6f, top);
        Assert.Equal(1, merged.MergedCells);
        Assert.Equal(new byte[] { 0, 0, 2, 88 }, merged.Texels.ToArray());
        Assert.True(merged.Contains(new Vector3(0.5f, 5.999f, 0.5f)));
        Assert.False(merged.Contains(new Vector3(0.5f, 6f, 0.5f)));
    }

    [Fact]
    public void QuantizationRoundsDownAndPacksExactly()
    {
        var window = new InteriorWindow(Vector2.Zero, 1f, 1);
        var field = InteriorHeightFieldBuilder.Build([Rect(0f, 0f, 1f, 1f, -7.429f, -4.869f)], window, new InteriorEncoding(-10f, 0.01f));
        byte[] expectedPackedBytes = [1, 1, 2, 1];
        Assert.Equal(expectedPackedBytes, field.Texels.ToArray());
        Assert.True(field.TryGetSpan(0, 0, out float floor, out float top));
        Assert.Equal(-7.43f, floor, 5);
        Assert.Equal(-4.87f, top, 5);
        // The decoded floor rounds below code 257. Its next represented value reaches the code-space span.
        Assert.False(field.Contains(new Vector3(0.5f, floor, 0.5f)));
        Assert.True(field.Contains(new Vector3(0.5f, MathF.BitIncrement(floor), 0.5f)));
        Assert.False(field.Contains(new Vector3(0.5f, top, 0.5f)));

        var centimetreField = InteriorHeightFieldBuilder.Build([], window, InteriorEncoding.For(0f, 655.35f));
        var wideField = InteriorHeightFieldBuilder.Build([], window, InteriorEncoding.For(-100f, 900f));
        Assert.Equal(0.01f, centimetreField.StepY);
        Assert.Equal(-100f, wideField.BaseY);
        Assert.True(wideField.StepY > 0.01f);
        var tiny = Build([Rect(0f, 0f, 1f, 1f, 0.001f, 0.009f)], window);
        Assert.False(tiny.TryGetSpan(0, 0, out _, out _));
    }

    [Fact]
    public void EmptyFieldCutsNothing()
    {
        var window = new InteriorWindow(Vector2.Zero, 0.25f, 4);
        var empty = Build([], window);
        Assert.False(empty.Contains(Vector3.Zero));
        Assert.Equal(new byte[64], empty.Texels.ToArray());
        var offWindow = Build([Rect(-3f, -3f, -2f, -2f), Rect(2f, 2f, 3f, 3f)], window);
        Assert.Equal(empty.Texels.ToArray(), offWindow.Texels.ToArray());
        Assert.NotEqual(empty.Version, offWindow.Version);
        Assert.False(empty.TryGetSpan(-1, 0, out _, out _));
        Assert.False(empty.TryGetSpan(0, 4, out _, out _));
        Assert.False(empty.Contains(new Vector3(float.NaN, 1f, 0f)));
        Assert.Equal(empty.Texels.ToArray(), Build([Rect(0f, 0f, 1f, 1f, 1f, 1f)], window).Texels.ToArray());
    }

    [Theory]
    [InlineData(0.25f, 256)]
    [InlineData(0.24f, 266)]
    public void RecentreKeepsOverlapBytes(float cellSize, int shiftCells)
    {
        InteriorVolume[] volumes = [Rect(0f, 0f, 24f, 24f, -3.129f, 4.567f), Rect(1000f, 1000f, 1001f, 1001f, -20f, 1f)];
        var encoding = InteriorEncoding.For(-20f, 4.567f);
        var before = InteriorHeightFieldBuilder.Build(volumes, InteriorWindow.Centred(Vector2.Zero, cellSize, 640), encoding);
        var after = InteriorHeightFieldBuilder.Build(volumes, InteriorWindow.Centred(new Vector2(64f, 0f), cellSize, 640), encoding);
        byte[] overlapBefore = before.Texels.ToArray().Chunk(640 * 4).SelectMany(row => row.Skip(shiftCells * 4)).ToArray();
        byte[] overlapAfter = after.Texels.ToArray().Chunk(640 * 4).SelectMany(row => row.Take((640 - shiftCells) * 4)).ToArray();
        Assert.Equal(overlapBefore, overlapAfter);
        Assert.Contains(overlapBefore, b => b != 0);
        Assert.Equal(-20f, before.BaseY);
        var snapped = InteriorWindow.Centred(new Vector2(0.13f, -0.13f), 0.25f, 2048);
        Assert.Equal(new Vector2(-256f, -256.25f), snapped.Min);
    }

    [Fact]
    public void EncodingAtMaximumKeepsTheLastRepresentableStep()
    {
        var field = InteriorHeightFieldBuilder.Build([Rect(0f, 0f, 1f, 1f, 0f, 655.35f)], new InteriorWindow(Vector2.Zero, 1f, 1), InteriorEncoding.For(0f, 655.35f));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, field.Texels.ToArray());
        Assert.True(field.TryGetSpan(0, 0, out _, out float top));
        Assert.Equal(655.35f, top);
        Assert.Equal(0, field.OutOfRangeCells);
    }

    [Theory]
    [InlineData(63.999f, 0f, false)]
    [InlineData(64f, 0f, true)]
    [InlineData(-64f, 0f, true)]
    [InlineData(0f, 63.999f, false)]
    [InlineData(0f, -64f, true)]
    public void RecentreStartsAt64MetresOnEitherAxis(float x, float z, bool expected) =>
        Assert.Equal(expected, InteriorWindow.Centred(Vector2.Zero, 0.25f, 2048).NeedsRecentre(new Vector2(x, z), 64f));

    [Theory]
    [InlineData(0.25f, 2048)]
    [InlineData(0.24f, 2134)]
    [InlineData(0.1f, 4096)]
    public void CellsForKeeps512Metres(float cellSize, int expected) =>
        Assert.Equal(expected, InteriorWindow.CellsFor(512f, cellSize));

    [Fact]
    public void EncodingClampsOutOfRangeVolumes()
    {
        var field = InteriorHeightFieldBuilder.Build([Rect(0f, 0f, 2f, 2f, -10f, 1000f), Rect(0f, 0f, 2f, 2f, -1f, 900f)], new InteriorWindow(Vector2.Zero, 1f, 2), new InteriorEncoding(0f, 0.01f));
        Assert.Equal(4, field.OutOfRangeCells);
        Assert.True(field.TryGetSpan(0, 0, out float floor, out float top));
        Assert.Equal(0f, floor);
        Assert.Equal(655.35f, top);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, field.Texels.ToArray()[..4]);
    }

    [Fact]
    public void InvalidGeometryAndWindowsAreRejected()
    {
        var window = new InteriorWindow(Vector2.Zero, 1f, 1);
        Vector2[][] rings = [[], [Vector2.Zero, Vector2.One], [Vector2.Zero, Vector2.UnitX, Vector2.UnitX, Vector2.UnitY], [Vector2.Zero, Vector2.One, Vector2.UnitY, Vector2.UnitX], [Vector2.Zero, Vector2.UnitX, new(2f, 0f)], [Vector2.Zero, Vector2.UnitX, new(float.NaN, 1f)]];
        foreach (Vector2[] ring in rings)
            Assert.ThrowsAny<ArgumentException>(() => Build([new InteriorVolume { Outline = ring, FloorY = 0f, CeilingY = 1f }], window));
        foreach (var invalid in new[] { new InteriorWindow(Vector2.Zero, 0f, 1), new InteriorWindow(Vector2.Zero, float.NaN, 1), new InteriorWindow(Vector2.Zero, 1f, 0), new InteriorWindow(Vector2.Zero, 1f, 4097), new InteriorWindow(new Vector2(float.PositiveInfinity), 1f, 1) })
            Assert.ThrowsAny<ArgumentException>(() => Build([], invalid));
        Assert.ThrowsAny<ArgumentException>(() => Build([Rect(0f, 0f, 1f, 1f, float.NaN, 1f)], window));
        Assert.ThrowsAny<ArgumentException>(() => Build([Rect(0f, 0f, 1f, 1f, 2f, 1f)], window));
        Assert.ThrowsAny<ArgumentException>(() => InteriorHeightFieldBuilder.Build([], window, new InteriorEncoding(0f, 0f)));
        Assert.ThrowsAny<ArgumentException>(() => InteriorEncoding.For(1f, 0f));
        Assert.ThrowsAny<ArgumentException>(() => InteriorEncoding.For(0f, float.PositiveInfinity));
        Assert.ThrowsAny<ArgumentException>(() => InteriorWindow.CellsFor(512f, 0f));
        Assert.ThrowsAny<ArgumentException>(() => InteriorWindow.Centred(Vector2.Zero, 1f, 0));
    }

    internal static InteriorVolume Rect(float x0, float z0, float x1, float z1, float floor = 0f, float top = 3f) =>
        new() { Outline = [new(x0, z0), new(x1, z0), new(x1, z1), new(x0, z1)], FloorY = floor, CeilingY = top };

    internal static InteriorHeightField Build(IReadOnlyList<InteriorVolume> volumes, InteriorWindow window) =>
        InteriorHeightFieldBuilder.Build(volumes, window, new InteriorEncoding(0f, 0.01f));

    static int CountFilled(InteriorHeightField field)
    {
        int count = 0;
        for (int z = 0; z < field.Window.Cells; z++)
            for (int x = 0; x < field.Window.Cells; x++)
                if (field.TryGetSpan(x, z, out _, out _)) count++;
        return count;
    }

    static bool InPolygon(IReadOnlyList<Vector2> ring, Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            Vector2 a = ring[i], b = ring[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (double)(b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }
}
