using System;
using System.Linq;
using System.Text.RegularExpressions;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>The positive-zero shortcut is exact, including finite overflow, signed zeros and non-finite inputs.
/// The production twin deliberately retains the original ordered arithmetic.</summary>
public sealed class TemporalLockZeroBypassTests
{
    [Fact]
    public void TheReleaseCallIsOnlyInTheQualifiedFallback()
    {
        string call = TemporalLockZeroBypass.CallSite;
        Assert.Contains("float releaseEdgeMotion = ownReprojected && useHistory ? 0.0 : edgeMotion;", call,
            StringComparison.Ordinal);
        Assert.Matches(@"if \(floatBitsToUint\(lockValue\) != 0u\s*\|\|", call);
        foreach (string argument in new[] { "motionPixels", "reactive", "releaseEdgeMotion" })
            Assert.Contains($"(floatBitsToUint({argument}) & 0x7f800000u) == 0x7f800000u", call,
                StringComparison.Ordinal);
        Assert.Matches(@"\) \{\s*lockValue = temporalReleaseLock\(lockValue, motionPixels, reactive, releaseEdgeMotion\);\s*\}", call);
        Assert.Single(Regex.Matches(call, @"temporalReleaseLock\("));
        // No assignment on the bypass branch. Its incoming +0 bits survive untouched.
        Assert.Equal(2, Regex.Matches(call, @"\b(?:releaseEdgeMotion|lockValue) = ").Count);
    }

    [Fact]
    public void EveryCompiledReleaseKeepsAllTwelveBarriersInsideOneFallbackBranch()
    {
        foreach (var program in ShippedShaderPrograms.GraphicsPrograms()
            .Where(p => p.FragmentGlsl.Contains(ShaderSources.TemporalLockReleaseGlsl, StringComparison.Ordinal)))
        {
            var instructions = TemporalLockReleaseSpirv.Instructions(
                TemporalLockReleaseSpirv.Fragment(program.FragmentGlsl)).ToArray();
            var thirtyOne = instructions.Where(i => i.Op == 43 && i.Words.Length == 4 && i.Words[3] == 31)
                .Select(i => i.Words[2]).ToHashSet();
            var shifts = instructions.Where(i => i.Op == 194 && thirtyOne.Contains(i.Words[4]))
                .Select(i => i.Words[2]).ToHashSet();
            int[] barriers = Enumerable.Range(0, instructions.Length).Where(n => instructions[n].Op == 197
                && (shifts.Contains(instructions[n].Words[3]) || shifts.Contains(instructions[n].Words[4]))).ToArray();
            Assert.Equal(12, barriers.Length);
            int label = Array.FindLastIndex(instructions, barriers[0], i => i.Op == 248);
            int nextLabel = Array.FindIndex(instructions, label + 1, i => i.Op == 248);
            Assert.True(barriers.All(n => n > label && n < nextLabel), program.Name + " barriers escaped their block");
            Assert.Equal(247, instructions[label - 2].Op); // OpSelectionMerge
            Assert.Equal(250, instructions[label - 1].Op); // OpBranchConditional
            uint fallback = instructions[label].Words[1], merge = instructions[label - 2].Words[1];
            uint[] branch = instructions[label - 1].Words;
            Assert.True(branch[2] == fallback && branch[3] == merge || branch[3] == fallback && branch[2] == merge,
                program.Name + " does not branch around the entire ordered release");
        }
    }

    [Fact]
    public void BypassAndOriginalTwinAgreeOnEveryExceptionalArgumentCombination()
    {
        float[] values = new uint[]
        {
            0, 0x80000000, 1, 0x80000001, 0x007fffff, 0x807fffff, 0x00800000, 0x80800000,
            0x3effffff, 0x3f000000, 0x3f000001, 0x3f7fffff, 0x3f800000, 0x3f800001,
            0x407fffff, 0x40800000, 0x40800001, 0x7effffff, 0x7f000000, 0x7f000001,
            0x7f7ffffe, 0x7f7fffff, 0xff7fffff, 0x7f800000, 0xff800000,
            0x7f800001, 0xff800001, 0x7fc00000, 0xffc00000, 0x7fffffff, 0xffffffff,
        }.Select(BitConverter.UInt32BitsToSingle).ToArray();
        foreach (float value in values)
            foreach (float motion in values)
                foreach (float reactive in values)
                    foreach (float edge in values)
                        Equal(value, motion, reactive, edge);
    }

    [Fact]
    public void ZeroQualificationCoversEveryExponentAndItsMantissaBoundaries()
    {
        foreach (uint sign in new[] { 0u, 0x80000000u })
            for (uint exponent = 0; exponent < 256; exponent++)
                foreach (uint mantissa in new[] { 0u, 1u, 0x3fffffu, 0x7ffffeu, 0x7fffffu })
                {
                    float argument = BitConverter.UInt32BitsToSingle(sign | exponent << 23 | mantissa);
                    bool finite = exponent != 255;
                    Assert.Equal(finite, TemporalLockZeroBypass.Qualifies(0f, argument, 0.25f, 0.5f));
                    Assert.Equal(finite, TemporalLockZeroBypass.Qualifies(0f, 2f, argument, 0.5f));
                    Assert.Equal(finite, TemporalLockZeroBypass.Qualifies(0f, 2f, 0.25f, argument));
                    Assert.False(TemporalLockZeroBypass.Qualifies(-0f, argument, 0f, 0f));
                    Equal(0f, argument, 0.25f, 0.5f);
                    Equal(0f, 2f, argument, 0.5f);
                    Equal(0f, 2f, 0.25f, argument);
                    Equal(-0f, argument, 0f, 0f);
                }
    }

    static void Equal(float value, float motion, float reactive, float edge)
    {
        float original = TemporalResolveMath.ReleaseLock(value, motion, reactive, edge);
        float bypassed = TemporalLockZeroBypass.Qualifies(value, motion, reactive, edge) ? 0f
            : TemporalLockReleaseProbe.Barrier(value, motion, reactive, edge);
        Assert.Equal(BitConverter.SingleToUInt32Bits(original), BitConverter.SingleToUInt32Bits(bypassed));
    }
}
