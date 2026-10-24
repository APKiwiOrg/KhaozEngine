using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class WaterOpticsRolledEmissionTests
{
    const string Evidence = "/tmp/khaozengine-water-triangles/shared-predicate-mapping/emission";

    // Catch loss of sharing or local loop controls in the actual pinned compiler output.
    [Fact]
    public void ProductionRetainsSharedArithmeticMappingAndPredicateBodies()
    {
        Directory.CreateDirectory(Evidence);
        byte[] vertex = SpirvFrontEnd.ToSpirv(ShaderSources.WaterVert, GpuShaderStages.Vertex, "Water");
        byte[] fragment = SpirvFrontEnd.ToSpirv(ShaderSources.WaterFrag, GpuShaderStages.Fragment, "Water");
        string hlsl = SpirvCrossCompile.VertexFragmentToHlsl(vertex, fragment, "Water").FragmentSource;
        string msl = SpirvCrossCompile.VertexFragmentToMsl(vertex, fragment, "Water").FragmentSource;
        File.WriteAllBytes(Path.Combine(Evidence, "water.frag.spv"), fragment);
        File.WriteAllText(Path.Combine(Evidence, "water.frag.hlsl"), hlsl);
        File.WriteAllText(Path.Combine(Evidence, "water.frag.msl"), msl);
        Loop[] loops = HlslLoops(hlsl);
        var opcodes = new Dictionary<uint, int>();
        var controls = new List<uint>();
        var constants = new Dictionary<uint, uint>();
        var lessBounds = new Dictionary<uint, uint>();
        var retainedBounds = new List<uint>();
        uint? loopControl = null;
        Assert.Equal(0x07230203u, Word(0));
        int offset = 20;
        while (offset < fragment.Length)
        {
            uint header = Word(offset), opcode = header & 65535u;
            int words = checked((int)(header >> 16));
            Assert.True(words > 0 && offset + 4 * words <= fragment.Length);
            opcodes[opcode] = opcodes.GetValueOrDefault(opcode) + 1;
            if (opcode == 43 && words == 4) constants[Word(offset + 8)] = Word(offset + 12); // OpConstant.
            if (opcode == 177 && constants.TryGetValue(Word(offset + 16), out uint bound)) // OpSLessThan.
                lessBounds[Word(offset + 8)] = bound;
            if (opcode == 246)
            {
                loopControl = Word(offset + 12); // OpLoopMerge control operand.
                controls.Add(loopControl.Value);
            }
            if (opcode == 250) // OpBranchConditional at the loop header.
            {
                if (loopControl == 2 && lessBounds.TryGetValue(Word(offset + 4), out uint limit))
                    retainedBounds.Add(limit);
                loopControl = null;
            }
            if (opcode == 249) loopControl = null; // OpBranch.
            offset += 4 * words;
        }
        Assert.Equal(fragment.Length, offset);
        File.WriteAllText(Path.Combine(Evidence, "structure.json"), JsonSerializer.Serialize(new
        {
            SpirvFrontEndPin.Identity,
            HlslPin = HlslCrossCompilePin.Identity,
            Spirv = new
            {
                Bytes = fragment.Length,
                Sha256 = Hash(fragment),
                Instructions = opcodes.Values.Sum(),
                Functions = opcodes.GetValueOrDefault(54u),
                Calls = opcodes.GetValueOrDefault(57u),
                LoopControls = controls,
                RetainedBounds = retainedBounds
            },
            Hlsl = new
            {
                Bytes = Encoding.UTF8.GetByteCount(hlsl),
                Sha256 = Hash(Encoding.UTF8.GetBytes(hlsl)),
                Lines = hlsl.Count(c => c == '\n'),
                WideArrays = Regex.Matches(hlsl, @"\buint\s+\w+\[18\]").Count,
                Loops = loops
            },
            Msl = new { Bytes = Encoding.UTF8.GetByteCount(msl), Sha256 = Hash(Encoding.UTF8.GetBytes(msl)) }
        }, new JsonSerializerOptions { WriteIndented = true }));

        Assert.Equal(1, opcodes.GetValueOrDefault(54u)); // Exhaustively inlined OpFunction.
        Assert.Equal(0, opcodes.GetValueOrDefault(57u)); // No surviving OpFunctionCall boundary.
        Assert.True(loops.Count(l => l.Bound == 6 && l.Retained) == 2
            && loops.Count(l => l.Bound == 4 && l.Retained) == 1
            && loops.Count(l => l.Bound == 2 && l.Retained) == 1,
            "Expected two retained six-term predicates, one four-step bin mapping and one two-axis origin loop. "
            + "Actual retained bounds: " + string.Join(",", loops.Where(l => l.Retained).Select(l => l.Bound)));
        Assert.Equal(new uint[] { 2, 3, 4, 6, 6, 18, 18, 18, 18, 18, 18, 23, 27, 32 },
            retainedBounds.Order().ToArray());
        Loop origin = Assert.Single(loops, l => l.Bound == 2 && l.Retained);
        Assert.Single(Regex.Matches(hlsl[origin.Start..origin.End], @"& 134217728u"));
        Loop mapping = Assert.Single(loops, l => l.Bound == 4 && l.Retained);
        Assert.Single(Regex.Matches(hlsl[mapping.Start..mapping.End], @"& 32768u"));
        Assert.True(origin.End < mapping.Start);
        Assert.Single(retainedBounds, b => b == 23);
        Assert.Single(retainedBounds, b => b == 27);
        Loop interpolation = Assert.Single(loops, l => l.Bound == 23);
        Assert.True(interpolation.Retained, "Interpolation schedule lost its [loop] control.");
        string body = hlsl[interpolation.Start..interpolation.End];
        // These distinguish the unchanged add normalization and 24-bit multiply bodies after inlining.
        Assert.Single(Regex.Matches(body, @"& 134217728u"));
        Assert.Single(Regex.Matches(body, @"& 32768u"));
        Loop division = Assert.Single(loops, l => l.Bound == 27 && l.Retained);
        Assert.InRange(division.Start, interpolation.Start + 1, interpolation.End - 1);
        Assert.True(division.End < interpolation.End);
        Loop[] carries = loops.Where(l => l.Bound == 18 && l.Retained).ToArray();
        Assert.Equal(6, carries.Length);
        Loop product = Assert.Single(loops, l => l.Bound == 6
            && carries.All(c => c.Start > l.Start && c.End < l.End));
        Assert.True(product.Retained, "Wide six-term product loop lost its [loop] control.");
        Loop narrow = Assert.Single(loops, l => l.Bound == 6 && l.Start != product.Start);
        Assert.True(narrow.Retained, "Narrow six-term product loop lost its [loop] control.");
        // One pair of low-half significands identifies one exact 24-by-24 product body.
        Assert.Equal(2, Regex.Matches(hlsl[narrow.Start..narrow.End], @"& 65535u").Count);
        Assert.Single(loops, l => l.Bound == 18 && !l.Retained); // Unhinted accumulator initialization.
        Assert.All(carries, l =>
        {
            Assert.True(l.Retained);
            Assert.InRange(l.Start, product.Start + 1, product.End - 1);
            Assert.True(l.End < product.End);
        });
        foreach ((int bound, int count) in new[] { (32, 1), (3, 1) })
        {
            Loop[] selected = loops.Where(l => l.Bound == bound && l.Retained).ToArray();
            Assert.Equal(count, selected.Length);
            Assert.All(selected, l => Assert.True(l.Retained));
        }
        Assert.Equal(14, loops.Count(l => l.Retained));
        Assert.Equal(14, controls.Count(c => c == 2)); // SPIR-V DontUnroll.
        Assert.All(controls, c => Assert.True(c is 0 or 2));
        uint Word(int at) => BinaryPrimitives.ReadUInt32LittleEndian(fragment.AsSpan(at, 4));
    }

    static Loop[] HlslLoops(string hlsl)
    {
        var loops = new List<Loop>();
        foreach (Match match in Regex.Matches(hlsl, @"(?:(\[loop\])\s*)?for\s*\(([^)]*)\)\s*\{"))
        {
            int depth = 1, end = match.Index + match.Length;
            while (depth > 0 && end < hlsl.Length)
            {
                if (hlsl[end] == '{') depth++;
                if (hlsl[end] == '}') depth--;
                end++;
            }
            Assert.Equal(0, depth);
            Match bound = Regex.Match(match.Groups[2].Value, @"<\s*(\d+)(?:u)?\s*;");
            // Early-exit loops are emitted as for (;;) with their bound as the first body condition.
            if (!bound.Success)
                bound = Regex.Match(hlsl[(match.Index + match.Length)..end], @"^\s*if\s*\(\w+\s*<\s*(\d+)\)");
            loops.Add(new(match.Index, end, match.Groups[2].Value, match.Groups[1].Success,
                bound.Success ? int.Parse(bound.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null));
        }
        return loops.ToArray();
    }

    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    sealed record Loop(int Start, int End, string Header, bool Retained, int? Bound);
}
