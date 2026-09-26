using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// <see cref="TemporalJitter"/>: the Halton (2, 3) sequence, its length, and the clip-space shift that moves every
/// projected point by exactly the jitter in pixels, x right and y down, under a perspective and an orthographic
/// projection alike.
/// </summary>
public sealed class TemporalJitterTests
{
    const int W = 64, H = 48;

    [Theory]
    [InlineData(1L, 0.5f, 1f / 3f)]
    [InlineData(2L, 0.25f, 2f / 3f)]
    [InlineData(3L, 0.75f, 1f / 9f)]
    [InlineData(4L, 0.125f, 4f / 9f)]
    [InlineData(5L, 0.625f, 7f / 9f)]
    [InlineData(6L, 0.375f, 2f / 9f)]
    [InlineData(7L, 0.875f, 5f / 9f)]
    [InlineData(8L, 0.0625f, 8f / 9f)]
    public void HaltonIsTheRadicalInverseInBasesTwoAndThree(long index, float base2, float base3)
    {
        Assert.Equal(base2, TemporalJitter.Halton(index, 2), 1e-6f);
        Assert.Equal(base3, TemporalJitter.Halton(index, 3), 1e-6f);
    }

    [Fact]
    public void OffsetIsHaltonAtThePhasePlusOneMinusOneHalf()
    {
        for (long frame = 0; frame < 24; frame++)
        {
            long index = frame % 8 + 1;
            Vector2 offset = TemporalJitter.Offset(frame, 8);
            Assert.Equal(TemporalJitter.Halton(index, 2) - 0.5f, offset.X, 1e-6f);
            Assert.Equal(TemporalJitter.Halton(index, 3) - 0.5f, offset.Y, 1e-6f);
        }
        // Index 0 is the unjittered centre in both bases, so phase 0 starts at index 1.
        Assert.Equal(0f, TemporalJitter.Offset(0, 8).X, 1e-6f);
        Assert.Equal(1f / 3f - 0.5f, TemporalJitter.Offset(0, 8).Y, 1e-6f);
    }

    [Fact]
    public void EveryOffsetLiesInTheHalfOpenPixelAndNoPhaseRepeatsWithinASequence()
    {
        foreach (int phases in new[] { 8, 18, 24, 32, 128 })
        {
            var seen = new HashSet<Vector2>();
            for (long frame = 0; frame < phases; frame++)
            {
                Vector2 offset = TemporalJitter.Offset(frame, phases);
                Assert.InRange(offset.X, -0.5f, 0.49999997f);
                Assert.InRange(offset.Y, -0.5f, 0.49999997f);
                Assert.True(seen.Add(offset), $"phase {frame} of {phases} repeats an earlier offset {offset}");
            }
            Assert.Equal(TemporalJitter.Offset(0, phases), TemporalJitter.Offset(phases, phases));
        }
    }

    [Theory]
    [InlineData(0L, 0)]
    [InlineData(7L, 7)]
    [InlineData(8L, 0)]
    [InlineData(-1L, 7)]
    [InlineData(long.MaxValue, 7)]
    public void PhaseWrapsIntoTheSequence(long frame, int phase) => Assert.Equal(phase, TemporalJitter.Phase(frame, 8));

    [Theory]
    [InlineData(1f, 8)]
    [InlineData(0.5f, 8)]
    [InlineData(float.NaN, 8)]
    [InlineData(1.5f, 18)]
    [InlineData(1.7f, 24)]
    [InlineData(2f, 32)]
    [InlineData(4f, 128)]
    [InlineData(100f, 128)]
    [InlineData(float.PositiveInfinity, 128)]
    public void PhaseCountIsEightAtNativeAndCeilEightRSquaredAbove(float ratio, int expected)
        => Assert.Equal(expected, TemporalJitter.PhaseCount(ratio));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyMovesAProjectedPointByTheJitterInPixelsRightAndDown(bool perspective)
    {
        Matrix4x4 projection = perspective
            ? Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, (float)W / H, 0.1f, 100f)
            : Matrix4x4.CreateOrthographic(12f, 9f, 0.1f, 100f);
        var jitter = new Vector2(0.25f, 0.375f);
        Matrix4x4 jittered = TemporalJitter.Apply(projection, jitter, W, H);
        foreach (Vector3 point in new[] { new Vector3(0.4f, -0.3f, -5f), new Vector3(-2f, 1.5f, -20f), new Vector3(0f, 0f, -1f) })
        {
            Vector2 before = TemporalAssert.Pixel(point, projection, W, H);
            Vector2 after = TemporalAssert.Pixel(point, jittered, W, H);
            Assert.Equal(jitter.X, after.X - before.X, 1e-4f);   // a positive x jitter moves the point right
            Assert.Equal(jitter.Y, after.Y - before.Y, 1e-4f);   // a positive y jitter moves it down, the pixel convention
            Vector4 c0 = Vector4.Transform(new Vector4(point, 1f), projection);
            Vector4 c1 = Vector4.Transform(new Vector4(point, 1f), jittered);
            Assert.Equal(c0.Z, c1.Z);   // depth is untouched
            Assert.Equal(c0.W, c1.W);   // and so is w
        }
    }

    [Fact]
    public void AZeroJitterOrAnEmptyTargetReturnsTheMatrixBitForBit()
    {
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(1f, 1.5f, 0.1f, 50f);
        projection.M12 = -0f;   // a signed zero a multiply by an identity would flip
        TemporalAssert.BitIdentical(projection, TemporalJitter.Apply(projection, Vector2.Zero, W, H), "a zero jitter");
        TemporalAssert.BitIdentical(projection, TemporalJitter.Apply(projection, new Vector2(0.25f, 0.25f), 0, H),
            "a zero-width target");
    }

    [Fact]
    public void ApplyIsTheProductWithTheClipTranslation()
    {
        var random = new Random(1149);
        for (int i = 0; i < 200; i++)
        {
            var m = new Matrix4x4();
            for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++)
                    m[row, column] = (float)(random.NextDouble() * 4.0 - 2.0);
            var jitter = new Vector2((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f);
            Vector2 clip = TemporalJitter.ClipOffset(jitter, W, H);
            Matrix4x4 translation = Matrix4x4.Identity;
            translation.M41 = clip.X;
            translation.M42 = clip.Y;
            TemporalAssert.Close(m * translation, TemporalJitter.Apply(m, jitter, W, H), 1e-5f, $"case {i}");
        }
    }

    [Fact]
    public void ClipOffsetIsTwoPixelsOverTheSizeWithYNegated()
    {
        Assert.Equal(new Vector2(2f * 0.25f / W, -2f * 0.375f / H), TemporalJitter.ClipOffset(new Vector2(0.25f, 0.375f), W, H));
        Assert.Equal(Vector2.Zero, TemporalJitter.ClipOffset(Vector2.Zero, W, H));
    }
}
