using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using Xunit;
using static KhaozEngine.Tests.InteriorCut.InteriorHeightFieldTests;

namespace KhaozEngine.Tests.InteriorCut;

public class InteriorRoofRasterizerTests
{
    [Fact]
    public void GableRoofUsesLowestWorldSample()
    {
        var roof = new InteriorRoof(ClearanceSamples.Build(Gable(), 0.125f), Matrix4x4.CreateTranslation(1f, 3f, 0f));
        var volume = Rect(0f, 0f, 2f, 2f) with { Roofs = [roof] };
        var field = Build([volume], new InteriorWindow(Vector2.Zero, 0.25f, 8));

        for (int z = 1; z < 8; z++)
            for (int x = 0; x < 8; x++)
            {
                Assert.True(field.TryGetSpan(x, z, out _, out float top));
                float analyticHeight = 3f + (x < 4 ? (x + 1) * 0.25f : 2f - x * 0.25f);
                Assert.True(top <= analyticHeight);
                Assert.InRange(analyticHeight - top, 0f, 0.25f + field.StepY);
            }
        for (int x = 0; x < 8; x++)
        {
            Assert.True(field.TryGetSpan(x, 0, out _, out float gableRowTop));
            Assert.Equal(3f, gableRowTop);
        }
    }

    [Fact]
    public void RotatedRoofSamplesTheWorldLattice()
    {
        float yaw = MathF.PI / 6f;
        var transform = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(-1.125f, 5f, 0.375f);
        var roof = new InteriorRoof(ClearanceSamples.Build(Gable(), 0.125f), transform);
        Vector2[] outline = new[] { new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(1f, 0f, 2f), new Vector3(-1f, 0f, 2f) }
            .Select(p => Vector3.Transform(p, transform)).Select(p => new Vector2(p.X, p.Z)).ToArray();
        var volume = new InteriorVolume { Outline = outline, FloorY = 2f, CeilingY = 5f, Roofs = [roof] };
        var window = new InteriorWindow(new Vector2(-3f, -1f), 0.25f, 24);
        var field = Build([volume], window);
        int checkedCells = 0;

        for (int z = 0; z < window.Cells; z++)
            for (int x = 0; x < window.Cells; x++)
            {
                if (!field.TryGetSpan(x, z, out _, out float top)) continue;
                float dx = window.Min.X + (x + 0.5f) * 0.25f + 1.125f;
                float dz = window.Min.Y + (z + 0.5f) * 0.25f - 0.375f;
                float localX = MathF.Cos(yaw) * dx - MathF.Sin(yaw) * dz;
                float localZ = MathF.Sin(yaw) * dx + MathF.Cos(yaw) * dz;
                if (localZ < 0.5f || localZ > 1.75f || MathF.Abs(localX) > 0.75f) continue;
                float analyticHeight = 6f - MathF.Abs(localX);
                Assert.True(top <= analyticHeight);
                Assert.InRange(analyticHeight - top, 0f, 0.25f + field.StepY);
                checkedCells++;
            }
        Assert.True(checkedCells > 10);
    }

    [Fact]
    public void MissingSamplesUseCeiling()
    {
        var roof = new InteriorRoof(ClearanceSamples.Build(Plane(0f, 0f, 0.5f, 0.5f, 2f), 0.125f), Matrix4x4.Identity);
        var volume = Rect(0f, 0f, 2f, 2f) with { Roofs = [roof] };
        var field = Build([volume], new InteriorWindow(Vector2.Zero, 0.25f, 8));

        Assert.True(field.TryGetSpan(0, 0, out _, out float sampledTop));
        Assert.Equal(2f, sampledTop);
        Assert.True(field.TryGetSpan(6, 6, out _, out float unsampledTop));
        Assert.Equal(3f, unsampledTop);
    }

    [Fact]
    public void EqualFallbackEndpointsStillUseRoofSamples()
    {
        var roof = new InteriorRoof(ClearanceSamples.Build(Plane(0f, 0f, 0.5f, 0.5f, 0f), 0.125f), Matrix4x4.CreateTranslation(0f, 3f, 0f));
        var volume = Rect(0f, 0f, 2f, 2f, floor: 0f, top: 0f) with { Roofs = [roof] };
        var field = Build([volume], new InteriorWindow(Vector2.Zero, 0.25f, 8));

        Assert.True(field.TryGetSpan(0, 0, out float floor, out float top));
        Assert.Equal(0f, floor);
        Assert.Equal(3f, top);
        Assert.True(field.Contains(new Vector3(0.125f, 0f, 0.125f)));
        Assert.False(field.Contains(new Vector3(0.125f, 3f, 0.125f)));
        Assert.False(field.TryGetSpan(6, 6, out _, out _));
        Assert.False(field.Contains(new Vector3(1.625f, 0f, 1.625f)));
    }

