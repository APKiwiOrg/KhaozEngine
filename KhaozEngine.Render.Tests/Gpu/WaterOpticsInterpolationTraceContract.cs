using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

// Pin the ordered operands in actual SSA, independently of the source schedule text.
internal static class WaterOpticsInterpolationTraceContract
{
    internal static void Verify(byte[] module, uint[] selector, Dictionary<int, uint> slots,
        uint destination, uint result)
    {
        uint[] words = MemoryMarshal.Cast<byte, uint>(module).ToArray();
        var code = new List<Instruction>();
        uint label = 0;
        for (int offset = 5; offset < words.Length;)
        {
            int count = checked((int)(words[offset] >> 16));
            uint op = words[offset] & 65535u;
            uint[] args = words[(offset + 1)..(offset + count)];
            if (op == 248) label = args[0];
            code.Add(new(op, args, label));
            offset += count;
        }
        var constants = code.Where(i => i.Op == 43 && i.Args.Length == 3)
            .ToDictionary(i => i.Args[1], i => i.Args[2]);
        Instruction dispatch = Assert.Single(code, i => i.Op == 251 && i.Args.SequenceEqual(selector));
        uint merge = Assert.Single(code, i => i.Label == dispatch.Label && i.Op == 247).Args[0];
        Instruction[] phis = code.Where(i => i.Label == merge && i.Op == 245).ToArray();
        Assert.Equal(4, phis.Length); // Exactly destination, operation, left and right.
        Assert.All(phis, i => Assert.Equal(50, i.Args.Length)); // 23 steps and default.
        Instruction dest = Definition(destination, 245);
        Assert.Contains(dest, phis);
        Instruction operation = Assert.Single(phis, i => i.Args[0] == dest.Args[0] && i != dest);
        Instruction[] operands = phis.Where(i => i.Args[0] != dest.Args[0]).ToArray();
        Assert.Equal(2, operands.Length);
        uint[] cases = selector.Skip(3).Where((_, i) => i % 2 == 0).ToArray();
        Dictionary<uint, uint> Incoming(Instruction phi) => Enumerable.Range(0, 24)
            .ToDictionary(i => phi.Args[3 + 2 * i], i => phi.Args[2 + 2 * i]);
        var destinations = Incoming(dest);
        var operations = Incoming(operation);
        Instruction left = Assert.Single(operands, phi => Incoming(phi)[cases[6]] == slots[0]);
        Instruction right = Assert.Single(operands, phi => phi != left);
        var lefts = Incoming(left);
        var rights = Incoming(right);
        Assert.All(phis, phi => Assert.Equal(cases.Append(dispatch.Label).Order(), Incoming(phi).Keys.Order()));
        Assert.Equal(0u, constants[lefts[dispatch.Label]]);
        Assert.Equal(0u, constants[rights[dispatch.Label]]);
        Assert.Equal(0u, constants[operations[dispatch.Label]]);
        uint stepAsInt = destinations[dispatch.Label];
        Assert.Equal(selector[0], Definition(stepAsInt, 124).Args[2]); // uint step bitcast to int.

        uint ax = Definition(rights[cases[0]], 198).Args[2];
        var origin = Atlas(ax);
        uint point = Definition(lefts[cases[4]], 81).Args[2];
        Assert.Equal(124u, Assert.Single(code, i => i.Op == 124 && i.Args[1] == point).Op);
        (int Operation, int Destination, string Left, string Right)[] trace =
        [
            (0, 0, "bx", "ax^"), (0, 1, "bz", "az^"),
            (0, 2, "cx", "ax^"), (0, 3, "cz", "az^"),
            (0, 4, "px", "ax^"), (0, 5, "pz", "az^"),
            (1, 6, "r0", "r3"), (1, 7, "r1", "r2"),
            (0, 8, "r6", "r7^"), (1, 6, "r4", "r3"),
            (1, 7, "r5", "r2"), (0, 9, "r6", "r7^"),
            (1, 6, "r0", "r5"), (1, 7, "r1", "r4"),
            (0, 10, "r6", "r7^"), (2, 11, "r9", "r8"),
            (2, 12, "r10", "r8"), (0, 13, "by", "ay^"),
            (0, 14, "cy", "ay^"), (1, 6, "r11", "r13"),
            (0, 15, "ay", "r6"), (1, 6, "r12", "r14"),
            (0, 15, "r15", "r6")
        ];
        var produced = new HashSet<int>();
        for (int step = 0; step < trace.Length; step++)
        {
            var expected = trace[step];
            uint route = cases[step];
            Assert.Equal((uint)expected.Operation, constants[operations[route]]);
            if (step < 6) Assert.Equal(stepAsInt, destinations[route]);
            else Assert.Equal((uint)expected.Destination, constants[destinations[route]]);
            Operand(lefts[route], expected.Left);
            Operand(rights[route], expected.Right);
            produced.Add(expected.Destination);
        }
        Assert.Equal(new[] { 13, 8, 2 }, Enumerable.Range(0, 3).Select(op => trace.Count(t => t.Operation == op)));

        // The selected operation must control both real arithmetic branches and their result merges.
        Instruction outer = Branch(merge, operation.Args[1], 0);
        Instruction inner = Branch(outer.Args[2], operation.Args[1], 1);
        uint outerMerge = Assert.Single(code, i => i.Label == merge && i.Op == 247).Args[0];
        uint innerMerge = Assert.Single(code, i => i.Label == inner.Label && i.Op == 247).Args[0];
        Instruction rounded = Definition(result, 245);
        Assert.Equal(outerMerge, rounded.Label);
        Assert.Equal(6, rounded.Args.Length);
        uint nested = Assert.Single(Enumerable.Range(0, 2), i => rounded.Args[3 + 2 * i] == innerMerge) == 0
            ? rounded.Args[2] : rounded.Args[4];
        Instruction nestedResult = Definition(nested, 245);
        Assert.Equal(innerMerge, nestedResult.Label);
        Assert.Equal(6, nestedResult.Args.Length);
        Assert.NotEqual(nestedResult.Args[2], nestedResult.Args[4]);
        foreach (uint value in new[] { left.Args[1], right.Args[1] })
            foreach (uint branch in new[] { outer.Args[1], inner.Args[1], inner.Args[2] })
                Assert.True(ReadsBeforeMerge(branch, branch == outer.Args[1] ? outerMerge : innerMerge, value));

        bool ReadsBeforeMerge(uint entry, uint stop, uint value)
        {
            var pending = new Stack<uint>();
            var visited = new HashSet<uint>();
            pending.Push(entry);
            while (pending.TryPop(out uint current))
            {
                if (current == stop || !visited.Add(current)) continue;
                foreach (Instruction instruction in code.Where(i => i.Label == current))
                {
                    if (instruction.Op is not (245 or 247 or 248 or 249 or 250 or 251)
                        && instruction.Args.Skip(2).Contains(value)) return true;
                    if (instruction.Op == 249) pending.Push(instruction.Args[0]);
                    if (instruction.Op == 250)
                    {
                        pending.Push(instruction.Args[1]);
                        pending.Push(instruction.Args[2]);
                    }
                    if (instruction.Op == 251)
                    {
                        pending.Push(instruction.Args[1]);
                        foreach (uint target in instruction.Args.Skip(3).Where((_, i) => i % 2 == 0))
                            pending.Push(target);
                    }
                }
            }
            return false;
        }

        Instruction Definition(uint id, uint opcode) => Assert.Single(code,
            i => i.Op == opcode && i.Args[1] == id);

        Instruction Branch(uint block, uint op, uint literal)
        {
            Instruction condition = Assert.Single(code, i => i.Label == block && i.Op == 170
                && i.Args[2] == op && constants.GetValueOrDefault(i.Args[3], uint.MaxValue) == literal);
            return Assert.Single(code, i => i.Label == block && i.Op == 250 && i.Args[0] == condition.Args[1]);
        }

        (uint Address, uint Width, uint Image, uint Sampler) Atlas(uint id)
        {
            uint scalar = Definition(id, 124).Args[2];
            Instruction component = Definition(scalar, 81);
            Assert.Equal(0u, component.Args[3]);
            Instruction fetch = Definition(component.Args[2], 95);
            Assert.Equal(0u, constants[fetch.Args[5]]); // Literal LOD zero.
            Instruction image = Definition(fetch.Args[2], 100);
            Instruction sampled = Definition(image.Args[2], 86);
            Instruction coordinates = Definition(fetch.Args[3], 80);
            Instruction x = Definition(coordinates.Args[2], 139);
            Instruction y = Definition(coordinates.Args[3], 135);
            Assert.Equal(x.Args.Skip(2), y.Args.Skip(2)); // address % width, address / width.
            return (x.Args[2], x.Args[3], sampled.Args[2], sampled.Args[3]);
        }

        void Operand(uint id, string expression)
        {
            if (expression.EndsWith('^'))
            {
                Instruction xor = Definition(id, 198);
                Assert.Equal(0x80000000u, constants[xor.Args[3]]);
                id = xor.Args[2];
                expression = expression[..^1];
            }
            if (expression[0] == 'r')
            {
                int slot = int.Parse(expression.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture);
                Assert.Contains(slot, produced); // Each read follows its producing write in the 23-step trace.
                Assert.Equal(slots[slot], id);
            }
            else if (expression[0] == 'p')
            {
                Instruction component = Definition(id, 81);
                Assert.Equal(point, component.Args[2]);
                Assert.Equal(expression[1] == 'x' ? 0u : 1u, component.Args[3]);
            }
            else
            {
                var atlas = Atlas(id);
                Assert.Equal((origin.Width, origin.Image, origin.Sampler), (atlas.Width, atlas.Image, atlas.Sampler));
                uint offset = (uint)((expression[0] - 'a') * 3 + (expression[1] switch { 'x' => 0, 'z' => 1, _ => 2 }));
                if (offset == 0) Assert.Equal(origin.Address, atlas.Address);
                else
                {
                    Instruction address = Definition(atlas.Address, 128);
                    Assert.Equal(origin.Address, address.Args[2]);
                    Assert.Equal(offset, constants[address.Args[3]]);
                }
            }
        }
    }

    sealed record Instruction(uint Op, uint[] Args, uint Label);
}
