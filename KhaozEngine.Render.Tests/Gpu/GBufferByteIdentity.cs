using System;
using System.Collections.Generic;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Exact attachment comparisons with numeric diagnostics, never a tolerance.</summary>
internal static class GBufferByteIdentity
{
    static readonly string[] Names = ["HDR", "normal", "depth", "motion", "visibility", "display", "history colour",
        "history confidence", "previous depth", "shadow atlas"];
    static readonly int[] PixelBytes = [8, 4, 4, 4, 1, 4, 8, 4, 4, 4];
    static readonly int[] ComponentBytes = [2, 1, 4, 2, 1, 1, 2, 2, 4, 4];

    internal static void AssertAll(byte[][] expected, byte[][] actual)
    {
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Length, actual.Length);
        var differences = new List<string>();
        for (int image = 0; image < expected.Length; image++)
        {
            byte[] a = expected[image], b = actual[image];
            Assert.Equal(a.Length, b.Length);
            int slot = image % Names.Length, stride = ComponentBytes[slot];
            int components = 0;
            var pixels = new HashSet<int>();
            double maximum = 0;
            uint maximumUlp = 0;
            for (int offset = 0; offset < a.Length; offset += stride)
            {
                if (a.AsSpan(offset, stride).SequenceEqual(b.AsSpan(offset, stride))) continue;
                components++;
                pixels.Add(offset / PixelBytes[slot]);
                double x = stride == 1 ? a[offset] / 255.0 : stride == 2
                    ? (double)BitConverter.ToHalf(a, offset) : BitConverter.ToSingle(a, offset);
                double y = stride == 1 ? b[offset] / 255.0 : stride == 2
                    ? (double)BitConverter.ToHalf(b, offset) : BitConverter.ToSingle(b, offset);
                maximum = Math.Max(maximum, Math.Abs(x - y));
                uint ux = Ordered(a, offset, stride), uy = Ordered(b, offset, stride);
                maximumUlp = Math.Max(maximumUlp, ux > uy ? ux - uy : uy - ux);
            }
            if (components > 0)
                differences.Add($"frame {image / Names.Length}, {Names[slot]}: {pixels.Count} changed pixels, "
                    + $"{components} changed components, max delta {maximum:R}, max ULP/code delta {maximumUlp}");
        }
        Assert.True(differences.Count == 0, string.Join("\n", differences));
    }

    static uint Ordered(byte[] bytes, int offset, int stride)
    {
        if (stride == 1) return bytes[offset];
        uint bits = stride == 2 ? BitConverter.ToUInt16(bytes, offset) : BitConverter.ToUInt32(bytes, offset);
        uint sign = stride == 2 ? 0x8000u : 0x80000000u;
        uint mask = stride == 2 ? 0xffffu : uint.MaxValue;
        return (bits & sign) != 0 ? ~bits & mask : bits | sign;
    }
}
