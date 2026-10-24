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
        // Keep the existing uint schedule alongside the defined scalar entry values.
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
        Assert.DoesNotContain(switches, i => i.Args.Length > 2 && i != selector);
        var slots = new HashSet<int>();
        var results = new HashSet<uint>();
        var destinations = new HashSet<uint>();
        var scalarSlots = new Dictionary<int, uint>();
        int loopStart = hlsl.IndexOf("for (" + header + ")", StringComparison.Ordinal);
        Assert.InRange(loopStart, 0, hlsl.Length - 1);
        string initialState = hlsl[..loopStart] + header[..header.IndexOf(';')] + ";";
        string declarations = @"\buint\s+(?:_\d+(?:\s*=\s*\w+)?\s*,\s*)*";
        foreach (Instruction register in registers)
        {
            Assert.Equal(6, register.Args.Length);
            Assert.Equal(loop.Args[1], register.Args[5]); // Scalar comes from the continuation block.
            // Defined incoming state avoids copying undef values before a slot's first arithmetic write.
            Assert.Contains(code, i => i.Op == 43 && i.Args[0] == uintType
                && i.Args[1] == register.Args[2] && i.Args[2] == 0); // OpConstant uint zero, never OpUndef.
            Instruction next = Assert.Single(code, i => i.Op == 169 && i.Args[1] == register.Args[4]); // OpSelect.
            Assert.Equal(uintType, next.Args[0]);
            Assert.Equal(5, next.Args.Length);
            Assert.Equal(register.Args[1], next.Args[4]); // False preserves this slot, never another one.
            Instruction condition = Assert.Single(code, i => i.Op == 170 && i.Args[1] == next.Args[2]); // OpIEqual.
            Assert.True(constants.TryGetValue(condition.Args[3], out uint literal));
            Assert.InRange(literal, 0u, 15u);
            int slot = checked((int)literal);
            Assert.True(slots.Add(slot), "Two destination selects clobber the same scalar register.");
            scalarSlots.Add(slot, register.Args[1]);
            destinations.Add(condition.Args[2]);
            results.Add(next.Args[3]);
            // Zero entries may use a separate assignment or share the for initializer's uint declaration.
            Assert.Matches(declarations + "_" + register.Args[1] + @"(?:\s*=\s*\w+)?\s*[,;]", initialState);
            Assert.Matches(@"\b_" + register.Args[1] + @"\s*=\s*0u\s*[,;]", initialState);
            Assert.Matches(declarations + "_" + next.Args[1] + @"(?:\s*=\s*\w+)?\s*[,;]", hlsl);
            Assert.Matches(@"\b_" + next.Args[1] + @" = \(_" + condition.Args[2] + " == "
                + slot + @"\) \? _" + next.Args[3] + " : _" + register.Args[1] + ";", body);
            Assert.Contains("_" + register.Args[1] + " = _" + next.Args[1], header, StringComparison.Ordinal);
            Assert.Contains(code.Skip(start).Take(end - start), i => i.Op is 245 or 198
                && i.Args[1] != register.Args[1] && i.Args[1] != next.Args[1]
                && i.Args.Skip(2).Where((_, n) => i.Op == 198 || n % 2 == 0).Contains(register.Args[1]));
        }
        Assert.Equal(Enumerable.Range(0, 16), slots.Order());
        uint result = Assert.Single(results); // Every slot receives the same rounded arithmetic result.
        Assert.Contains(code, i => i.Op == 245 && i.Args[0] == uintType && i.Args[1] == result);
        uint destination = Assert.Single(destinations);
        WaterOpticsInterpolationTraceContract.Verify(module, selector.Args, scalarSlots, destination, result);
        Assert.Equal(16, Regex.Matches(header, @"_\d+ = _\d+").Count);
        string index = "_" + counter.Args[1];
        Assert.Matches(@"^uint (?:_\d+ = 0u, )*" + index + @" = 0u(?:, _\d+ = 0u)*; "
            + index + " < 23u; (?:_\\d+ = _\\d+, ){16}"
            + index + @"\+\+$", header);
    }

    internal static void VerifyRejectsCorruptedRoutes(byte[] module, string hlsl, string body, string header)
    {
        uint[] words = MemoryMarshal.Cast<byte, uint>(module).ToArray();
        var instructions = new List<(int Offset, uint Op, uint[] Args)>();
        for (int offset = 5; offset < words.Length;)
        {
            int count = checked((int)(words[offset] >> 16));
            instructions.Add((offset, words[offset] & 65535u, words[(offset + 1)..(offset + count)]));
            offset += count;
        }
        uint[] backedges = Regex.Matches(header, @"_\d+ = _(\d+)")
            .Select(m => uint.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var selects = instructions.Where(i => i.Op == 169 && backedges.Contains(i.Args[1])).ToArray();
        Assert.Equal(16, selects.Length);
        for (int slot = 0; slot < selects.Length; slot++)
        {
            var current = selects[slot];
            var other = selects[(slot + 1) % selects.Length];
            // All mutations remain well-typed and alter the actual emitted route, never a mock.
            foreach ((int operand, uint replacement) in new[]
            {
                (3, other.Args[2]), // A different slot's destination condition.
                (4, current.Args[4]), // Old state instead of the rounded result.
                (5, other.Args[4]) // Another slot's old state on the false edge.
            })
            {
                uint[] corrupted = (uint[])words.Clone();
                corrupted[current.Offset + operand] = replacement;
                Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Verify(
                    MemoryMarshal.AsBytes(corrupted.AsSpan()).ToArray(), hlsl, body, header));
            }
        }
    }

    sealed record Instruction(uint Op, uint[] Args, uint Label);
}
