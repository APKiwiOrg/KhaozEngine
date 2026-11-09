using System;
using System.Numerics;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Contract v2 permits native XYZ direction error only. Every other record word stays exact.</summary>
internal static class CoverageDirectionAssertions
{
    internal const float ComponentTolerance = 2e-5f;

    internal static void Equal(Vector3 expected, Vector3 actual)
    {
        Assert.True(float.IsFinite(actual.X) && float.IsFinite(actual.Y) && float.IsFinite(actual.Z));
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0, ComponentTolerance);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0, ComponentTolerance);
        Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0, ComponentTolerance);
    }

    internal static void EqualRecords(ReadOnlySpan<uint> expected, ReadOnlySpan<uint> actual)
    {
        Assert.True(expected.Length > 0 && expected.Length % 16 == 0);
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            int field = i % 16;
            if (field is 3 or 12)
            {
                Vector3 a = new(Float(expected[i]), Float(expected[i + 1]), Float(expected[i + 2]));
                Vector3 b = new(Float(actual[i]), Float(actual[i + 1]), Float(actual[i + 2]));
                if (a == Vector3.Zero)
                    Assert.Equal(expected.Slice(i, 3).ToArray(), actual.Slice(i, 3).ToArray());
                else Equal(a, b);
                i += 2;
            }
            else Assert.Equal(expected[i], actual[i]);
        }
    }

    static float Float(uint word) => BitConverter.UInt32BitsToSingle(word);
}
