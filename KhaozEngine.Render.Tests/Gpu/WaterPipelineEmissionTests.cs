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
                Assert.DoesNotContain("if (OpticsParams.y", program.FragmentGlsl);
            }
            else
            {
                Assert.Equal(0, xors);
                Assert.Equal(0, shifts);
                Assert.DoesNotContain("asuint(", hlsl.FragmentSource);
                Assert.DoesNotContain("opticsAdd(", program.FragmentGlsl);
                Assert.DoesNotContain("tryOpticsTriangle(", program.FragmentGlsl);
            }
            Assert.Equal(12, hlsl.Reflection.ResourceLayouts[0].Elements.Length);
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

    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
