using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// The point caster signature's mixer: xxHash64's round over whole 64-bit words, with fixed constants. Fixed is the
/// point. <c>System.HashCode</c> is seeded per process, and while a signature is only compared within one process, a
/// mixer that changed between runs would make a rebuild count irreproducible.
/// </summary>
public sealed class PointSignatureMixerTests
{
    [Theory]
    [InlineData(0UL, 0UL)]
    [InlineData(0x0123456789ABCDEFUL, 1UL)]
    [InlineData(ulong.MaxValue, 0x8000000000000000UL)]
    public void OneWordIsOneFixedMultiplyAndRotateRound(ulong hash, ulong word)
    {
        ulong expected = BitOperations.RotateLeft(hash + word * 0xC2B2AE3D27D4EB4FUL, 31) * 0x9E3779B185EBCA87UL;
        Scene3D.MixPointSignature(ref hash, word);
        Assert.Equal(expected, hash);
    }

    [Fact]
    public void AMatrixIsEightWordsOfTwoElementsInRowOrder()
    {
        var m = new Matrix4x4(1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f, 11f, 12f, 13f, 14f, 15f, 16f);
        ulong byMatrix = 7UL, byWords = 7UL;
        Scene3D.MixPointSignature(ref byMatrix, m);
        for (int element = 1; element <= 16; element += 2)
            Scene3D.MixPointSignature(ref byWords, (uint)BitConverter.SingleToInt32Bits(element)
                | (ulong)(uint)BitConverter.SingleToInt32Bits(element + 1) << 32);
        Assert.Equal(byWords, byMatrix);
    }

    [Fact]
    public void OneFloatStepInAnyMatrixElementChangesTheSignature()
    {
        // For a fixed word the round is a bijection of the running value, and for a fixed running value it is
        // injective in the word, so two sequences that differ in one word can never collide. Pinned per element.
        Matrix4x4 m = Matrix4x4.CreateRotationY(0.3f) * Matrix4x4.CreateTranslation(12.5f, 1f, -40f);
        ulong reference = 0;
        Scene3D.MixPointSignature(ref reference, m);
        for (int e = 0; e < 16; e++)
        {
            Matrix4x4 moved = m;
            moved[e / 4, e % 4] = MathF.BitIncrement(moved[e / 4, e % 4]);
            ulong changed = 0;
            Scene3D.MixPointSignature(ref changed, moved);
            Assert.NotEqual(reference, changed);
        }
    }
}
