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
    const string Evidence = "/tmp/khaozengine-water-triangles/destination-selects/emission/triangle-structure";

    // Catch loss of sharing or local loop controls in the actual pinned compiler output.
    [Fact]
    public void ProductionRetainsSharedArithmeticMappingAndPredicateBodies()
    {
        Directory.CreateDirectory(Evidence);
        byte[] vertex = SpirvFrontEnd.ToSpirv(ShaderSources.WaterVert, GpuShaderStages.Vertex, "WaterTriangle");
        byte[] fragment = SpirvFrontEnd.ToSpirv(ShaderSources.WaterTriangleFrag, GpuShaderStages.Fragment, "WaterTriangle");
        string hlsl = SpirvCrossCompile.VertexFragmentToHlsl(vertex, fragment, "WaterTriangle").FragmentSource;
        string msl = SpirvCrossCompile.VertexFragmentToMsl(vertex, fragment, "WaterTriangle").FragmentSource;
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
            if (opcode is 176 or 177 && constants.TryGetValue(Word(offset + 16), out uint bound)) // OpULessThan or OpSLessThan.
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

        Assert.DoesNotMatch(@"\buint\s+\w+\[18\]", hlsl);
        Assert.DoesNotMatch(@"\buint\s+\w+\[16\]", hlsl);
        Assert.Equal(1, opcodes.GetValueOrDefault(54u)); // Exhaustively inlined OpFunction.
        Assert.Equal(0, opcodes.GetValueOrDefault(57u)); // No surviving OpFunctionCall boundary.
        Assert.True(loops.Count(l => l.Bound == 6 && l.Retained) == 3
            && loops.Count(l => l.Bound == 4 && l.Retained) == 1
            && loops.Count(l => l.Bound == 2 && l.Retained) == 1,
            "Expected three retained six-term bodies, one four-step bin mapping and one two-axis origin loop. "
            + "Actual retained bounds: " + string.Join(",", loops.Where(l => l.Retained).Select(l => l.Bound)));
        Assert.Equal(new uint[] { 2, 3, 4, 6, 6, 6, 18, 23, 27, 32 },
            retainedBounds.Order().ToArray());
        Loop origin = Assert.Single(loops, l => l.Bound == 2 && l.Retained);
        string originBody = hlsl[origin.Start..origin.End];
        Assert.Single(Regex.Matches(originBody, @"& 134217728u"));
        string originAxis = Regex.Match(origin.Header, @"int\s+(\w+)\s*=").Groups[1].Value;
        Assert.NotEmpty(originAxis);
        // Keep origin vector accesses static while retaining the loop for FXC.
        Assert.DoesNotMatch(@"\[[^\]]*\b" + Regex.Escape(originAxis) + @"\b[^\]]*\]", originBody);
        string axisZero = Regex.Match(originBody, @"bool (\w+) = " + Regex.Escape(originAxis) + @" == 0;")
            .Groups[1].Value;
        Assert.NotEmpty(axisZero);
        string selection = @"if \(" + Regex.Escape(axisZero) + @"\)\s*\{\s*(\w+) = asuint\((\w+)\.x\);\s*\}"
            + @"\s*else\s*\{\s*\1 = asuint\(\2\.([zy])\);\s*\}";
        Assert.Equal(new[] { "z", "y" }, Regex.Matches(originBody, selection)
            .Select(m => m.Groups[3].Value).ToArray());
        Assert.Single(Regex.Matches(originBody, @"if \(" + Regex.Escape(axisZero)
            + @"\)\s*\{\s*uint2 (\w+) = (\w+);\s*\1\.x = (\w+);\s*(\w+) = \1;\s*\}"
            + @"\s*else\s*\{\s*uint2 (\w+) = \2;\s*\5\.y = \3;\s*\4 = \5;\s*\}"));
        Loop mapping = Assert.Single(loops, l => l.Bound == 4 && l.Retained);
        Assert.Single(Regex.Matches(hlsl[mapping.Start..mapping.End], @"& 32768u"));
        Assert.True(origin.End < mapping.Start);
        Assert.Single(retainedBounds, b => b == 23);
        Assert.Single(retainedBounds, b => b == 27);
        Loop interpolation = Assert.Single(loops, l => l.Bound == 23);
        Assert.True(interpolation.Retained, "Interpolation schedule lost its [loop] control.");
        string body = hlsl[interpolation.Start..interpolation.End];
        WaterOpticsInterpolationEmissionContract.Verify(fragment, hlsl, body, interpolation.Header);
        WaterOpticsInterpolationEmissionContract.VerifyRejectsCorruptedRoutes(fragment, hlsl, body, interpolation.Header);
        WaterOpticsInterpolationResultMutations.Verify(fragment, hlsl, body, interpolation.Header);
        // These distinguish the unchanged add normalization and 24-bit multiply bodies after inlining.
        Assert.Single(Regex.Matches(body, @"& 134217728u"));
        Assert.Single(Regex.Matches(body, @"& 32768u"));
        Loop division = Assert.Single(loops, l => l.Bound == 27 && l.Retained);
        Assert.InRange(division.Start, interpolation.Start + 1, interpolation.End - 1);
        Assert.True(division.End < interpolation.End);
        Loop scan = Assert.Single(loops, l => l.Bound == 18);
        Assert.True(scan.Retained);
        Loop contribution = Assert.Single(loops, l => l.Bound == 6 && l.Start > scan.Start && l.End < scan.End);
        Loop product = Assert.Single(loops, l => l.Bound == 6 && l.End < scan.Start
            && Regex.IsMatch(hlsl[l.Start..l.End], @"\w+\[\w+\] = uint4\("));
        Loop narrow = Assert.Single(loops, l => l.Bound == 6 && l.Start != product.Start && l.Start != contribution.Start);
        foreach (Loop exactProduct in new[] { product, narrow })
        {
            string exactBody = hlsl[exactProduct.Start..exactProduct.End];
            Assert.Equal(2, Regex.Matches(exactBody, @"& 65535u").Count);
            Assert.Equal(4, Regex.Matches(exactBody, @"\*").Count); // Four partial multiplies, one product body.
            Assert.Equal(exactProduct == product ? 1 : 5, Regex.Matches(exactBody, @"uint\(\w+ < \w+\)").Count);
        }
        string preprocessing = hlsl[product.Start..product.End];
        Assert.Single(Regex.Matches(preprocessing, @"\w+\[\w+\]")); // Only the local cache store is indexed.
        Match safeSpill = Regex.Match(preprocessing, @"uint (\w+) = \(32u - (\w+)\) & 31u;");
        Assert.True(safeSpill.Success);
        Assert.Equal(2, Regex.Matches(preprocessing, @">> " + Regex.Escape(safeSpill.Groups[1].Value)).Count);
        Assert.Contains("== 0u", preprocessing, StringComparison.Ordinal);
        Match bank = Assert.Single(Regex.Matches(hlsl, @"uint4 (\w+)\[6\];"));
        string cache = Regex.Escape(bank.Groups[1].Value);
        Assert.Equal(2, Regex.Matches(hlsl, @"\b" + cache + @"\b").Count(m =>
            m.Index < contribution.Start)); // Declaration and single preprocessing store.
        Assert.Single(Regex.Matches(hlsl, cache + @"\[[^\]]+\] = uint4\("));
        Assert.DoesNotMatch(@"=\s*" + cache + @"\s*[;,]", hlsl); // No whole-bank values or copy chains.
        string visits = hlsl[contribution.Start..contribution.End];
        Assert.DoesNotMatch(@"\*|uint4|\]\[|\bbreak\b|\breturn\b", visits);
        Assert.Equal(new[] { "w", "x", "y", "z", "w" }, Regex.Matches(visits, cache + @"\[\w+\]\.([xyzw])")
            .Select(m => m.Groups[1].Value).ToArray());
        Assert.Equal(2, Regex.Matches(visits, @"uint\(\w+ < \w+\)").Count); // Scalar unsigned overflow counts.
        string scanBody = hlsl[scan.Start..scan.End];
        Assert.DoesNotMatch(@"\bbreak\b|\breturn\b|\[18\]", scanBody);
        Assert.Single(Regex.Matches(scanBody, @"\(\w+ > \w+\) \? 1 : \(-1\)"));
        Assert.True(narrow.End < product.Start && product.End < scan.Start);
        WaterOpticsStreamedEmissionContract.Verify(fragment);
        foreach ((int bound, int count) in new[] { (32, 1), (3, 1) })
        {
            Loop[] selected = loops.Where(l => l.Bound == bound && l.Retained).ToArray();
            Assert.Equal(count, selected.Length);
            Assert.All(selected, l => Assert.True(l.Retained));
        }
        Assert.Equal(10, loops.Count(l => l.Retained));
        Assert.Equal(10, controls.Count(c => c == 2)); // SPIR-V DontUnroll.
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
