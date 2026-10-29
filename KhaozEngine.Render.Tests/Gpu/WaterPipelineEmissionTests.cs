using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class WaterPipelineEmissionTests
{
    const string Evidence = "/tmp/khaozengine-water-triangles/lazy-pipelines/emission";

    [Fact]
    public void ActualDefaultEmissionExcludesExactIntegerOpticsAndAllFourProgramsKeepTheirBindings()
    {
        Directory.CreateDirectory(Evidence);
        var hashes = new Dictionary<string, Dictionary<string, string>>();
        var shapes = new List<object>();
        string[] names = ["Water", "WaterClipmap", "WaterTriangle", "WaterClipmapTriangle"];
        var programs = ShippedShaderPrograms.GraphicsPrograms().Where(p => names.Contains(p.Name)).ToArray();
        Assert.Equal(4, programs.Length);
        foreach (ShippedGraphicsProgram program in programs)
        {
            bool triangle = program.Name.EndsWith("Triangle", StringComparison.Ordinal);
            byte[] vertex = SpirvFrontEnd.ToSpirv(program.VertexGlsl, GpuShaderStages.Vertex, program.Name);
            byte[] fragment = SpirvFrontEnd.ToSpirv(program.FragmentGlsl, GpuShaderStages.Fragment, program.Name);
            CrossCompiledPair hlsl = SpirvCrossCompile.VertexFragmentToHlsl(vertex, fragment, program.Name);
            CrossCompiledPair msl = SpirvCrossCompile.VertexFragmentToMsl(vertex, fragment, program.Name);
            int xors = 0, shifts = 0;
            for (int offset = 20; offset < fragment.Length;)
            {
                uint header = BinaryPrimitives.ReadUInt32LittleEndian(fragment.AsSpan(offset));
                int words = (int)(header >> 16);
                Assert.True(words > 0 && offset + words * 4 <= fragment.Length);
                uint op = header & 65535;
                if (op == 198) xors++;
                if (op is 194 or 195 or 196) shifts++;
                offset += words * 4;
            }
            if (triangle)
            {
                Assert.True(xors > 0 && shifts > 0);
                Assert.Contains("asuint(", hlsl.FragmentSource);
                AssertTriangleModeIsolation(program.FragmentGlsl);
            }
            else
            {
                Assert.Equal(0, xors);
                Assert.Equal(0, shifts);
                Assert.DoesNotContain("asuint(", hlsl.FragmentSource);
                Assert.DoesNotContain("opticsAdd(", program.FragmentGlsl);
                Assert.DoesNotContain("tryOpticsTriangle(", program.FragmentGlsl);
            }
            Assert.Equal(17, hlsl.Reflection.ResourceLayouts[0].Elements.Length);
            Assert.Contains("register(t4)", hlsl.FragmentSource);
            Assert.Contains("register(s4)", hlsl.FragmentSource);
            foreach (string uniform in new[] { "vec4 OpticsRect;", "vec4 OpticsParams;", "vec4 SkyDiscHalo[8];" })
            {
                Assert.Contains(uniform, program.VertexGlsl);
                Assert.Contains(uniform, program.FragmentGlsl);
            }
            hashes[program.Name] = new()
            {
                ["spirv.vertex"] = Hash(vertex),
                ["spirv.fragment"] = Hash(fragment),
                ["hlsl.vertex"] = Hash(Encoding.UTF8.GetBytes(hlsl.VertexSource)),
                ["hlsl.fragment"] = Hash(Encoding.UTF8.GetBytes(hlsl.FragmentSource)),
                ["msl.vertex"] = Hash(Encoding.UTF8.GetBytes(msl.VertexSource)),
                ["msl.fragment"] = Hash(Encoding.UTF8.GetBytes(msl.FragmentSource))
            };
            File.WriteAllBytes(Path.Combine(Evidence, program.Name + ".frag.spv"), fragment);
            File.WriteAllText(Path.Combine(Evidence, program.Name + ".frag.glsl"), program.FragmentGlsl);
            File.WriteAllText(Path.Combine(Evidence, program.Name + ".frag.hlsl"), hlsl.FragmentSource);
            shapes.Add(new { program.Name, Bytes = fragment.Length, Xors = xors, Shifts = shifts });
        }
        File.WriteAllText(Path.Combine(Evidence, "hashes.json"), JsonSerializer.Serialize(hashes, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(Evidence, "isolation.json"), JsonSerializer.Serialize(shapes, new JsonSerializerOptions { WriteIndented = true }));
    }

    static void AssertTriangleModeIsolation(string fragment)
    {
        // Triangle specialization must always query the exact bed, without a runtime legacy/bilinear fallback.
        string opticalDepth = Function(fragment, "float opticalBedDepth(");
        Assert.DoesNotContain("OpticsParams.y", opticalDepth);
        Assert.DoesNotContain("return legacyDepth", opticalDepth);
        // M5 only caps invalid unbound scene depth. Mode 2 never takes this branch or changes its exact bed.
        string columnDepth = Function(fragment, "float waterColumnDepth(");
        Assert.Equal("""
            float waterColumnDepth(float rawDepth, float bedDepth) {
                float meanSigma = (ColumnExtinction.r + ColumnExtinction.g + ColumnExtinction.b) / 3.0;
                float limit = 8.0 * (3.0 / meanSigma);
                if (OpticsParams.y < 0.5 && (isnan(rawDepth) || isinf(rawDepth) || rawDepth < 0.0 || rawDepth >= 1.0))
                    return min(max(bedDepth, 0.0), limit);
                return max(bedDepth, 0.0);
            }
            """, columnDepth);
        // No other helper or shading branch may reintroduce runtime optical-mode selection.
        Assert.DoesNotContain("OpticsParams.y", fragment.Replace(columnDepth, "", StringComparison.Ordinal));
    }

    static string Function(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing emitted function {signature}");
        int body = source.IndexOf('{', start);
        Assert.True(body >= 0, $"Missing emitted function body {signature}");
        int depth = 0;
        for (int i = body; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }
        throw new InvalidOperationException($"Unclosed emitted function {signature}");
    }

    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
