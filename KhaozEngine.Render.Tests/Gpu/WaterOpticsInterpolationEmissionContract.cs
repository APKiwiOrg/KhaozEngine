using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

// Follow actual scalar backedges and destination routes after the pinned optimizer has run.
internal static class WaterOpticsInterpolationEmissionContract
{
    internal static void Verify(byte[] module, string hlsl, string body, string header)
    {
        uint[] words = MemoryMarshal.Cast<byte, uint>(module).ToArray();
        var code = new List<Instruction>();
        uint label = 0;
        for (int i = 5; i < words.Length;)
        {
            int size = checked((int)(words[i] >> 16));
            Assert.InRange(size, 1, words.Length - i);
            uint op = words[i] & 65535u;
            uint[] operands = words[(i + 1)..(i + size)];
            if (op == 248) label = operands[0]; // OpLabel.
            code.Add(new(op, operands, label));
            i += size;
        }
        var constants = code.Where(i => i.Op == 43 && i.Args.Length == 3)
            .ToDictionary(i => i.Args[1], i => i.Args[2]);
        uint uintType = Assert.Single(code, i => i.Op == 21 && i.Args[1] == 32 && i.Args[2] == 0).Args[0];
        Assert.DoesNotContain(code, i => i.Op == 28 && constants[i.Args[2]] is 16 or 18);
        Assert.DoesNotMatch(@"\buint\s+\w+\[16\]", hlsl);
        Assert.DoesNotMatch(@"\b\w+\s*\[[^\]]+\]", body); // No interpolation array addresses, even literal ones.

        Instruction comparison = Assert.Single(code, i => i.Op is 176 or 177
            && constants.GetValueOrDefault(i.Args[3]) == 23); // OpULessThan or OpSLessThan.
        Instruction[] block = code.Where(i => i.Label == comparison.Label).ToArray();
        Instruction loop = Assert.Single(block, i => i.Op == 246); // OpLoopMerge.
        Assert.Equal(2u, loop.Args[2]); // DontUnroll.
        Instruction counter = Assert.Single(block, i => i.Op == 245 && i.Args[1] == comparison.Args[2]);
        // A signed counter mixed with uint scalar assignments in the HLSL comma continuation
        // reached FXC's l-value cast diagnostic. Keep all continuation expressions unsigned.
        Assert.Equal(uintType, counter.Args[0]);
        Assert.Equal(176u, comparison.Op); // OpULessThan.
        Assert.Equal(0u, constants[counter.Args[2]]);
        Assert.Equal(uintType, Assert.Single(code, i => i.Op == 43 && i.Args[1] == counter.Args[2]).Args[0]);
        Assert.Equal(uintType, Assert.Single(code, i => i.Op == 43 && i.Args[1] == comparison.Args[3]).Args[0]);
        Instruction increment = Assert.Single(code, i => i.Op == 128 && i.Args[1] == counter.Args[4]); // OpIAdd.
        Assert.Equal(uintType, increment.Args[0]);
        Assert.Equal(counter.Args[1], increment.Args[2]);
        Assert.Equal(1u, constants[increment.Args[3]]);
        uint oneType = Assert.Single(code, i => i.Op == 43 && i.Args[1] == increment.Args[3]).Args[0];
        // OpIAdd permits different signedness at the same width. Its result controls HLSL's ++ type.
        Assert.Contains(code, i => i.Op == 21 && i.Args[0] == oneType && i.Args[1] == 32);
        Assert.Equal(loop.Args[1], increment.Label);
        Instruction[] registers = block.Where(i => i.Op == 245 && i.Args[0] == uintType
            && i.Args[1] != counter.Args[1]).ToArray();
        Assert.Equal(16, registers.Length);
        int start = code.IndexOf(block[0]);
        int end = code.FindIndex(start, i => i.Op == 248 && i.Label == loop.Args[0]);
        Assert.True(end > start);
        Instruction[] switches = code.Skip(start).Take(end - start).Where(i => i.Op == 251).ToArray();
        Instruction selector = Assert.Single(switches, i => i.Args.Length == 48); // All 23 original steps.
        Assert.Equal(counter.Args[1], selector.Args[0]);
        Assert.Equal(Enumerable.Range(0, 23).Select(i => (uint)i), selector.Args.Skip(2).Where((_, i) => i % 2 == 0));
        Instruction destination = Assert.Single(switches, i => i.Args.Length == 34);
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (uint)i), destination.Args.Skip(2).Where((_, i) => i % 2 == 0));
        uint[] cases = destination.Args.Skip(3).Where((_, i) => i % 2 == 0).ToArray();
        var slots = new HashSet<int>();
        var results = new HashSet<uint>();
        foreach (Instruction register in registers)
        {
            Assert.Equal(6, register.Args.Length);
            Assert.Equal(loop.Args[1], register.Args[5]); // Scalar comes from the continuation block.
            Assert.Contains(code, i => i.Op == 1 && i.Args[0] == uintType && i.Args[1] == register.Args[2]); // OpUndef.
            Instruction next = Assert.Single(code, i => i.Op == 245 && i.Args[1] == register.Args[4]);
            Assert.Equal(uintType, next.Args[0]);
            Assert.Equal(36, next.Args.Length); // Sixteen cases plus the unchanged default route.
            Assert.Equal(next.Label, destination.Args[1]);
            var incoming = Enumerable.Range(0, 17).ToDictionary(i => next.Args[3 + 2 * i], i => next.Args[2 + 2 * i]);
            Assert.Equal(cases.Append(destination.Label).Order(), incoming.Keys.Order());
            Assert.Equal(register.Args[1], incoming[destination.Label]);
            int slot = Assert.Single(Enumerable.Range(0, 16), i => incoming[cases[i]] != register.Args[1]);
            Assert.True(slots.Add(slot), "Two destination cases clobber the same scalar register.");
            results.Add(incoming[cases[slot]]);
            Assert.All(Enumerable.Range(0, 16).Where(i => i != slot), i =>
                Assert.Equal(register.Args[1], incoming[cases[i]]));
            // The HLSL compiler input carries exactly these same independently declared scalar values.
            Assert.Matches(@"\buint _" + register.Args[1] + @";", hlsl);
            Assert.Matches(@"\buint _" + next.Args[1] + @";", hlsl);
            Assert.Contains("_" + register.Args[1] + " = _" + next.Args[1], header, StringComparison.Ordinal);
            Assert.Contains(code.Skip(start).Take(end - start), i => i.Op is 245 or 198
                && i.Args[1] != register.Args[1] && i.Args[1] != next.Args[1]
                && i.Args.Skip(2).Where((_, n) => i.Op == 198 || n % 2 == 0).Contains(register.Args[1]));
        }
        Assert.Equal(Enumerable.Range(0, 16), slots.Order());
        uint result = Assert.Single(results); // Every slot receives the same rounded arithmetic result.
        Assert.Contains(code, i => i.Op == 245 && i.Args[0] == uintType && i.Args[1] == result);
        Assert.Equal(16, Regex.Matches(header, @"_\d+ = _\d+").Count);
        string index = "_" + counter.Args[1];
        Assert.Matches(@"^uint " + index + " = 0u; " + index + " < 23u; (?:_\\d+ = _\\d+, ){16}"
            + index + @"\+\+$", header);
    }

    sealed record Instruction(uint Op, uint[] Args, uint Label);
}
