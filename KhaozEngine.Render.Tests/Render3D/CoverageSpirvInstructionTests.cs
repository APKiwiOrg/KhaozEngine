using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageSpirvInstructionTests
{
    [Theory]
    [InlineData(182u, 0x7fc00000u, 0x3f800000u, 0u)]
    [InlineData(183u, 0x7fc00000u, 0x3f800000u, 1u)]
    [InlineData(182u, 0x3f800000u, 0x40000000u, 1u)]
    [InlineData(183u, 0x3f800000u, 0x3f800000u, 0u)]
    public void Floating_not_equal_respects_ordered_and_unordered_NaN(uint opcode, uint a, uint b, uint expected)
        => Assert.Equal(expected, Execute(opcode, a, b, floating: true));

    [Theory]
    [InlineData(138u, -5, 3, -2)]
    [InlineData(139u, -5, 3, 1)]
    [InlineData(138u, 5, -3, 2)]
    [InlineData(139u, 5, -3, -1)]
    [InlineData(139u, -5, -3, -2)]
    public void Signed_remainder_and_modulo_keep_their_distinct_sign_rules(uint opcode, int a, int b, int expected)
        => Assert.Equal(unchecked((uint)expected), Execute(opcode, unchecked((uint)a), unchecked((uint)b), floating: false));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_vector_instructions_execute_and_unknown_extended_instructions_refuse(bool unsupported)
    {
        const string source = """
            #version 450
            layout(local_size_x = 64) in;
            layout(set=0,binding=0) readonly buffer InputBuf { uint Inputs[]; };
            layout(set=0,binding=1) buffer OutputBuf { uint Outputs[]; };
            void main() {
                if (gl_GlobalInvocationID.x != 0u) return;
                vec3 a = vec3(uintBitsToFloat(Inputs[0]),uintBitsToFloat(Inputs[1]),uintBitsToFloat(Inputs[2]));
                vec3 b = vec3(uintBitsToFloat(Inputs[3]),uintBitsToFloat(Inputs[4]),uintBitsToFloat(Inputs[5]));
                vec3 c = cross(a,b), d = a*uintBitsToFloat(Inputs[1]);
                Outputs[0] = floatBitsToUint(dot(a,b));
                Outputs[1] = floatBitsToUint(c.x); Outputs[2] = floatBitsToUint(c.y); Outputs[3] = floatBitsToUint(c.z);
                Outputs[4] = floatBitsToUint(d.x); Outputs[5] = floatBitsToUint(d.y); Outputs[6] = floatBitsToUint(d.z);
            }
            """;
        byte[] module = SpirvFrontEnd.ToSpirv(source, GpuShaderStages.Compute, "native vector instructions",
            new GpuShaderCompileOptions(PreciseMath: true));
        Span<uint> words = MemoryMarshal.Cast<byte, uint>(module.AsSpan());
        int crosses = 0;
        for (int at = 5; at < words.Length; at += (int)(words[at] >> 16))
            if ((words[at] & 65535) == 12 && words[at + 4] == 68)
            { crosses++; if (unsupported) words[at + 4] = uint.MaxValue; }
        Assert.Equal(1, crosses);
        uint[] input = [0x3f800000, 0x40000000, 0x40400000, 0x40800000, 0x40a00000, 0x40c00000];
        uint[] result = new uint[7];
        var executor = new CoverageSpirvExecutor(module, new[] { input, result });
        if (unsupported) Assert.Throws<NotSupportedException>(() => executor.Run(1));
        else
        {
            executor.Run(1);
            Assert.Equal(new uint[] { 0x42000000, 0xc0400000, 0x40c00000, 0xc0400000,
                0x40000000, 0x40800000, 0x40c00000 }, result);
        }
    }

    static uint Execute(uint opcode, uint a, uint b, bool floating)
    {
        string expression = floating
            ? "uint(uintBitsToFloat(Inputs[0]) != uintBitsToFloat(Inputs[1]))"
            : "uint(int(Inputs[0]) % int(Inputs[1]))";
        string source = """
            #version 450
            layout(local_size_x = 64) in;
            layout(set=0,binding=0) readonly buffer InputBuf { uint Inputs[]; };
            layout(set=0,binding=1) buffer OutputBuf { uint Outputs[]; };
            void main() { if (gl_GlobalInvocationID.x != 0u) return; Outputs[0] =
            """ + expression + "; }";
        byte[] module = SpirvFrontEnd.ToSpirv(source, GpuShaderStages.Compute, "instruction fixture",
            new GpuShaderCompileOptions(PreciseMath: true));
        Span<uint> words = MemoryMarshal.Cast<byte, uint>(module.AsSpan());
        int changed = 0;
        for (int at = 5; at < words.Length; at += (int)(words[at] >> 16))
        {
            uint op = words[at] & 65535;
            if (floating ? op is 182 or 183 : op is 138 or 139)
            { words[at] = (words[at] & 0xffff0000u) | opcode; changed++; }
        }
        Assert.Equal(1, changed);
        uint[] result = [0xdeadbeef];
        new CoverageSpirvExecutor(module, new uint[][] { [a, b], result }).Run(1);
        return result[0];
    }
}
