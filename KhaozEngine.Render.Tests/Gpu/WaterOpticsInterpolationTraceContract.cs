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
        Instruction add = HelperOutput(outer.Args[1], 0);
        Instruction mul = HelperOutput(inner.Args[1], 1);
        Instruction div = HelperOutput(inner.Args[2], 2);
        PhiEdges(nestedResult, (mul.Args[1], mul.Label), (div.Args[1], div.Label));
        PhiEdges(rounded, (add.Args[1], add.Label), (nestedResult.Args[1], innerMerge));

        Instruction HelperOutput(uint entry, int kind)
        {
            uint exit = Assert.Single(code, i => i.Label == entry && i.Op == 247).Args[0];
            Instruction wrapper = Assert.Single(code, i => i.Label == entry && i.Op == 251);
            Assert.Equal(2, wrapper.Args.Length); // Inlined helper with structured early returns.
            Assert.Equal(0u, constants[wrapper.Args[0]]);
            var pending = new Stack<uint>();
            var region = new HashSet<uint>();
            pending.Push(entry);
            while (pending.TryPop(out uint current))
            {
                if (current == exit || !region.Add(current)) continue;
                foreach (uint target in code.Where(i => i.Label == current).SelectMany(Targets))
                    pending.Push(target);
            }
            Instruction[] helper = code.Where(i => region.Contains(i.Label)).ToArray();
            Instruction output = Assert.Single(code, i => i.Label == exit && i.Op == 245);
            Assert.Equal(left.Args[0], output.Args[0]);

            // Identify the actual finite bit-pack independently of the returned phi's inputs.
            // Numerical oracles cover helper math. This gate covers the path from its computed
            // packed word, overflow and legal early returns to the shared rounded result.
            Instruction packed = Assert.Single(helper, i => i.Op == 197
                && Find(i.Args[2], 197) is { } high
                && Find(high.Args[3], 196) is { } shift && Literal(shift.Args[3], 23)
                && Find(i.Args[3], 199) is { } fraction && Literal(fraction.Args[3], 0x7fffff));
            Instruction highBits = Definition(packed.Args[2], 197);
            uint sign = highBits.Args[2];
            Instruction shifted = Definition(highBits.Args[3], 196);
            Instruction exponent = Definition(Definition(shifted.Args[2], 124).Args[2], 169);
            Instruction fractionBits = Definition(packed.Args[3], 199);
            uint significand = fractionBits.Args[2];
            Instruction subnormal = Definition(exponent.Args[2], 176);
            Assert.Equal(significand, subnormal.Args[2]);
            Assert.True(Literal(subnormal.Args[3], 0x800000));
            Assert.True(Literal(exponent.Args[3], 0));

            uint roundExit = Assert.Single(code, i => i.Label == packed.Label && i.Op == 249).Args[0];
            Instruction overflow = Assert.Single(helper, i => i.Op == 197
                && i.Args[2] == sign && Literal(i.Args[3], 0x7f800000)
                && code.Any(branch => branch.Op == 249 && branch.Label == i.Label && branch.Args[0] == roundExit));
            Instruction normal = Assert.Single(code, i => i.Label == roundExit && i.Op == 245);
            Assert.Equal(output.Args[0], normal.Args[0]);
            PhiEdges(normal, (packed.Args[1], packed.Label), (overflow.Args[1], overflow.Label));
            Instruction overflowCondition = Assert.Single(helper, i => i.Op == 175
                && i.Args[2] == exponent.Args[4] && Literal(i.Args[3], 255));
            Instruction overflowBranch = Conditional(overflowCondition);
            Assert.Equal(overflow.Label, overflowBranch.Args[1]);
            Assert.Equal(packed.Label, overflowBranch.Args[2]);

            Instruction leftZero = Zero(left.Args[1]);
            Instruction rightZero = Zero(right.Args[1]);
            var returns = new List<(uint Value, uint Predecessor)> { (normal.Args[1], normal.Label) };
            if (kind == 0)
            {
                Instruction bothZero = Assert.Single(helper, i => i.Op == 167
                    && i.Args[2] == leftZero.Args[1] && i.Args[3] == rightZero.Args[1]);
                Instruction bothSign = Mask(Assert.Single(helper, i => i.Op == 199
                    && i.Args[2] == left.Args[1] && i.Args[3] == right.Args[1]).Args[1], 0x80000000);
                returns.Add((bothSign.Args[1], Conditional(bothZero).Args[1]));
                returns.Add((right.Args[1], Conditional(leftZero).Args[1]));
                returns.Add((left.Args[1], Conditional(rightZero).Args[1]));
                Instruction cancelled = Assert.Single(helper, i => i.Op == 170
                    && Find(i.Args[2], 130) is not null && Literal(i.Args[3], 0));
                uint zero = Assert.Single(code, i => i.Op == 43 && i.Args[0] == output.Args[0]
                    && i.Args[2] == 0).Args[1];
                returns.Add((zero, Conditional(cancelled).Args[1]));
            }
            else
            {
                Instruction signMask = Mask(Assert.Single(helper, i => i.Op == 198
                    && i.Args[2] == left.Args[1] && i.Args[3] == right.Args[1]).Args[1], 0x80000000);
                Assert.Equal(signMask.Args[1], sign);
                if (kind == 1)
                {
                    Instruction eitherZero = Assert.Single(helper, i => i.Op == 245
                        && i.Args[0] == leftZero.Args[0] && i.Args.Length == 6
                        && new[] { i.Args[2], i.Args[4] }.Order().SequenceEqual(
                            new[] { leftZero.Args[1], rightZero.Args[1] }.Order()));
                    PhiEdges(eitherZero, (leftZero.Args[1], leftZero.Label), (rightZero.Args[1], rightZero.Label));
                    returns.Add((sign, Conditional(eitherZero).Args[1]));
                }
                else
                {
                    Instruction infinity = Assert.Single(helper, i => i.Op == 197
                        && i.Args[2] == sign && Literal(i.Args[3], 0x7f800000) && i != overflow);
                    returns.Add((infinity.Args[1], Conditional(rightZero).Args[1]));
                    returns.Add((sign, Conditional(leftZero).Args[1]));
                }
            }
            PhiEdges(output, returns.ToArray());
            return output;

            Instruction Mask(uint value, uint mask) => Assert.Single(helper, i => i.Op == 199
                && i.Args[2] == value && Literal(i.Args[3], mask));
            Instruction Zero(uint value)
            {
                uint magnitude = Mask(value, 0x7fffffff).Args[1];
                return Assert.Single(helper, i => i.Op == 170 && i.Args[2] == magnitude && Literal(i.Args[3], 0));
            }
        }

        void PhiEdges(Instruction phi, params (uint Value, uint Predecessor)[] expected)
        {
            Assert.Equal(2 + expected.Length * 2, phi.Args.Length);
            var actual = Enumerable.Range(0, expected.Length)
                .Select(n => (Value: phi.Args[2 + 2 * n], Predecessor: phi.Args[3 + 2 * n])).ToArray();
            Assert.Equal(expected.Order(), actual.Order());
            Assert.Equal(code.Where(i => Targets(i).Contains(phi.Label)).Select(i => i.Label).Order(),
                actual.Select(p => p.Predecessor).Order());
        }

        static IEnumerable<uint> Targets(Instruction i) => i.Op switch
        {
            249 => i.Args,
            250 => i.Args.Skip(1).Take(2),
            251 => i.Args.Skip(1).Where((_, n) => n % 2 == 0),
            _ => []
        };
        bool Literal(uint id, uint value) => constants.TryGetValue(id, out uint literal) && literal == value;
        Instruction? Find(uint id, uint opcode) => code.SingleOrDefault(i => i.Op == opcode && i.Args[1] == id);
        Instruction Conditional(Instruction condition) => Assert.Single(code,
            i => i.Op == 250 && i.Args[0] == condition.Args[1]);

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
