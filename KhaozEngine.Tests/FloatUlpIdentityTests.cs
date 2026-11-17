using System;
using Xunit;

namespace KhaozEngine.Tests;

// #1832. Every engine float step moved from the BCL to FloatUlp, so engine output stays identical only if FloatUlp
// returns the BCL's bits for every input. This assembly runs at the default floating point controls, where the BCL
// steps are the reference. The project compiles its own copy of build/FloatUlp.cs, the source every engine copy is
// generated from. The bake regression, which needs a changed control, is in KhaozEngine.FloatingPoint.Tests.
public sealed class FloatUlpIdentityTests
{
    // Both zeros, both smallest subnormals, both largest subnormals, both smallest normals, powers of two at an
    // exponent change and their neighbours, both largest finite values, both infinities, quiet and signalling NaNs
    // of both signs, and a NaN payload.
    private static readonly uint[] Boundaries =
    [
        0x00000000u, 0x80000000u, 0x00000001u, 0x80000001u, 0x007FFFFFu, 0x807FFFFFu, 0x00800000u, 0x80800000u,
        0x3F7FFFFFu, 0x3F800000u, 0x3F800001u, 0xBF7FFFFFu, 0xBF800000u, 0xBF800001u, 0x40000000u, 0xC0000000u,
        0x7F7FFFFFu, 0xFF7FFFFFu, 0x7F800000u, 0xFF800000u, 0x7FC00000u, 0xFFC00000u, 0x7F800001u, 0xFF800001u,
        0x7FA00000u, 0x7FC12345u,
    ];

    public static TheoryData<uint> BoundaryRows()
    {
        var rows = new TheoryData<uint>();
        foreach (uint bits in Boundaries) rows.Add(bits);
        return rows;
    }

    [Theory]
    [MemberData(nameof(BoundaryRows))]
    public void BothStepsMatchTheBclAtEveryBoundary(uint bits) => AssertMatches(bits);

    // Every pattern within 256 of each boundary, and a stride of 4099, a prime, across all 2^32 patterns.
    [Fact]
    public void BothStepsMatchTheBclAcrossADeterministicSweep()
    {
        long checkedCount = 0;
        foreach (uint boundary in Boundaries)
        {
            for (int offset = -256; offset <= 256; offset++)
            {
                AssertMatches(unchecked(boundary + (uint)offset));
                checkedCount++;
            }
        }
        for (ulong bits = 0; bits <= uint.MaxValue; bits += 4099)
        {
            AssertMatches((uint)bits);
            checkedCount++;
        }
        Assert.Equal(Boundaries.Length * 513L + 1_047_809L, checkedCount);
    }

    // All 2^32 patterns. The proof behind #1832 ran this once. It is an on-demand cost, never default CI.
    [Fact]
    [Trait("Category", "Diagnostic")]
    public void BothStepsMatchTheBclForEveryPattern()
    {
        for (ulong bits = 0; bits <= uint.MaxValue; bits++) AssertMatches((uint)bits);
    }

    private static void AssertMatches(uint bits)
    {
        float x = BitConverter.UInt32BitsToSingle(bits);
        uint down = BitConverter.SingleToUInt32Bits(FloatUlp.Down(x));
        uint up = BitConverter.SingleToUInt32Bits(FloatUlp.Up(x));
        if (down != BitConverter.SingleToUInt32Bits(MathF.BitDecrement(x)))
            Assert.Fail($"Down(0x{bits:X8}) = 0x{down:X8}, BCL 0x{BitConverter.SingleToUInt32Bits(MathF.BitDecrement(x)):X8}");
        if (up != BitConverter.SingleToUInt32Bits(MathF.BitIncrement(x)))
            Assert.Fail($"Up(0x{bits:X8}) = 0x{up:X8}, BCL 0x{BitConverter.SingleToUInt32Bits(MathF.BitIncrement(x)):X8}");
    }
}