    [Fact]
    public void EavesCannotFillAnOutdoorCell()
    {
        var roof = new InteriorRoof(ClearanceSamples.Build(Plane(-0.5f, -0.5f, 2.5f, 2.5f, 2f), 0.125f), Matrix4x4.Identity);
        var volume = Rect(0.125f, 0.125f, 1.875f, 1.875f) with { Roofs = [roof] };
        var window = new InteriorWindow(new Vector2(-0.5f), 0.25f, 12);
        var field = Build([volume], window);

        Assert.True(field.Contains(new Vector3(0.5f, 1f, 0.5f)));
        Assert.False(field.Contains(new Vector3(-0.125f, 1f, 0.5f)));
        Assert.False(field.Contains(new Vector3(0.125f, 1f, 0.5f)));
        for (int z = 0; z < 12; z++)
            for (int x = 0; x < 12; x++)
                Assert.Equal(x >= 3 && x < 9 && z >= 3 && z < 9, field.TryGetSpan(x, z, out _, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SlabUndersideWinsAcrossRoofOrder(bool reverse)
    {
        var slab = new ClearanceMesh(
            [new(0f, 4f, 0f), new(2f, 4f, 0f), new(2f, 4f, 2f), new(0f, 4f, 2f),
             new(0f, 3f, 0f), new(2f, 3f, 0f), new(2f, 3f, 2f), new(0f, 3f, 2f)],
            [0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6, 0, 4, 5, 0, 5, 1,
             1, 5, 6, 1, 6, 2, 2, 6, 7, 2, 7, 3, 3, 7, 4, 3, 4, 0]);
        InteriorRoof[] roofs = [new(ClearanceSamples.Build(slab, 0.125f), Matrix4x4.Identity),
            new(ClearanceSamples.Build(Plane(0f, 0f, 2f, 2f, 5f), 0.125f), Matrix4x4.Identity)];
        var volume = Rect(0f, 0f, 2f, 2f, top: 6f) with { Roofs = reverse ? roofs.Reverse().ToArray() : roofs };
        var field = Build([volume], new InteriorWindow(Vector2.Zero, 0.25f, 8));

        Assert.True(field.TryGetSpan(4, 4, out _, out float top));
        Assert.Equal(3f, top);
        Assert.True(field.Contains(new Vector3(1.125f, 2.999f, 1.125f)));
        Assert.False(field.Contains(new Vector3(1.125f, 3f, 1.125f)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RoofLoweringPrecedesVolumeMerging(bool thinFirst)
    {
        var roof = new InteriorRoof(ClearanceSamples.Build(Plane(0f, 0f, 1f, 1f, 6.009f), 0.125f), Matrix4x4.Identity);
        var thin = Rect(0f, 0f, 1f, 1f, 6.001f, 9f) with { Roofs = [roof] };
        var lower = Rect(0f, 0f, 1f, 1f);
        var field = Build(thinFirst ? [thin, lower] : [lower, thin], new InteriorWindow(Vector2.Zero, 0.25f, 4));

        Assert.True(field.TryGetSpan(0, 0, out float floor, out float top));
        Assert.Equal(0f, floor);
        Assert.Equal(6f, top);
        Assert.Equal(16, field.MergedCells);
        Assert.Equal(new byte[] { 0, 0, 2, 88 }, field.Texels.ToArray()[..4]);
    }

    [Theory]
    [InlineData(0.25f, 256)]
    [InlineData(0.24f, 266)]
    public void RoofSamplesKeepOverlapBytesAcrossSlicesAndRecentres(float cellSize, int shiftCells)
    {
        var roof = new InteriorRoof(ClearanceSamples.Build(Gable(), 0.125f), Matrix4x4.CreateTranslation(16f, 3f, 15.5f));
        var volume = Rect(0f, 0f, 24f, 24f) with { Roofs = [roof] };
        var before = Build([volume], InteriorWindow.Centred(Vector2.Zero, cellSize, 640));
        var after = Build([volume], InteriorWindow.Centred(new Vector2(64f, 0f), cellSize, 640));
        byte[] overlapBefore = before.Texels.ToArray().Chunk(640 * 4).SelectMany(row => row.Skip(shiftCells * 4)).ToArray();
        byte[] overlapAfter = after.Texels.ToArray().Chunk(640 * 4).SelectMany(row => row.Take((640 - shiftCells) * 4)).ToArray();

        Assert.Equal(overlapBefore, overlapAfter);
        Assert.True(before.Contains(new Vector3(16f, 3.5f, 16.5f)));
    }

    [Fact]
    public void RoofBelowFloorContributesNoSpan()
    {
        var roof = new InteriorRoof(ClearanceSamples.Build(Plane(0f, 0f, 1f, 1f, 1f), 0.125f), Matrix4x4.Identity);
        var empty = Rect(0f, 0f, 1f, 1f, 2f, 6f) with { Roofs = [roof] };
        var window = new InteriorWindow(Vector2.Zero, 0.25f, 4);
        var field = Build([empty, Rect(0f, 0f, 1f, 1f, 4f, 5f)], window);

        Assert.True(field.TryGetSpan(0, 0, out float floor, out float top));
        Assert.Equal(4f, floor);
        Assert.Equal(5f, top);
        Assert.Equal(0, field.MergedCells);
        Assert.False(Build([empty], window).Contains(new Vector3(0.125f, 2f, 0.125f)));
    }

    // Local-zero eaves keep their exact height through surface interpolation and world translation.
    static ClearanceMesh Gable() => new(
        [new(-1f, 0f, 0f), new(0f, 1f, 0f), new(1f, 0f, 0f),
         new(-1f, 0f, 2f), new(0f, 1f, 2f), new(1f, 0f, 2f)],
        [0, 1, 4, 0, 4, 3, 1, 2, 5, 1, 5, 4, 0, 2, 1]);

    static ClearanceMesh Plane(float x0, float z0, float x1, float z1, float y) => new(
        [new(x0, y, z0), new(x1, y, z0), new(x1, y, z1), new(x0, y, z1)], [0, 1, 2, 0, 2, 3]);
}
