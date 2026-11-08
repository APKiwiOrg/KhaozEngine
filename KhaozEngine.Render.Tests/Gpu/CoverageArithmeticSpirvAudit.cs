using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

// Inspect the compiled helper bodies, rather than counting source qualifiers.
internal static class CoverageArithmeticSpirvAudit
{
    internal static int MissingSites(byte[] spirv, ITestOutputHelper output)
    {
        CoverageSpirvAccessAudit.Validate(spirv, "coverage arithmetic");
        uint[] words = MemoryMarshal.Cast<byte, uint>(spirv).ToArray();
        var names = new Dictionary<uint, string>();
        var decorated = new HashSet<uint>();
        var bodies = new Dictionary<uint, List<uint[]>>();
        uint function = 0;
        for (int offset = 5; offset < words.Length;)
        {
            int length = (int)(words[offset] >> 16);
            Assert.True(length > 0 && offset + length <= words.Length);
            uint[] instruction = words.AsSpan(offset, length).ToArray();
            uint opcode = instruction[0] & 0xffff;
            if (opcode == 5)
                names[instruction[1]] = Encoding.UTF8.GetString(MemoryMarshal.AsBytes(instruction.AsSpan(2))).TrimEnd('\0');
            if (opcode == 71 && instruction[2] == 42) decorated.Add(instruction[1]);
            if (opcode == 54) { function = instruction[2]; bodies.Add(function, new()); }
            if (function != 0) bodies[function].Add(instruction);
            if (opcode == 56) function = 0;
            offset += length;
        }
        uint Id(string name) => names.Single(pair => pair.Value.StartsWith(name + "(", StringComparison.Ordinal)).Key;
        uint flush = Id("covF");
        int missing = 0, arithmetic = 0;
        foreach (var body in bodies)
            foreach (uint[] instruction in body.Value.Where(i => (i[0] & 0xffff) is 129 or 131 or 133))
            {
                arithmetic++;
                if (!decorated.Contains(instruction[2])) missing++;
                output.WriteLine($"{names[body.Key]}: arithmetic opcode {instruction[0] & 0xffff}, %{instruction[2]}, NoContraction={decorated.Contains(instruction[2])}");
            }
        Assert.Equal(3, arithmetic);
        foreach ((string name, int calls) in new[] { ("covAdd", 3), ("covSub", 3), ("covMul", 3),
            ("covAbs", 2), ("covNeg", 2), ("covMin", 3), ("covMax", 3), ("covClamp", 4),
            ("covRead", 1), ("covStore", 1), ("covFinite", 1), ("covLess", 2), ("covLessOrEqual", 2),
            ("covGreater", 2), ("covGreaterOrEqual", 2), ("covEqual", 2), ("covNotEqual", 2) })
        {
            int actual = bodies[Id(name)].Count(i => (i[0] & 0xffff) == 57 && i[3] == flush);
            if (actual != calls) missing++;
            output.WriteLine($"{name}: {actual}/{calls} explicit F calls");
        }
        // F must use integer masking and an unsigned threshold comparison, with no float arithmetic.
        List<uint[]> f = bodies[flush];
        Assert.Equal(2, f.Count(i => (i[0] & 0xffff) == 199)); // OpBitwiseAnd
        Assert.Single(f, i => (i[0] & 0xffff) == 178); // OpULessThanEqual
        Assert.Contains(words, word => word == 0x00800000u);
        Assert.DoesNotContain(f, i => (i[0] & 0xffff) is 129 or 131 or 133);
        return missing;
    }
}
