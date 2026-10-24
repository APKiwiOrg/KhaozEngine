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
    const string Evidence = "/tmp/khaozengine-water-triangles/rolled-wide/emission";

    // Catch loss of sharing or local loop controls in the actual pinned compiler output.
    [Fact]
    public void ProductionWideProductsRetainOneSixTermBodyAndSixCarryBodies()
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
        Assert.Equal(0x07230203u, Word(0));
        int offset = 20;
        while (offset < fragment.Length)
        {
            uint header = Word(offset), opcode = header & 65535u;
            int words = checked((int)(header >> 16));
            Assert.True(words > 0 && offset + 4 * words <= fragment.Length);
            opcodes[opcode] = opcodes.GetValueOrDefault(opcode) + 1;
            if (opcode == 246) controls.Add(Word(offset + 12)); // OpLoopMerge control operand.
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
                LoopControls = controls
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

        Loop product = Assert.Single(loops, l => l.Bound == 6);
        Assert.True(product.Retained, "Six-term product loop lost its [loop] control.");
        Loop[] carries = loops.Where(l => l.Bound == 18 && l.Retained).ToArray();
        Assert.Equal(6, carries.Length);
        Assert.Single(loops, l => l.Bound == 18 && !l.Retained); // Unhinted accumulator initialization.
        Assert.All(carries, l =>
        {
            Assert.True(l.Retained);
            Assert.InRange(l.Start, product.Start + 1, product.End - 1);
            Assert.True(l.End < product.End);
        });
        foreach ((int bound, int count) in new[] { (32, 1), (3, 1), (27, 2) })
        {
            Loop[] selected = loops.Where(l => l.Bound == bound && l.Retained).ToArray();
            Assert.Equal(count, selected.Length);
            Assert.All(selected, l => Assert.True(l.Retained));
        }
        Assert.Equal(11, loops.Count(l => l.Retained));
        Assert.Equal(11, controls.Count(c => c == 2)); // SPIR-V DontUnroll.
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
