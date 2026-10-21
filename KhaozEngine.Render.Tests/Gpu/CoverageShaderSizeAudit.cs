using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

// Count the call tree, not only shared definitions. FXC inlines every HLSL call.
internal static class CoverageShaderSizeAudit
{
    internal readonly record struct Sizes(int Instructions, int InlineInstructions, long InlineBound, int HlslBytes, int MslBytes);
    sealed class Function
    {
        internal int Instructions, Returns;
        internal readonly List<uint> Calls = new();
    }

    internal static Sizes Validate(byte[] spirv, string hlsl, string msl, string label)
    {
        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(spirv);
        var functions = new Dictionary<uint, Function>();
        var names = new Dictionary<uint, string>();
        uint entry = 0;
        Function? current = null;
        int instructions = 0, globals = 0;
        for (int i = 5; i < words.Length;)
        {
            int length = (int)(words[i] >> 16);
            uint op = words[i] & 65535u;
            Assert.True(length > 0 && i + length <= words.Length);
            instructions++;
            if (op == 5) names[words[i + 1]] = Encoding.UTF8.GetString(MemoryMarshal.AsBytes(words.Slice(i + 2, length - 2))).TrimEnd('\0');
            if (op == 15) entry = words[i + 2];
            if (op == 54) functions.Add(words[i + 2], current = new());
            if (current == null) globals++;
            else
            {
                current.Instructions++;
                if (op is 253 or 254) current.Returns++;
                if (op == 57) current.Calls.Add(words[i + 3]);
                if (op == 246) Assert.True((words[i + 3] & 2u) != 0u, $"{label}: C8 requires DontUnroll on every worker/traversal loop");
            }
            if (op == 56) current = null;
            i += length;
        }
        // Retain every function, parameter and call instruction, even though inlining
        // removes them. Single-return helpers need no return merge. Reserve eight
        // instructions per additional return for the structured return flag/value,
        // merge and branch. No unrolling is allowed. The proof records comparison
        // with SPIRV-Tools' actual exhaustive pass for every physical pipeline.
        long Bound(uint id)
        {
            Function f = functions[id];
            return f.Instructions + 8L * Math.Max(0, f.Returns - 1) + f.Calls.Sum(Bound);
        }
        int Sites(uint id, string helper) => functions[id].Calls.Sum(c =>
            (names.GetValueOrDefault(c, "").StartsWith(helper + "(", StringComparison.Ordinal) ? 1 : 0) + Sites(c, helper));
        foreach (string helper in new[] { "covExactWorker", "covPredicateProduct", "covExactAdd", "covRound32", "covRoundOperation", "covCorrectPositive", "covRoundedAtOrBelow", "covResidualProduct" })
        {
            int expected = functions.Keys.Any(id => names.GetValueOrDefault(id, "").StartsWith(helper + "(", StringComparison.Ordinal)) ? 1 : 0;
            Assert.True(Sites(entry, helper) == expected, $"{label}: {helper} has {Sites(entry, helper)} expanded call sites, expected {expected}");
        }
        foreach (uint id in functions.Keys.Where(id => new[] { "covPredicateProduct", "covResidualProduct", "covExactProduct" }
            .Any(helper => names.GetValueOrDefault(id, "").StartsWith(helper + "(", StringComparison.Ordinal))))
            Assert.True(Sites(id, "covExactMul32") == 1, $"{label}: {names[id]} must have one word-product site");
        if (label != CoverageGpuExactWordsProbe.Name)
            Assert.Equal(0, Sites(entry, "covExactProduct"));
        long bound = globals + Bound(entry);
        int hlslBytes = Encoding.UTF8.GetByteCount(hlsl.ReplaceLineEndings("\n"));
        int mslBytes = Encoding.UTF8.GetByteCount(msl.ReplaceLineEndings("\n"));
        Assert.True(bound <= 30_000, $"{label}: exhaustive inline bound {bound} exceeds 30000");
        Assert.True(hlslBytes <= 100_000, $"{label}: HLSL {hlslBytes} exceeds 100000 bytes");
        Assert.True(mslBytes <= 100_000, $"{label}: MSL {mslBytes} exceeds 100000 bytes");
        int inlined = CoverageShaderSizeManifest.InlineInstructions(spirv, label, hlslBytes, mslBytes);
        Assert.InRange(inlined, 1, 30_000);
        Assert.True(inlined <= bound, $"{label}: exhaustive pass exceeded the structural bound");
        CoverageSpirvAccessAudit.Validate(spirv, label, constantPrivateArrays: true);
        return new(instructions, inlined, bound, hlslBytes, mslBytes);
    }
}
