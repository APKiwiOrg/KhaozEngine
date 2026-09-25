using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// Assertions the temporal foundations tests share: bit-exact and tolerant matrix equality, and the projection
/// convention the motion target uses, so every test measures a shift the same way.
/// </summary>
internal static class TemporalAssert
{
    /// <summary>Fail unless the two matrices are identical bit for bit, signed zeros included. This is the bar a frame
    /// with temporal rendering off has to meet, because a golden compares the pixels those bits produce.</summary>
    internal static void BitIdentical(in Matrix4x4 expected, in Matrix4x4 actual, string what)
    {
        ReadOnlySpan<byte> e = MemoryMarshal.AsBytes(new ReadOnlySpan<Matrix4x4>(in expected));
        ReadOnlySpan<byte> a = MemoryMarshal.AsBytes(new ReadOnlySpan<Matrix4x4>(in actual));
        Assert.True(e.SequenceEqual(a), $"{what} is not bit-identical.\nexpected {expected}\nactual   {actual}");
    }

    /// <summary>Fail unless every element agrees within <paramref name="tolerance"/>, scaled by the element's size when
    /// that exceeds one.</summary>
    internal static void Close(in Matrix4x4 expected, in Matrix4x4 actual, float tolerance, string what)
    {
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
            {
                float e = expected[row, column], a = actual[row, column];
                float bound = tolerance * MathF.Max(1f, MathF.Abs(e));
                Assert.True(MathF.Abs(e - a) <= bound,
                    $"{what}: M{row + 1}{column + 1} is {a}, expected {e} within {bound}.");
            }
    }

    /// <summary>A render-relative point's UV through <paramref name="viewProjection"/>, in the motion target's
    /// convention <c>uv = ndc.xy * (0.5, -0.5) + 0.5</c>, so u runs right and v runs down.</summary>
    internal static Vector2 Uv(Vector3 point, in Matrix4x4 viewProjection)
    {
        Vector4 clip = Vector4.Transform(new Vector4(point, 1f), viewProjection);
        return new Vector2(clip.X / clip.W * 0.5f + 0.5f, clip.Y / clip.W * -0.5f + 0.5f);
    }

    /// <summary>The same point in pixels of a <paramref name="width"/> by <paramref name="height"/> target, top-left
    /// origin and y down, the convention of <c>CameraProjection.WorldToScreen</c>.</summary>
    internal static Vector2 Pixel(Vector3 point, in Matrix4x4 viewProjection, int width, int height)
        => Uv(point, viewProjection) * new Vector2(width, height);
}
