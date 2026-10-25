using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

// Corrupt real helper return routes while keeping operand types and matching HLSL assignments.
internal static class WaterOpticsInterpolationResultMutations
{
    internal static void Verify(byte[] module, string hlsl, string body, string header)
    {
        const string evidence = "/tmp/khaozengine-water-triangles/trace-result-fix/mutations";
        Directory.CreateDirectory(evidence);
        uint[] words = MemoryMarshal.Cast<byte, uint>(module).ToArray();
        var code = new List<Instruction>();
        uint label = 0;
        for (int offset = 5; offset < words.Length;)
        {
            int count = checked((int)(words[offset] >> 16));
            uint op = words[offset] & 65535u;
            uint[] args = words[(offset + 1)..(offset + count)];
            if (op == 248) label = args[0];
            code.Add(new(offset, op, args, label));
            offset += count;
        }
        var constants = code.Where(i => i.Op == 43 && i.Args.Length == 3)
            .ToDictionary(i => i.Args[1], i => i.Args[2]);
        Instruction selector = Assert.Single(code, i => i.Op == 251 && i.Args.Length == 48);
        uint operandMerge = Merge(selector.Label);
        Instruction[] operands = code.Where(i => i.Op == 245 && i.Label == operandMerge
            && code.Any(t => t.Op == 21 && t.Args.SequenceEqual(new[] { i.Args[0], 32u, 0u }))).ToArray();
        Assert.Equal(2, operands.Length);
        Instruction left = Assert.Single(operands, i => Definition(Input(i, selector.Args[3])).Op != 198);
        Instruction right = Assert.Single(operands, i => i != left);
        uint[] backedges = Regex.Matches(header, @"_\d+ = _(\d+)")
            .Select(m => uint.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        uint result = Assert.Single(code.Where(i => i.Op == 169 && backedges.Contains(i.Args[1]))
            .Select(i => i.Args[3]).Distinct());
        Instruction rounded = Definition(result);
        Instruction outer = Assert.Single(code, i => i.Label == operandMerge && i.Op == 250);
        Instruction inner = Assert.Single(code, i => i.Label == outer.Args[2] && i.Op == 250);
        Instruction nested = Assert.Single(code, i => i.Op == 245 && i.Label == Merge(inner.Label));
        var outcomes = new List<object>();
        foreach ((string name, uint entry, Instruction parent) in new[]
        {
            ("Add", outer.Args[1], rounded), ("Mul", inner.Args[1], nested), ("Div", inner.Args[2], nested)
        })
        {
            Instruction output = Assert.Single(code, i => i.Op == 245 && i.Label == Merge(entry));
            Instruction normal = Assert.Single(Inputs(output).Select(p => Definition(p.Value)),
                i => i.Op == 245 && Inputs(output).Contains((i.Args[1], i.Label)));
            int packedIndex = Assert.Single(Enumerable.Range(0, (normal.Args.Length - 2) / 2), n =>
            {
                Instruction value = Definition(normal.Args[2 + 2 * n]);
                return value.Op == 197 && value.Args.Skip(2).Any(id =>
                    Definition(id) is { Op: 199 } mask && constants.GetValueOrDefault(mask.Args[3]) == 0x7fffffu);
            });
            foreach ((string operand, uint replacement) in new[] { ("left", left.Args[1]), ("right", right.Args[1]) })
            {
                Corrupt(name + "-forward-" + operand, parent, PairIndex(parent, output.Label), replacement);
                Corrupt(name + "-helper-return-" + operand, output, PairIndex(output, normal.Label), replacement);
                Corrupt(name + "-packed-return-" + operand, normal, packedIndex, replacement);
            }
            if (name == "Add")
            {
                // Both operands dominate both early exits, so this is a valid SSA edge association error.
                int a = Assert.Single(Enumerable.Range(0, 5), n => output.Args[2 + 2 * n] == left.Args[1]);
                int b = Assert.Single(Enumerable.Range(0, 5), n => output.Args[2 + 2 * n] == right.Args[1]);
                Mutate("Add-swapped-early-edges", output, [(a, right.Args[1]), (b, left.Args[1])]);
            }
        }
        Assert.Equal(19, outcomes.Count);
        File.WriteAllText(Path.Combine(evidence, "results.json"), JsonSerializer.Serialize(outcomes,
            new JsonSerializerOptions { WriteIndented = true }));

        void Corrupt(string name, Instruction phi, int pair, uint replacement) =>
            Mutate(name, phi, [(pair, replacement)]);

        void Mutate(string name, Instruction phi, (int Pair, uint Value)[] changes)
        {
            uint[] corrupted = (uint[])words.Clone();
            string changedHlsl = hlsl;
            string changedBody = body;
            foreach (var change in changes)
            {
                uint old = phi.Args[2 + 2 * change.Pair];
                Assert.NotEqual(old, change.Value);
                Assert.Equal(phi.Args[0], Definition(change.Value).Args[0]);
                Assert.Equal(operandMerge, Definition(change.Value).Label);
                string assignment = "_" + phi.Args[1] + " = _" + old + ";";
                if (!hlsl.Contains(assignment, StringComparison.Ordinal))
                {
                    // SPIRV-Cross folds the final bit pack into the return assignment.
                    Assert.Equal(197u, Definition(old).Op);
                    assignment = Assert.Single(Regex.Matches(hlsl, @"\b_" + phi.Args[1]
                        + @" = [^;\r\n]+;").Cast<Match>(), m => m.Value.Contains("& 8388607u", StringComparison.Ordinal)).Value;
                }
                string replacement = "_" + phi.Args[1] + " = MUTATED_" + change.Value + ";";
                Assert.Single(Regex.Matches(hlsl, Regex.Escape(assignment)));
                Assert.Contains(assignment, body, StringComparison.Ordinal);
                changedHlsl = changedHlsl.Replace(assignment, replacement, StringComparison.Ordinal);
                changedBody = changedBody.Replace(assignment, replacement, StringComparison.Ordinal);
                corrupted[phi.Offset + 3 + 2 * change.Pair] = change.Value;
            }
            changedHlsl = changedHlsl.Replace("MUTATED_", "_", StringComparison.Ordinal);
            changedBody = changedBody.Replace("MUTATED_", "_", StringComparison.Ordinal);
            byte[] bytes = MemoryMarshal.AsBytes(corrupted.AsSpan()).ToArray();
            File.WriteAllBytes(Path.Combine(evidence, name + ".spv"), bytes);
            File.WriteAllText(Path.Combine(evidence, name + ".hlsl"), changedHlsl);
            Exception failure = Record.Exception(() => WaterOpticsInterpolationEmissionContract.Verify(
                bytes, changedHlsl, changedBody, header));
            Assert.True(failure is Xunit.Sdk.XunitException, name + " was accepted by the result-routing guard.");
            Assert.Contains(nameof(WaterOpticsInterpolationTraceContract), failure.StackTrace ?? "", StringComparison.Ordinal);
            outcomes.Add(new
            {
                name,
                Phi = phi.Args[1],
                phi.Label,
                Changes = changes.Select(c => new
                {
                    Word = phi.Offset + 3 + 2 * c.Pair,
                    Old = phi.Args[2 + 2 * c.Pair],
                    Replacement = c.Value,
                    Predecessor = phi.Args[3 + 2 * c.Pair],
                    Type = phi.Args[0]
                }),
                Rejection = failure.Message
            });
        }

        uint Merge(uint block) => Assert.Single(code, i => i.Op == 247 && i.Label == block).Args[0];
        Instruction Definition(uint id) => Assert.Single(code, i => i.Args.Length >= 2
            && i.Op is not (247 or 248 or 249 or 250 or 251) && i.Args[1] == id);
        static IEnumerable<(uint Value, uint Predecessor)> Inputs(Instruction phi) =>
            Enumerable.Range(0, (phi.Args.Length - 2) / 2).Select(n => (phi.Args[2 + 2 * n], phi.Args[3 + 2 * n]));
        static uint Input(Instruction phi, uint predecessor) => Assert.Single(Inputs(phi), p => p.Predecessor == predecessor).Value;
        static int PairIndex(Instruction phi, uint predecessor) => Assert.Single(
            Enumerable.Range(0, (phi.Args.Length - 2) / 2), n => phi.Args[3 + 2 * n] == predecessor);
    }

    sealed record Instruction(int Offset, uint Op, uint[] Args, uint Label);
}
